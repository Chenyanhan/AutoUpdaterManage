using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using AutoUpdater.Protocol;

namespace AutoUpdater.Client;

/// <summary>嵌入上位机的 UDP 更新客户端。</summary>
public sealed class EmbeddedUpdateClient : IDisposable
{
    public const int DefaultPort = 45678;
    private readonly EmbeddedClientOptions _options;
    private readonly string? _databaseConnectionString;
    private readonly ProcessedRequestStore _processedRequests;
    private readonly ConcurrentDictionary<Guid, Lazy<Task<CommandOutcome>>> _inflightRequests = new();
    private readonly ConcurrentDictionary<Guid,
        Lazy<Task<DatabaseSyncExecutionResult>>> _inflightDatabaseRequests = new();
    private UdpClient? _udp;
    private UdpClient? _discoveryUdp;
    private CancellationTokenSource? _cancellation;

    public EmbeddedUpdateClient(EmbeddedClientOptions options)
    {
        _options = options;
        var installationDirectory = Path.GetFullPath(
            options.InstallationDirectory ?? AppContext.BaseDirectory);
        _processedRequests = new ProcessedRequestStore(installationDirectory);
        _databaseConnectionString = options.DatabaseConnectionString ??
            ClientDatabaseSettingsStore.TryLoadFromApplicationConfig(
                installationDirectory,
                options.RestartExecutablePath ??
                ClientDatabaseSettingsStore.DefaultHostExecutableName,
                out _);
    }

    public event Func<UpdateCommandContext, Task<bool>>? UpdateConfirmationRequired;
    public event Func<RollbackCommandContext, Task<bool>>? RollbackConfirmationRequired;
    public event Func<UpdateCommandContext, Task<UpdateDecision>>? UpdateDecisionRequired;
    public event Func<RollbackCommandContext, Task<UpdateDecision>>? RollbackDecisionRequired;
    public event Action? ShutdownRequested;
    public event Action<Exception>? Error;

    public Task StartAsync()
    {
        if (_udp is not null) return Task.CompletedTask;
        _udp = CreateListener(_options.Port);
        _cancellation = new CancellationTokenSource();
        _ = ReceiveLoopAsync(_udp, discoveryOnly: false, _cancellation.Token);

        if (_options.Port != DefaultPort)
        {
            _discoveryUdp = CreateListener(DefaultPort);
            _ = ReceiveLoopAsync(_discoveryUdp, discoveryOnly: true, _cancellation.Token);
        }
        return Task.CompletedTask;
    }

    private static UdpClient CreateListener(int port)
    {
        var udp = new UdpClient(AddressFamily.InterNetwork);
        udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        udp.Client.Bind(new IPEndPoint(IPAddress.Any, port));
        return udp;
    }

