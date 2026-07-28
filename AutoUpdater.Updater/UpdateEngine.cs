using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;

namespace AutoUpdater.Updater;

internal sealed class UpdateEngine(
    FileUpdateLogger log,
    UdpResultReporter reporter,
    IProgress<UpdateProgress>? progress = null)
{
    private static readonly string[] ProtectedDirectories = [".autoupdater", "AutoUpdater"];
    private static readonly HttpClient HttpClient = CreateHttpClient();

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("AutoUpdater.Updater/1.0");
        return client;
    }

    public async Task ExecuteAsync(
        UpdaterOptions options, CancellationToken cancellationToken = default)
    {
        Report(UpdateStage.Preparing, 3, "正在准备更新环境");
        Directory.CreateDirectory(options.TargetDirectory);
        Directory.CreateDirectory(options.WorkRoot);
        Directory.CreateDirectory(options.BackupRoot);

        var lockPath = Path.Combine(options.TargetDirectory, ".autoupdater", "update.lock");
        Directory.CreateDirectory(Path.GetDirectoryName(lockPath)!);
        await using var updateLock = new FileStream(
            lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

        await log.WriteAsync($"开始执行 {options.Operation}，RequestId={options.RequestId:N}");
        await WaitForHostExitAsync(options, cancellationToken);

        if (options.Operation == UpdateOperation.Rollback)
            await RollbackAsync(options, cancellationToken);
        else
            await InstallAsync(options, cancellationToken);
    }

    private async Task InstallAsync(UpdaterOptions options, CancellationToken cancellationToken)
    {
        Report(UpdateStage.ReadingManifest, 12, "正在读取更新清单", options.Source);
        var manifest = await UpdateManifest.LoadAsync(
            options.Source!, HttpClient, cancellationToken);
        await log.WriteAsync($"清单版本：{manifest.Version}");

        var packagePath = Path.Combine(options.WorkRoot, "package.zip");
        Report(UpdateStage.AcquiringPackage, 18, "正在获取更新包", manifest.Package);
        await AcquirePackageAsync(options.Source!, manifest.Package, packagePath, cancellationToken);
        Report(UpdateStage.Verifying, 42, "正在校验 SHA-256");
        await VerifySha256Async(packagePath, manifest.Sha256, cancellationToken);

        var stagingDirectory = Path.Combine(options.WorkRoot, "staging");
        Report(UpdateStage.Extracting, 50, "正在解压更新包");
        ExtractZipSafely(packagePath, stagingDirectory);
        EnsureExecutableAvailable(
            stagingDirectory, options.TargetDirectory, options.RestartExecutable);

        var installedVersionPath = Path.Combine(
            options.TargetDirectory, ".autoupdater", "installed-version.txt");
        var previousVersion = File.Exists(installedVersionPath)
            ? (await File.ReadAllTextAsync(installedVersionPath, cancellationToken)).Trim()
            : "unknown";
        var backupDirectory = Path.Combine(
            options.BackupRoot, $"{DateTime.Now:yyyyMMddHHmmss}_{SanitizeName(previousVersion)}");
        Report(UpdateStage.BackingUp, 62, "正在备份本次变更文件", previousVersion);
        await BackupChangedFilesAsync(
            stagingDirectory,
            options.TargetDirectory,
            backupDirectory,
            previousVersion,
            manifest.Version,
            cancellationToken);

        try
        {
            Report(UpdateStage.Installing, 78, "正在安装新版本", manifest.Version);
            await OverlayDirectoryAsync(stagingDirectory, options.TargetDirectory, cancellationToken);
            await WriteInstalledVersionAsync(options.TargetDirectory, manifest, cancellationToken);
            Report(UpdateStage.Restarting, 95, "正在重新启动上位机");
            Restart(options, options.RestartExecutable);
            await reporter.ReportAsync(
                true, $"更新到 {manifest.Version} 成功", manifest.Version);
            await log.WriteAsync($"更新到 {manifest.Version} 成功。");
        }
        catch
        {
            await log.WriteAsync("安装失败，正在恢复备份。");
            await RestoreBackupAsync(backupDirectory, options.TargetDirectory, cancellationToken);
            throw;
        }
    }

    private async Task RollbackAsync(UpdaterOptions options, CancellationToken cancellationToken)
    {
        Report(UpdateStage.BackingUp, 20, "正在查找版本备份");
        var backup = SelectBackup(options.BackupRoot, options.TargetVersion);
        await log.WriteAsync($"使用备份：{backup}");

        var safetyBackup = Path.Combine(
            options.BackupRoot, $"before_rollback_{DateTime.Now:yyyyMMddHHmmss}");
        Report(UpdateStage.BackingUp, 40, "正在备份回退前版本");
        var currentVersion = await ReadInstalledVersionAsync(
            options.TargetDirectory, cancellationToken);
        var incrementalSafetyBackup = await BackupRollbackAffectedFilesAsync(
            backup,
            options.TargetDirectory,
            safetyBackup,
            currentVersion,
            cancellationToken);
        await log.WriteAsync(incrementalSafetyBackup
            ? $"已创建回退前增量安全备份：{safetyBackup}"
            : $"目标是旧版完整备份，已创建兼容性完整安全备份：{safetyBackup}");
        try
        {
            Report(UpdateStage.Installing, 70, "正在恢复本次变更文件", Path.GetFileName(backup));
            var restoredVersion = await RestoreSelectedBackupAsync(
                backup, options.TargetDirectory, cancellationToken);
            if (!string.IsNullOrWhiteSpace(restoredVersion))
                await WriteInstalledVersionAsync(
                    options.TargetDirectory, restoredVersion, cancellationToken);
            Report(UpdateStage.Restarting, 95, "正在重新启动上位机");
            Restart(options, options.RestartExecutable);
            await reporter.ReportAsync(
                true,
                $"已回退到备份 {Path.GetFileName(backup)}",
                restoredVersion);
            TryDeleteDirectory(safetyBackup);
            await log.WriteAsync("版本回退成功。");
        }
        catch
        {
            await log.WriteAsync("回退失败，正在恢复回退前版本。");
            await RestoreSelectedBackupAsync(
                safetyBackup, options.TargetDirectory, CancellationToken.None);
            if (!string.IsNullOrWhiteSpace(currentVersion))
                await WriteInstalledVersionAsync(
                    options.TargetDirectory, currentVersion, CancellationToken.None);
            throw;
        }
    }

    private async Task WaitForHostExitAsync(
        UpdaterOptions options, CancellationToken cancellationToken)
    {
        if (options.ProcessId <= 0) return;
        Process? process;
        try
        {
            process = Process.GetProcessById(options.ProcessId);
        }
        catch (ArgumentException)
        {
            return;
        }

        Report(UpdateStage.WaitingForHost, 7, "正在等待上位机退出",
            $"进程 ID：{options.ProcessId}");
        await log.WriteAsync($"等待宿主进程 {options.ProcessId} 退出。");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.ProcessWaitTimeout);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"宿主进程 {options.ProcessId} 在规定时间内未退出。");
        }
    }

    private async Task AcquirePackageAsync(
        string manifestSource, string package, string destination,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        if (Uri.TryCreate(package, UriKind.Absolute, out var packageUri) &&
            packageUri.Scheme is "http" or "https")
        {
            await DownloadPackageAsync(packageUri, destination, cancellationToken);
            return;
        }

        if (Uri.TryCreate(manifestSource, UriKind.Absolute, out var manifestUri) &&
            manifestUri.Scheme is "http" or "https")
        {
            var resolvedUri = new Uri(manifestUri, package);
            await DownloadPackageAsync(resolvedUri, destination, cancellationToken);
            return;
        }

        var manifestDirectory = Path.GetDirectoryName(Path.GetFullPath(manifestSource));
        var sourcePath = Path.GetFullPath(
            Path.IsPathRooted(package) ? package : Path.Combine(manifestDirectory!, package));
        await using var input = new FileStream(
            sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        await using var output = new FileStream(
            destination, FileMode.Create, FileAccess.Write, FileShare.None);
        await CopyPackageWithProgressAsync(
            input, output, input.Length, "正在复制更新包", cancellationToken);
    }

    private async Task DownloadPackageAsync(
        Uri uri, string destination, CancellationToken cancellationToken)
    {
        using var response = await HttpClient.GetAsync(
            uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var output = new FileStream(
            destination, FileMode.Create, FileAccess.Write, FileShare.None);
        await CopyPackageWithProgressAsync(
            input, output, response.Content.Headers.ContentLength,
            "正在下载更新包", cancellationToken);
    }

    private async Task CopyPackageWithProgressAsync(
        Stream input,
        Stream output,
        long? totalLength,
        string message,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[128 * 1024];
        long copied = 0;
        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken);
            if (read == 0) break;
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            copied += read;
            var percentage = totalLength is > 0
                ? 18 + (int)Math.Min(22, copied * 22 / totalLength.Value)
                : 28;
            var detail = totalLength is > 0
                ? $"{FormatBytes(copied)} / {FormatBytes(totalLength.Value)}"
                : FormatBytes(copied);
            Report(UpdateStage.AcquiringPackage, percentage, message, detail);
        }
    }

    private static async Task VerifySha256Async(
        string path, string expected, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
        var normalized = expected.Replace("-", "", StringComparison.Ordinal).Trim();
        if (!actual.Equals(normalized, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"更新包 SHA-256 不匹配。期望 {normalized}，实际 {actual}。");
    }

    private static void ExtractZipSafely(string zipPath, string destination)
    {
        if (Directory.Exists(destination)) Directory.Delete(destination, recursive: true);
        Directory.CreateDirectory(destination);
        var root = Path.GetFullPath(destination) + Path.DirectorySeparatorChar;
        using var archive = ZipFile.OpenRead(zipPath);
        foreach (var entry in archive.Entries)
        {
            var path = Path.GetFullPath(Path.Combine(destination, entry.FullName));
            if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"ZIP 包含非法路径：{entry.FullName}");
            if (string.IsNullOrEmpty(entry.Name))
            {
                Directory.CreateDirectory(path);
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            entry.ExtractToFile(path, overwrite: true);
        }
    }

    private static void EnsureExecutableAvailable(
        string stagingRoot, string targetRoot, string executable)
    {
        var stagingPath = ResolveSafeRelativePath(stagingRoot, executable);
        var targetPath = ResolveSafeRelativePath(targetRoot, executable);
        if (!File.Exists(stagingPath) && !File.Exists(targetPath))
            throw new InvalidDataException(
                $"更新包和现有安装目录中均不存在启动文件：{executable}");
    }

    private static string ResolveSafeRelativePath(string root, string relative)
    {
        if (Path.IsPathRooted(relative))
            return Path.GetFullPath(relative);
        var rootPath = Path.GetFullPath(root) + Path.DirectorySeparatorChar;
        var resolved = Path.GetFullPath(Path.Combine(root, relative));
        if (!resolved.StartsWith(rootPath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("清单 executable 超出安装目录。");
        return resolved;
    }

    private static async Task BackupDirectoryAsync(
        string source, string destination, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(destination);
        await CopyDirectoryAsync(source, destination, cancellationToken,
            relative => !IsProtectedRelativePath(relative));
    }

    private static async Task<bool> BackupRollbackAffectedFilesAsync(
        string selectedBackup,
        string targetDirectory,
        string safetyBackup,
        string currentVersion,
        CancellationToken cancellationToken)
    {
        var selectedManifestPath = Path.Combine(
            selectedBackup, "rollback-manifest.json");
        if (!File.Exists(selectedManifestPath))
        {
            // Compatibility for backups produced before incremental rollback
            // manifests were introduced.
            await BackupDirectoryAsync(
                targetDirectory, safetyBackup, cancellationToken);
            return false;
        }

        var selectedManifest = await ReadRollbackManifestAsync(
            selectedManifestPath, cancellationToken);
        var safetyFilesDirectory = Path.Combine(safetyBackup, "files");
        Directory.CreateDirectory(safetyFilesDirectory);
        var safetyEntries = new List<RollbackFileEntry>();

        foreach (var selectedEntry in selectedManifest.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = selectedEntry.RelativePath.Replace(
                '/', Path.DirectorySeparatorChar);
            if (IsProtectedRelativePath(relative))
                throw new InvalidDataException(
                    $"回退清单包含受保护路径：{selectedEntry.RelativePath}");

            var targetFile = ResolveSafeRelativePath(targetDirectory, relative);
            var existedBeforeRollback = File.Exists(targetFile);
            safetyEntries.Add(new RollbackFileEntry(
                selectedEntry.RelativePath, existedBeforeRollback));
            if (!existedBeforeRollback) continue;

            var safetyFile = ResolveSafeRelativePath(
                safetyFilesDirectory, relative);
            await CopyFileAsync(targetFile, safetyFile, cancellationToken);
        }

        var safetyManifest = new RollbackManifest(
            currentVersion,
            currentVersion,
            DateTimeOffset.UtcNow,
            safetyEntries);
        await WriteRollbackManifestAsync(
            safetyBackup, safetyManifest, cancellationToken);
        return true;
    }

    private static async Task BackupChangedFilesAsync(
        string stagingDirectory,
        string targetDirectory,
        string backupDirectory,
        string previousVersion,
        string installedVersion,
        CancellationToken cancellationToken)
    {
        var filesDirectory = Path.Combine(backupDirectory, "files");
        Directory.CreateDirectory(filesDirectory);
        var entries = new List<RollbackFileEntry>();

        foreach (var stagedFile in Directory.EnumerateFiles(
                     stagingDirectory, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(stagingDirectory, stagedFile);
            if (IsProtectedRelativePath(relative)) continue;
            var targetFile = ResolveSafeRelativePath(targetDirectory, relative);
            var existedBefore = File.Exists(targetFile);
            entries.Add(new RollbackFileEntry(
                relative.Replace('\\', '/'), existedBefore));
            if (!existedBefore) continue;

            var backupFile = Path.Combine(filesDirectory, relative);
            await CopyFileAsync(targetFile, backupFile, cancellationToken);
        }

        var rollbackManifest = new RollbackManifest(
            previousVersion,
            installedVersion,
            DateTimeOffset.UtcNow,
            entries);
        var metadataPath = Path.Combine(backupDirectory, "rollback-manifest.json");
        var json = JsonSerializer.Serialize(rollbackManifest, new JsonSerializerOptions
        {
            WriteIndented = true
        });
        await File.WriteAllTextAsync(metadataPath, json, cancellationToken);
    }

    private static async Task<string?> RestoreSelectedBackupAsync(
        string backupDirectory,
        string targetDirectory,
        CancellationToken cancellationToken)
    {
        var metadataPath = Path.Combine(backupDirectory, "rollback-manifest.json");
        if (!File.Exists(metadataPath))
        {
            // 兼容早期完整目录备份。
            await RestoreBackupAsync(backupDirectory, targetDirectory, cancellationToken);
            return TryReadVersionFromBackupName(backupDirectory);
        }

        var json = await File.ReadAllTextAsync(metadataPath, cancellationToken);
        var manifest = JsonSerializer.Deserialize<RollbackManifest>(json)
                       ?? throw new InvalidDataException("回退清单内容无效。");
        var filesDirectory = Path.Combine(backupDirectory, "files");

        foreach (var entry in manifest.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = entry.RelativePath.Replace('/', Path.DirectorySeparatorChar);
            if (IsProtectedRelativePath(relative))
                throw new InvalidDataException($"回退清单包含受保护路径：{entry.RelativePath}");
            var targetFile = ResolveSafeRelativePath(targetDirectory, relative);
            if (entry.ExistedBefore)
            {
                var backupFile = ResolveSafeRelativePath(filesDirectory, relative);
                if (!File.Exists(backupFile))
                    throw new FileNotFoundException(
                        $"回退备份缺少文件：{entry.RelativePath}", backupFile);
                await CopyFileAsync(backupFile, targetFile, cancellationToken);
            }
            else if (File.Exists(targetFile))
            {
                File.Delete(targetFile);
                DeleteEmptyParents(Path.GetDirectoryName(targetFile), targetDirectory);
            }
        }
        return manifest.PreviousVersion;
    }

    private static async Task<RollbackManifest> ReadRollbackManifestAsync(
        string path, CancellationToken cancellationToken)
    {
        var json = await File.ReadAllTextAsync(path, cancellationToken);
        return JsonSerializer.Deserialize<RollbackManifest>(json)
               ?? throw new InvalidDataException("回退清单内容无效。");
    }

    private static async Task WriteRollbackManifestAsync(
        string backupDirectory,
        RollbackManifest manifest,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(backupDirectory);
        var path = Path.Combine(backupDirectory, "rollback-manifest.json");
        var json = JsonSerializer.Serialize(manifest, new JsonSerializerOptions
        {
            WriteIndented = true
        });
        await File.WriteAllTextAsync(path, json, cancellationToken);
    }

    private static async Task OverlayDirectoryAsync(
        string source, string destination, CancellationToken cancellationToken) =>
        await CopyDirectoryAsync(source, destination, cancellationToken, _ => true);

    private static async Task RestoreBackupAsync(
        string backup, string target, CancellationToken cancellationToken)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(target))
        {
            if (ProtectedDirectories.Contains(
                    Path.GetFileName(entry), StringComparer.OrdinalIgnoreCase))
                continue;
            if (Directory.Exists(entry)) Directory.Delete(entry, recursive: true);
            else File.Delete(entry);
        }
        await CopyDirectoryAsync(backup, target, cancellationToken, _ => true);
    }

    private static async Task CopyDirectoryAsync(
        string source, string destination, CancellationToken cancellationToken,
        Func<string, bool> include)
    {
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(source, directory);
            if (include(relative)) Directory.CreateDirectory(Path.Combine(destination, relative));
        }
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(source, file);
            if (!include(relative)) continue;
            var target = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await using (var input = new FileStream(
                             file, FileMode.Open, FileAccess.Read, FileShare.Read))
            await using (var output = new FileStream(
                             target, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await input.CopyToAsync(output, cancellationToken);
            }
            File.SetLastWriteTimeUtc(target, File.GetLastWriteTimeUtc(file));
        }
    }

    private static async Task CopyFileAsync(
        string source, string destination, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        await using (var input = new FileStream(
                         source, FileMode.Open, FileAccess.Read, FileShare.Read))
        await using (var output = new FileStream(
                         destination, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            await input.CopyToAsync(output, cancellationToken);
        }
        File.SetLastWriteTimeUtc(destination, File.GetLastWriteTimeUtc(source));
    }

    private static void DeleteEmptyParents(string? directory, string root)
    {
        var rootPath = Path.GetFullPath(root)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        while (!string.IsNullOrWhiteSpace(directory))
        {
            var fullPath = Path.GetFullPath(directory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (fullPath.Equals(rootPath, StringComparison.OrdinalIgnoreCase) ||
                !fullPath.StartsWith(
                    rootPath + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase) ||
                Directory.EnumerateFileSystemEntries(fullPath).Any())
                return;
            Directory.Delete(fullPath);
            directory = Path.GetDirectoryName(fullPath);
        }
    }

    private static bool IsProtectedRelativePath(string relative)
    {
        var firstSegment = relative.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            2, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return firstSegment is not null &&
               ProtectedDirectories.Contains(firstSegment, StringComparer.OrdinalIgnoreCase);
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
            // A successful rollback must not be reported as failed merely
            // because an antivirus or indexer briefly holds the safety backup.
        }
    }

    private static string SelectBackup(string backupRoot, string? targetVersion)
    {
        var backups = Directory.Exists(backupRoot)
            ? Directory.GetDirectories(backupRoot)
                .Where(path => !Path.GetFileName(path).StartsWith("before_rollback_",
                    StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(Directory.GetCreationTimeUtc)
                .ToArray()
            : [];
        var selected = string.IsNullOrWhiteSpace(targetVersion)
            ? backups.FirstOrDefault()
            : backups.FirstOrDefault(path =>
                Path.GetFileName(path).Contains(targetVersion, StringComparison.OrdinalIgnoreCase));
        return selected ?? throw new DirectoryNotFoundException("没有找到可用的版本备份。");
    }

    private static string? TryReadVersionFromBackupName(string backupDirectory)
    {
        var name = Path.GetFileName(backupDirectory);
        var separator = name.IndexOf('_');
        return separator >= 0 && separator + 1 < name.Length
            ? name[(separator + 1)..]
            : null;
    }

    private static string SanitizeName(string value)
    {
        foreach (var character in Path.GetInvalidFileNameChars())
            value = value.Replace(character, '_');
        return string.IsNullOrWhiteSpace(value) ? "unknown" : value;
    }

    private void Report(
        UpdateStage stage, int percentage, string message, string? detail = null) =>
        progress?.Report(new UpdateProgress(stage, percentage, message, detail));

    private static string FormatBytes(long bytes) => bytes switch
    {
        >= 1024L * 1024L * 1024L => $"{bytes / 1024d / 1024d / 1024d:F2} GB",
        >= 1024L * 1024L => $"{bytes / 1024d / 1024d:F2} MB",
        >= 1024L => $"{bytes / 1024d:F1} KB",
        _ => $"{bytes} B"
    };

    private static async Task WriteInstalledVersionAsync(
        string target, UpdateManifest manifest, CancellationToken cancellationToken)
        => await WriteInstalledVersionAsync(
            target, manifest.Version, cancellationToken);

    private static async Task<string> ReadInstalledVersionAsync(
        string target, CancellationToken cancellationToken)
    {
        var path = Path.Combine(target, ".autoupdater", "installed-version.txt");
        return File.Exists(path)
            ? (await File.ReadAllTextAsync(path, cancellationToken)).Trim()
            : "unknown";
    }

    private static async Task WriteInstalledVersionAsync(
        string target, string version, CancellationToken cancellationToken)
    {
        var path = Path.Combine(target, ".autoupdater", "installed-version.txt");
        await File.WriteAllTextAsync(path, version, cancellationToken);
    }

    private static void Restart(UpdaterOptions options, string executable)
    {
        var path = Path.IsPathRooted(executable)
            ? executable
            : Path.Combine(options.TargetDirectory, executable);
        if (!File.Exists(path)) throw new FileNotFoundException("找不到更新后的启动程序。", path);
        Process.Start(new ProcessStartInfo(path)
        {
            WorkingDirectory = options.TargetDirectory,
            UseShellExecute = true
        });
    }

    private sealed record RollbackManifest(
        string PreviousVersion,
        string InstalledVersion,
        DateTimeOffset CreatedAt,
        IReadOnlyCollection<RollbackFileEntry> Files);

    private sealed record RollbackFileEntry(
        string RelativePath,
        bool ExistedBefore);
}
