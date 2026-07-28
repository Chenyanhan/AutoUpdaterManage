using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.IO;
using MySqlConnector;

namespace AutoUpdater.Client;

public sealed record ClientDatabaseSettings(
    string Server,
    uint Port,
    string Database,
    string UserId,
    string EncryptedPassword,
    string SslMode = "None");

public sealed record DatabaseConnectionTestResult(
    bool Success,
    string Message,
    string? ServerVersion = null);

public static class ClientDatabaseSettingsStore
{
    private static readonly byte[] Entropy =
        Encoding.UTF8.GetBytes("AutoUpdater.Client.DatabaseSettings.v1");
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public static string GetDefaultPath(string installationDirectory) =>
        Path.Combine(
            Path.GetFullPath(installationDirectory),
            "AutoUpdater",
            "client-settings.json");

    public static async Task SaveAsync(
        string installationDirectory,
        MySqlConnectionStringBuilder source,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(source.Server))
            throw new ArgumentException("MySQL服务器不能为空。");
        if (string.IsNullOrWhiteSpace(source.Database))
            throw new ArgumentException("数据库名不能为空。");
        if (string.IsNullOrWhiteSpace(source.UserID))
            throw new ArgumentException("用户名不能为空。");

        var protectedPassword = ProtectedData.Protect(
            Encoding.UTF8.GetBytes(source.Password),
            Entropy,
            DataProtectionScope.CurrentUser);
        var settings = new ClientDatabaseSettings(
            source.Server,
            source.Port,
            source.Database,
            source.UserID,
            Convert.ToBase64String(protectedPassword),
            source.SslMode.ToString());
        var path = GetDefaultPath(installationDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporaryPath = path + ".tmp";
        try
        {
            await File.WriteAllTextAsync(
                temporaryPath,
                JsonSerializer.Serialize(settings, JsonOptions),
                Encoding.UTF8,
                cancellationToken);
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    public static string? TryLoadConnectionString(
        string installationDirectory,
        out string message)
    {
        var path = GetDefaultPath(installationDirectory);
        if (!File.Exists(path))
        {
            message = $"未找到数据库配置：{path}";
            return null;
        }
        try
        {
            var settings =
                JsonSerializer.Deserialize<ClientDatabaseSettings>(
                    File.ReadAllText(path),
                    new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    })
                ?? throw new InvalidDataException("数据库配置内容为空。");
            var passwordBytes = ProtectedData.Unprotect(
                Convert.FromBase64String(settings.EncryptedPassword),
                Entropy,
                DataProtectionScope.CurrentUser);
            if (!Enum.TryParse<MySqlSslMode>(
                    settings.SslMode,
                    ignoreCase: true,
                    out var sslMode))
                throw new InvalidDataException(
                    $"不支持的SSL模式：{settings.SslMode}");
            var builder = new MySqlConnectionStringBuilder
            {
                Server = settings.Server,
                Port = settings.Port,
                Database = settings.Database,
                UserID = settings.UserId,
                Password = Encoding.UTF8.GetString(passwordBytes),
                SslMode = sslMode,
                ConnectionTimeout = 10,
                DefaultCommandTimeout = 30,
                Pooling = true
            };
            CryptographicOperations.ZeroMemory(passwordBytes);
            message = $"已读取加密配置：{path}";
            return builder.ConnectionString;
        }
        catch (Exception ex)
        {
            message = $"数据库配置无法读取：{ex.Message}";
            return null;
        }
    }

    public static async Task<DatabaseConnectionTestResult> TestAsync(
        string connectionString,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection =
                new MySqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT DATABASE();";
            var database = Convert.ToString(
                await command.ExecuteScalarAsync(cancellationToken));
            return new DatabaseConnectionTestResult(
                true,
                $"连接成功：{database}",
                connection.ServerVersion);
        }
        catch (Exception ex)
        {
            return new DatabaseConnectionTestResult(
                false,
                $"连接失败：{ex.Message}");
        }
    }
}
