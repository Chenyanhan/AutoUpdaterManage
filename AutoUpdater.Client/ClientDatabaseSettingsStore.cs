using System.IO;
using System.Xml.Linq;
using MySqlConnector;

namespace AutoUpdater.Client;

public sealed record DatabaseConnectionTestResult(
    bool Success,
    string Message,
    string? ServerVersion = null);

public static class ClientDatabaseSettingsStore
{
    public const string DefaultHostExecutableName = "卷绕机.exe";

    public static string? TryLoadFromApplicationConfig(
        string installationDirectory,
        string? applicationExecutable,
        out string message)
    {
        var directory = Path.GetFullPath(installationDirectory);
        var executable = string.IsNullOrWhiteSpace(applicationExecutable)
            ? DefaultHostExecutableName
            : applicationExecutable;
        var executablePath = Path.IsPathRooted(executable)
            ? executable
            : Path.Combine(directory, executable);
        var configPath = Path.GetFullPath(executablePath) + ".config";
        if (!File.Exists(configPath))
        {
            message = $"未找到上位机数据库配置：{configPath}";
            return null;
        }

        try
        {
            var document = XDocument.Load(configPath, LoadOptions.None);
            var values = document
                .Descendants()
                .Where(element =>
                    element.Name.LocalName.Equals(
                        "add", StringComparison.OrdinalIgnoreCase))
                .SelectMany(element => new[]
                {
                    element.Attribute("connectionString")?.Value,
                    element.Attribute("value")?.Value
                })
                .Where(value => !string.IsNullOrWhiteSpace(value));
            foreach (var value in values)
            {
                try
                {
                    var builder =
                        new MySqlConnectionStringBuilder(value!);
                    if (string.IsNullOrWhiteSpace(builder.Server) ||
                        string.IsNullOrWhiteSpace(builder.Database) ||
                        string.IsNullOrWhiteSpace(builder.UserID))
                        continue;
                    builder.ConnectionTimeout = 10;
                    builder.DefaultCommandTimeout = 30;
                    builder.Pooling = true;
                    message = $"已读取上位机配置：{configPath}";
                    return builder.ConnectionString;
                }
                catch (ArgumentException)
                {
                    // 该配置项不是MySQL连接串，继续检查其他项。
                }
            }
            message = $"未在 {configPath} 中找到标准MySQL连接串。";
            return null;
        }
        catch (Exception ex)
        {
            message = $"上位机配置无法读取：{configPath}：{ex.Message}";
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
