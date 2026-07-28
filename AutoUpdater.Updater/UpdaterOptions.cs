using System.IO;
using System.Net;

namespace AutoUpdater.Updater;

internal sealed record UpdaterOptions
{
    public required UpdateOperation Operation { get; init; }
    public string? Source { get; init; }
    public required string TargetDirectory { get; init; }
    public required string RestartExecutable { get; init; }
    public int ProcessId { get; init; }
    public required Guid RequestId { get; init; }
    public required string DeviceId { get; init; }
    public IPAddress? ControllerAddress { get; init; }
    public int ControllerPort { get; init; } = 45677;
    public string? TargetVersion { get; init; }
    public required string BackupRoot { get; init; }
    public required string WorkRoot { get; init; }
    public required string LogPath { get; init; }
    public TimeSpan ProcessWaitTimeout { get; init; } = TimeSpan.FromSeconds(60);

    public static string Usage =>
        """
        更新:
          AutoUpdater.Updater.exe --source <manifest路径或URL> --target <安装目录>
            --process-id <PID> --restart <相对或绝对exe路径> --request-id <GUID>
            --device-id <设备编号> [--controller-ip <IP>] [--controller-port 45677]

        回退:
          AutoUpdater.Updater.exe --rollback --target <安装目录> --restart <exe路径>
            --process-id <PID> --request-id <GUID> --device-id <设备编号>
            [--target-version <版本>] [--backup-root <备份目录>]
        """;

    public static UpdaterOptions Parse(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var flags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < args.Length; index++)
        {
            var token = args[index];
            if (!token.StartsWith("--", StringComparison.Ordinal))
                throw new OptionsException($"无法识别参数：{token}");
            if (token.Equals("--rollback", StringComparison.OrdinalIgnoreCase))
            {
                flags.Add(token);
                continue;
            }
            if (index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal))
                throw new OptionsException($"参数 {token} 缺少值。");
            values[token] = args[++index];
        }

        string Required(string name) =>
            values.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
                ? value
                : throw new OptionsException($"缺少必需参数 {name}。");

        var operation = flags.Contains("--rollback") ? UpdateOperation.Rollback : UpdateOperation.Update;
        var target = Path.GetFullPath(Required("--target"));
        var restart = Required("--restart");
        var requestText = Required("--request-id");
        if (!Guid.TryParse(requestText, out var requestId))
            throw new OptionsException("--request-id 必须是有效 GUID。");

        var processId = 0;
        if (values.TryGetValue("--process-id", out var processText) &&
            (!int.TryParse(processText, out processId) || processId < 0))
            throw new OptionsException("--process-id 必须是非负整数。");

        var controllerPort = 45677;
        if (values.TryGetValue("--controller-port", out var portText) &&
            (!int.TryParse(portText, out controllerPort) || controllerPort is < 1 or > 65535))
            throw new OptionsException("--controller-port 无效。");

        IPAddress? controllerAddress = null;
        if (values.TryGetValue("--controller-ip", out var ipText) &&
            !IPAddress.TryParse(ipText, out controllerAddress))
            throw new OptionsException("--controller-ip 无效。");

        var metadataRoot = Path.Combine(target, ".autoupdater");
        return new UpdaterOptions
        {
            Operation = operation,
            Source = operation == UpdateOperation.Update ? Required("--source") : null,
            TargetDirectory = target,
            RestartExecutable = restart,
            ProcessId = processId,
            RequestId = requestId,
            DeviceId = Required("--device-id"),
            ControllerAddress = controllerAddress,
            ControllerPort = controllerPort,
            TargetVersion = values.GetValueOrDefault("--target-version"),
            BackupRoot = Path.GetFullPath(values.GetValueOrDefault("--backup-root") ??
                                          Path.Combine(metadataRoot, "backups")),
            WorkRoot = Path.Combine(metadataRoot, "work", requestId.ToString("N")),
            LogPath = Path.Combine(metadataRoot, "logs", $"{requestId:N}.log")
        };
    }
}

internal enum UpdateOperation
{
    Update,
    Rollback
}

internal sealed class OptionsException(string message) : Exception(message);
