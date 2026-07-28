using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using AutoUpdaterManage.Models;

namespace AutoUpdaterManage.Services;

public sealed record PackageBuildRequest(
    string SourceDirectory,
    string OutputDirectory,
    string Version,
    IReadOnlyCollection<PackageFileItem> Files);

public sealed record PackageBuildResult(
    string PackagePath, string ManifestPath, string Sha256, int FileCount);

public sealed class UpdatePackageBuilder
{
    public async Task<PackageBuildResult> BuildAsync(
        PackageBuildRequest request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.SourceDirectory))
            throw new InvalidOperationException("请先选择程序根目录。");
        if (string.IsNullOrWhiteSpace(request.OutputDirectory))
            throw new InvalidOperationException("请选择发布包输出目录。");
        if (!System.Version.TryParse(request.Version, out _))
            throw new InvalidOperationException("目标版本格式无效，例如应为 2.0.0.0。");
        var selectedFiles = request.Files.Where(file => file.IsSelected).ToArray();
        if (selectedFiles.Length == 0)
            throw new InvalidOperationException("请至少选择一个更新文件。");

        var sourceRoot = EnsureDirectoryRoot(request.SourceDirectory);
        Directory.CreateDirectory(request.OutputDirectory);
        var safeVersion = string.Join("_", request.Version.Split(Path.GetInvalidFileNameChars()));
        var packageName = $"application-{safeVersion}.zip";
        var packagePath = Path.Combine(request.OutputDirectory, packageName);
        var temporaryPackage = packagePath + $".{Guid.NewGuid():N}.tmp";

        try
        {
            await using (var output = new FileStream(
                             temporaryPackage, FileMode.CreateNew,
                             FileAccess.ReadWrite, FileShare.None))
            {
                using var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
                foreach (var file in selectedFiles)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var fullPath = Path.GetFullPath(file.FullPath);
                    if (!fullPath.StartsWith(sourceRoot, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException($"文件超出源目录：{file.RelativePath}");
                    var entryName = file.RelativePath.Replace('\\', '/');
                    var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
                    var sourceWriteTime = File.GetLastWriteTime(fullPath);
                    // ZIP 时间戳的最早有效年份是 1980。
                    if (sourceWriteTime.Year >= 1980)
                        entry.LastWriteTime = new DateTimeOffset(sourceWriteTime);
                    try
                    {
                        await using var input = new FileStream(
                            fullPath, FileMode.Open, FileAccess.Read,
                            FileShare.ReadWrite | FileShare.Delete);
                        await using var entryStream = entry.Open();
                        await input.CopyToAsync(entryStream, cancellationToken);
                    }
                    catch (IOException ex)
                    {
                        throw new IOException(
                            $"无法读取文件“{file.RelativePath}”。该文件可能正被其他程序独占使用，" +
                            "请关闭相关程序或取消勾选此文件。", ex);
                    }
                    catch (UnauthorizedAccessException ex)
                    {
                        throw new UnauthorizedAccessException(
                            $"没有权限读取文件“{file.RelativePath}”，请检查文件权限或取消勾选。", ex);
                    }
                }
            }

            string hash;
            await using (var packageStream = File.OpenRead(temporaryPackage))
            {
                hash = Convert.ToHexString(
                    await SHA256.HashDataAsync(packageStream, cancellationToken));
            }
            try
            {
                File.Move(temporaryPackage, packagePath, overwrite: true);
            }
            catch (IOException ex)
            {
                throw new IOException(
                    $"无法写入发布包“{packagePath}”。旧 ZIP 可能正被压缩软件、资源管理器" +
                    "或其他程序占用，请关闭后重试。", ex);
            }

            var manifestPath = Path.Combine(request.OutputDirectory, "manifest.json");
            var manifest = new
            {
                version = request.Version,
                package = packageName,
                sha256 = hash,
                releaseNotes = $"版本 {request.Version} 增量更新",
                mandatory = false
            };
            var json = JsonSerializer.Serialize(manifest, new JsonSerializerOptions
            {
                WriteIndented = true
            });
            await File.WriteAllTextAsync(manifestPath, json, cancellationToken);
            return new PackageBuildResult(
                packagePath, manifestPath, hash, selectedFiles.Length);
        }
        finally
        {
            if (File.Exists(temporaryPackage))
                File.Delete(temporaryPackage);
        }
    }

    private static string EnsureDirectoryRoot(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (!Directory.Exists(fullPath))
            throw new DirectoryNotFoundException($"源目录不存在：{fullPath}");
        return fullPath.TrimEnd(
                   Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
               Path.DirectorySeparatorChar;
    }
}