    private async Task ReceiveLoopAsync(
        UdpClient receiver, bool discoveryOnly, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var datagram = await receiver.ReceiveAsync(cancellationToken);
                if (!UdpProtocol.TryDecode(datagram.Buffer, out var packet)) continue;
                if (packet.Command == UdpCommand.DiscoverRequest)
                    await ReplyToDiscoveryAsync(packet.RequestId, datagram.RemoteEndPoint);
                else if (!discoveryOnly && packet.Command == UdpCommand.UpdateRequest)
                    await HandleUpdateAsync(packet, datagram.RemoteEndPoint);
                else if (!discoveryOnly && packet.Command == UdpCommand.RollbackRequest)
                    await HandleRollbackAsync(packet, datagram.RemoteEndPoint);
                else if (!discoveryOnly &&
                         packet.Command == UdpCommand.DatabaseSyncRequest)
                    await HandleDatabaseSyncAsync(packet, datagram.RemoteEndPoint);
                else if (!discoveryOnly &&
                         packet.Command == UdpCommand.DatabaseSyncFileRequest)
                    await HandleDatabaseSyncFileAsync(
                        packet, datagram.RemoteEndPoint, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                Error?.Invoke(ex);
            }
        }
    }

    private Task ReplyToDiscoveryAsync(Guid requestId, IPEndPoint controller)
    {
        var payload = new DiscoverResponsePayload(
            _options.DeviceId, _options.DeviceName, GetPreferredIpv4(),
            _options.CurrentVersion, _options.Port);
        return SendAsync(UdpProtocol.Encode(UdpCommand.DiscoverResponse, requestId, payload), controller);
    }

    private async Task HandleUpdateAsync(UdpPacket packet, IPEndPoint controller)
    {
        var request = UdpProtocol.DecodePayload<UpdateRequestPayload>(packet);
        if (request is null ||
            !string.Equals(request.TargetDeviceId, _options.DeviceId,
                StringComparison.OrdinalIgnoreCase))
            return;

        await SendTaskReceivedAsync(packet.RequestId, controller);
        if (_processedRequests.TryGet(packet.RequestId, out var processed))
        {
            await SendAcceptedAsync(
                packet.RequestId, controller, processed.Accepted, processed.Message);
            return;
        }

        var operation = _inflightRequests.GetOrAdd(
            packet.RequestId,
            _ => new Lazy<Task<CommandOutcome>>(
                () => ConfirmUpdateAsync(packet.RequestId, controller, request),
                LazyThreadSafetyMode.ExecutionAndPublication));
        var outcome = await operation.Value;
        await SendAcceptedAsync(
            packet.RequestId, controller, outcome.Accepted, outcome.Message);
        if (!outcome.Accepted)
        {
            _inflightRequests.TryRemove(packet.RequestId, out _);
            return;
        }
        if (!outcome.TryClaimExecution())
            return;

        try
        {
            StartUpdater(BuildUpdaterArguments(
                packet.RequestId, controller, request.UpdatePath,
                rollback: false, targetVersion: null));
            ShutdownRequested?.Invoke();
        }
        catch (Exception ex)
        {
            await SendResultAsync(packet.RequestId, controller, false, ex.Message);
        }
        finally
        {
            _inflightRequests.TryRemove(packet.RequestId, out _);
        }
    }

    private async Task<CommandOutcome> ConfirmUpdateAsync(
        Guid requestId,
        IPEndPoint controller,
        UpdateRequestPayload request)
    {
        var context = new UpdateCommandContext(
            requestId, controller.Address.ToString(), request.UpdatePath);
        var accepted =
            await GetUpdateDecisionAsync(context) == UpdateDecision.InstallNow;
        var message = accepted
            ? "设备已接受更新"
            : "用户选择稍后更新";
        SaveProcessedRequest(new ProcessedRequest(
            requestId, "Update", accepted, message, DateTimeOffset.UtcNow));
        return new CommandOutcome(accepted, message);
    }

    private async Task<UpdateDecision> GetUpdateDecisionAsync(
        UpdateCommandContext context)
    {
        if (UpdateDecisionRequired is not null)
            return await UpdateDecisionRequired(context);
        if (UpdateConfirmationRequired is not null)
            return await UpdateConfirmationRequired(context)
                ? UpdateDecision.InstallNow
                : UpdateDecision.Postpone;
        return await DesktopUpdatePrompt.ShowUpdateAsync(
            context, _options.DeviceName);
    }

    private async Task<UpdateDecision> GetRollbackDecisionAsync(
        RollbackCommandContext context)
    {
        if (RollbackDecisionRequired is not null)
            return await RollbackDecisionRequired(context);
        if (RollbackConfirmationRequired is not null)
            return await RollbackConfirmationRequired(context)
                ? UpdateDecision.InstallNow
                : UpdateDecision.Postpone;
        return await DesktopUpdatePrompt.ShowRollbackAsync(
            context, _options.DeviceName);
    }

    private async Task HandleRollbackAsync(UdpPacket packet, IPEndPoint controller)
    {
        var request = UdpProtocol.DecodePayload<RollbackRequestPayload>(packet);
        if (request is null ||
            !string.Equals(request.TargetDeviceId, _options.DeviceId,
                StringComparison.OrdinalIgnoreCase))
            return;

        await SendTaskReceivedAsync(packet.RequestId, controller);
        if (_processedRequests.TryGet(packet.RequestId, out var processed))
        {
            await SendAcceptedAsync(
                packet.RequestId, controller, processed.Accepted, processed.Message);
            return;
        }

        var operation = _inflightRequests.GetOrAdd(
            packet.RequestId,
            _ => new Lazy<Task<CommandOutcome>>(
                () => ConfirmRollbackAsync(packet.RequestId, controller, request),
                LazyThreadSafetyMode.ExecutionAndPublication));
        var outcome = await operation.Value;
        await SendAcceptedAsync(
            packet.RequestId, controller, outcome.Accepted, outcome.Message);
        if (!outcome.Accepted)
        {
            _inflightRequests.TryRemove(packet.RequestId, out _);
            return;
        }
        if (!outcome.TryClaimExecution())
            return;

        try
        {
            StartUpdater(BuildUpdaterArguments(
                packet.RequestId, controller, null,
                rollback: true, request.TargetVersion));
            ShutdownRequested?.Invoke();
        }
        catch (Exception ex)
        {
            await SendResultAsync(packet.RequestId, controller, false, ex.Message);
        }
        finally
        {
            _inflightRequests.TryRemove(packet.RequestId, out _);
        }
    }

    private async Task<CommandOutcome> ConfirmRollbackAsync(
        Guid requestId,
        IPEndPoint controller,
        RollbackRequestPayload request)
    {
        var context = new RollbackCommandContext(
            requestId, controller.Address.ToString(), request.TargetVersion);
        var accepted =
            await GetRollbackDecisionAsync(context) == UpdateDecision.InstallNow;
        var message = accepted
            ? "设备已接受版本回退"
            : "用户选择稍后处理";
        SaveProcessedRequest(new ProcessedRequest(
            requestId, "Rollback", accepted, message, DateTimeOffset.UtcNow));
        return new CommandOutcome(accepted, message);
    }

    private async Task HandleDatabaseSyncAsync(
        UdpPacket packet,
        IPEndPoint controller)
    {
        var request =
            UdpProtocol.DecodePayload<DatabaseSyncRequestPayload>(packet);
        if (request is null ||
            !string.Equals(
                request.TargetDeviceId,
                _options.DeviceId,
                StringComparison.OrdinalIgnoreCase))
            return;

        await SendTaskReceivedAsync(packet.RequestId, controller);
        if (_processedRequests.TryGet(packet.RequestId, out var processed))
        {
            await SendDatabaseSyncResultAsync(
                packet.RequestId,
                controller,
                processed.Accepted,
                processed.Message,
                processed.Accepted ? request.Changes.Count : 0);
            return;
        }

        var operation = _inflightDatabaseRequests.GetOrAdd(
            packet.RequestId,
            _ => new Lazy<Task<DatabaseSyncExecutionResult>>(
                () => ExecuteDatabaseSyncAsync(packet.RequestId, request),
                LazyThreadSafetyMode.ExecutionAndPublication));
        var result = await operation.Value;
        await SendDatabaseSyncResultAsync(
            packet.RequestId,
            controller,
            result.Success,
            result.Message,
            result.Success ? request.Changes.Count : 0);
        _inflightDatabaseRequests.TryRemove(packet.RequestId, out _);
    }

    private async Task HandleDatabaseSyncFileAsync(
        UdpPacket packet,
        IPEndPoint controller,
        CancellationToken cancellationToken)
    {
        var fileRequest =
            UdpProtocol.DecodePayload<DatabaseSyncFileRequestPayload>(packet);
        if (fileRequest is null ||
            !string.Equals(
                fileRequest.TargetDeviceId,
                _options.DeviceId,
                StringComparison.OrdinalIgnoreCase))
            return;

        await SendTaskReceivedAsync(packet.RequestId, controller);
        if (_processedRequests.TryGet(packet.RequestId, out var processed))
        {
            await SendDatabaseSyncResultAsync(
                packet.RequestId,
                controller,
                processed.Accepted,
                processed.Message,
                0);
            return;
        }

        DatabaseSyncRequestPayload request;
        try
        {
            var package = await LoadDatabaseSyncPackageAsync(
                fileRequest, cancellationToken);
            request = new DatabaseSyncRequestPayload(
                fileRequest.SenderId,
                fileRequest.TargetDeviceId,
                package.DatabaseName,
                package.Changes);
        }
        catch (Exception ex)
        {
            var failure = SaveDatabaseSyncResult(
                packet.RequestId,
                false,
                $"同步包校验失败：{ex.Message}");
            await SendDatabaseSyncResultAsync(
                packet.RequestId,
                controller,
                false,
                failure.Message,
                0);
            return;
        }

        var operation = _inflightDatabaseRequests.GetOrAdd(
            packet.RequestId,
            _ => new Lazy<Task<DatabaseSyncExecutionResult>>(
                () => ExecuteDatabaseSyncAsync(packet.RequestId, request),
                LazyThreadSafetyMode.ExecutionAndPublication));
        var result = await operation.Value;
        await SendDatabaseSyncResultAsync(
            packet.RequestId,
            controller,
            result.Success,
            result.Message,
            result.Success ? request.Changes.Count : 0);
        _inflightDatabaseRequests.TryRemove(packet.RequestId, out _);
    }

    private static async Task<DatabaseSyncPackage> LoadDatabaseSyncPackageAsync(
        DatabaseSyncFileRequestPayload request,
        CancellationToken cancellationToken)
    {
        const long maxPackageSize = 100L * 1024 * 1024;
        if (request.PackageSize is <= 0 or > maxPackageSize)
            throw new InvalidOperationException("同步包大小必须在1字节到100MB之间。");
        if (string.IsNullOrWhiteSpace(request.PackagePath))
            throw new InvalidOperationException("同步包路径不能为空。");
        var path = Path.GetFullPath(request.PackagePath);
        var file = new FileInfo(path);
        if (!file.Exists)
            throw new FileNotFoundException("找不到数据库同步包。", path);
        if (file.Length != request.PackageSize)
            throw new InvalidDataException(
                $"同步包大小不一致，期望 {request.PackageSize}，实际 {file.Length}。");

        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var actualHash = await SHA256.HashDataAsync(stream, cancellationToken);
        byte[] expectedHash;
        try
        {
            expectedHash = Convert.FromHexString(request.Sha256);
        }
        catch (FormatException)
        {
            throw new InvalidDataException("同步包SHA-256格式无效。");
        }
        if (expectedHash.Length != actualHash.Length ||
            !CryptographicOperations.FixedTimeEquals(expectedHash, actualHash))
            throw new InvalidDataException("同步包SHA-256校验失败，文件可能已损坏或被篡改。");

        stream.Position = 0;
        var package = await JsonSerializer.DeserializeAsync<DatabaseSyncPackage>(
                          stream,
                          new JsonSerializerOptions
                          {
                              PropertyNameCaseInsensitive = true
                          },
                          cancellationToken)
                      ?? throw new InvalidDataException("同步包内容为空。");
        if (package.SchemaVersion != 1)
            throw new InvalidDataException(
                $"不支持同步包版本：{package.SchemaVersion}。");
        if (package.Changes.Count == 0)
            throw new InvalidDataException("同步包不包含数据库变更。");
        return package;
    }

    private async Task<DatabaseSyncExecutionResult> ExecuteDatabaseSyncAsync(
        Guid requestId,
        DatabaseSyncRequestPayload request)
    {
        var validationError = ValidateDatabaseSync(request);
        if (validationError is not null)
            return SaveDatabaseSyncResult(
                requestId, false, validationError);
        if (string.IsNullOrWhiteSpace(_databaseConnectionString))
            return SaveDatabaseSyncResult(
                requestId, false, "客户端尚未配置MySQL连接。");

        try
        {
            var affectedRows = await DatabaseSyncExecutor.ExecuteAsync(
                _databaseConnectionString,
                request,
                _cancellation?.Token ?? CancellationToken.None);
            return SaveDatabaseSyncResult(
                requestId,
                true,
                $"数据库同步成功：{request.Changes.Count} 条变更，影响 {affectedRows} 行");
        }
        catch (Exception ex)
        {
            return SaveDatabaseSyncResult(
                requestId,
                false,
                $"数据库同步失败，事务已回滚：{ex.Message}");
        }
    }

    private DatabaseSyncExecutionResult SaveDatabaseSyncResult(
        Guid requestId,
        bool success,
        string message)
    {
        SaveProcessedRequest(new ProcessedRequest(
            requestId,
            "DatabaseSync",
            success,
            message,
            DateTimeOffset.UtcNow));
        return new DatabaseSyncExecutionResult(success, message);
    }

    private static string? ValidateDatabaseSync(
        DatabaseSyncRequestPayload request)
    {
        if (string.IsNullOrWhiteSpace(request.DatabaseName))
            return "数据库名称不能为空";
        if (request.Changes.Count is 0 or > 500)
            return "数据库变更数量必须在1到500条之间";
        var allowedTables = new HashSet<string>(
            ["data_result", "plc_user_manage"],
            StringComparer.OrdinalIgnoreCase);
        foreach (var change in request.Changes)
        {
            if (!allowedTables.Contains(change.TableName))
                return $"不允许同步数据表：{change.TableName}";
            if (change.Operation is not ("INSERT" or "UPDATE" or "DELETE"))
                return $"不支持的数据库操作：{change.Operation}";
            if (change.Operation is "UPDATE" or "DELETE" &&
                change.KeyValues.Count == 0)
                return $"{change.Operation} {change.TableName} 缺少主键";
        }
        return null;
    }

    private void StartUpdater(IReadOnlyCollection<string> arguments)
    {
        var updaterPath = ResolveUpdaterExecutablePath();
        if (!File.Exists(updaterPath))
            throw new FileNotFoundException(
                "找不到独立更新程序，请确认上位机目录下的 AutoUpdater 文件夹已正确部署。",
                updaterPath);
        var startInfo = new ProcessStartInfo
        {
            FileName = updaterPath,
            UseShellExecute = true
        };
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);
        _ = Process.Start(startInfo)
            ?? throw new InvalidOperationException("更新程序启动失败。");
    }

    private string ResolveUpdaterExecutablePath()
    {
        if (!string.IsNullOrWhiteSpace(_options.UpdaterExecutablePath))
            return Path.GetFullPath(_options.UpdaterExecutablePath);
        var installationDirectory = Path.GetFullPath(
            _options.InstallationDirectory ?? AppContext.BaseDirectory);
        return Path.Combine(
            installationDirectory, "AutoUpdater", "AutoUpdater.Updater.exe");
    }

    private IReadOnlyCollection<string> BuildUpdaterArguments(
        Guid requestId, IPEndPoint controller, string? source,
        bool rollback, string? targetVersion)
    {
        var target = Path.GetFullPath(
            _options.InstallationDirectory ?? AppContext.BaseDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var restart = _options.RestartExecutablePath ??
                      Path.GetFileName(Environment.ProcessPath) ??
                      throw new InvalidOperationException("无法确定宿主启动程序。");
        var arguments = new List<string>();
        if (rollback) arguments.Add("--rollback");
        if (source is not null)
        {
            arguments.Add("--source");
            arguments.Add(source);
        }
        arguments.AddRange([
            "--target", target,
            "--process-id", Environment.ProcessId.ToString(),
            "--restart", restart,
            "--current-version", _options.CurrentVersion,
            "--request-id", requestId.ToString("N"),
            "--device-id", _options.DeviceId,
            "--controller-ip", controller.Address.ToString(),
            "--controller-port", controller.Port.ToString()
        ]);
        if (!string.IsNullOrWhiteSpace(targetVersion))
        {
            arguments.Add("--target-version");
            arguments.Add(targetVersion);
        }
        return arguments;
    }

    private Task SendAcceptedAsync(
        Guid requestId, IPEndPoint target, bool accepted, string message) =>
        SendAsync(UdpProtocol.Encode(
            UdpCommand.UpdateAccepted, requestId,
            new UpdateAcceptedPayload(_options.DeviceId, accepted, message)), target);

    private Task SendTaskReceivedAsync(Guid requestId, IPEndPoint target) =>
        SendAsync(UdpProtocol.Encode(
            UdpCommand.TaskReceived,
            requestId,
            new TaskReceivedPayload(_options.DeviceId)), target);

    private Task SendResultAsync(
        Guid requestId, IPEndPoint target, bool success, string message) =>
        SendAsync(UdpProtocol.Encode(
            UdpCommand.UpdateResult, requestId,
            new UpdateResultPayload(_options.DeviceId, success, message)), target);

    private Task SendDatabaseSyncResultAsync(
        Guid requestId,
        IPEndPoint target,
        bool success,
        string message,
        int acceptedChanges) =>
        SendAsync(UdpProtocol.Encode(
            UdpCommand.DatabaseSyncResult,
            requestId,
            new DatabaseSyncResultPayload(
                _options.DeviceId,
                success,
                message,
                acceptedChanges)), target);

    private async Task SendAsync(byte[] packet, IPEndPoint target) =>
        await _udp!.SendAsync(packet, target);

    private void SaveProcessedRequest(ProcessedRequest request)
    {
        try
        {
            _processedRequests.Save(request);
        }
        catch (Exception ex)
        {
            Error?.Invoke(ex);
        }
    }

    private static string GetPreferredIpv4() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up &&
                        n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
            ?.Address.ToString() ?? "0.0.0.0";

    public void Dispose()
    {
        _cancellation?.Cancel();
        _udp?.Dispose();
        _discoveryUdp?.Dispose();
        _cancellation?.Dispose();
    }

    private sealed class CommandOutcome(bool accepted, string message)
    {
        private int _executionClaimed;

        public bool Accepted { get; } = accepted;
        public string Message { get; } = message;

        public bool TryClaimExecution() =>
            Interlocked.CompareExchange(ref _executionClaimed, 1, 0) == 0;
    }

    private sealed record DatabaseSyncExecutionResult(
        bool Success,
        string Message);
}

public sealed record EmbeddedClientOptions(
    string DeviceId,
    string DeviceName,
    string CurrentVersion,
    string? UpdaterExecutablePath = null,
    int Port = EmbeddedUpdateClient.DefaultPort,
    string? InstallationDirectory = null,
    string? RestartExecutablePath = null,
    string? DatabaseConnectionString = null);

public enum UpdateDecision
{
    InstallNow,
    Postpone
}

public sealed record UpdateCommandContext(
    Guid RequestId, string ControllerIp, string UpdatePath);

public sealed record RollbackCommandContext(
    Guid RequestId, string ControllerIp, string? TargetVersion);
