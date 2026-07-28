using System.Security.Cryptography;
using System.Text.Json;
using System.IO;
using AutoUpdater.Protocol;

namespace AutoUpdaterManage.Services;

public sealed record DatabaseSyncPackageInfo(
    string Path,
    long Size,
    string Sha256,
    Guid PackageId);

public static class DatabaseSyncPackageBuilder
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public static async Task<DatabaseSyncPackageInfo> CreateAsync(
        string outputDirectory,
        string databaseName,
        IReadOnlyList<DatabaseChangePayload> changes,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(outputDirectory))
            throw new ArgumentException("请输入设备可访问的同步包共享目录。");
        if (changes.Count == 0)
            throw new InvalidOperationException("没有可生成同步包的变更。");

        var directory = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(directory);
        var packageId = Guid.NewGuid();
        var package = new DatabaseSyncPackage(
            1,
            packageId,
            databaseName,
            DateTimeOffset.UtcNow,
            changes);
        var finalPath = Path.Combine(
            directory,
            $"dbsync-{DateTime.Now:yyyyMMdd-HHmmss}-{packageId:N}.dbsync.json");
        var temporaryPath = finalPath + ".tmp";
        try
        {
            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             81920,
                             FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(
                    stream, package, JsonOptions, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }
            var packageSize = new FileInfo(temporaryPath).Length;
            if (packageSize > 100L * 1024 * 1024)
                throw new InvalidOperationException(
                    "数据库同步包不能超过100MB，请拆分为多个批次。");
            File.Move(temporaryPath, finalPath);
            await using var readStream = new FileStream(
                finalPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read);
            var hash = await SHA256.HashDataAsync(
                readStream, cancellationToken);
            return new DatabaseSyncPackageInfo(
                finalPath,
                readStream.Length,
                Convert.ToHexString(hash),
                packageId);
        }
        catch
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
            throw;
        }
    }
}
