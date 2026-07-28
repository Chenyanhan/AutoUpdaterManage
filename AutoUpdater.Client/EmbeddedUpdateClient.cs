using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using AutoUpdater.Protocol;

namespace AutoUpdater.Client;

/// <summary>嵌入上位机的 UDP 更新客户端。</summary>
public sealed class EmbeddedUpdateClient : IDisposable
{
    public const int DefaultPort = 45678;
    private readonly EmbeddedClientOptions _options;
    private readonly HashSet<Guid> _handledRequests = [];
    private readonly object _requestLock = new();
    private UdpClient? _udp;
    private UdpClient? _discoveryUdp;
    private CancellationTokenSource? _cancellation;

    public EmbeddedUpdateClient(EmbeddedClientOptions options) => _options = options;

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
                StringComparison.OrdinalIgnoreCase) ||
            !TryRegisterRequest(packet.RequestId))
            return;

        var context = new UpdateCommandContext(
            packet.RequestId, controller.Address.ToString(), request.UpdatePath);
        var decision = await GetUpdateDecisionAsync(context);
        var accepted = decision == UpdateDecision.InstallNow;
        await SendAcceptedAsync(packet.RequestId, controller, accepted,
            accepted ? "设备已接受更新" : "用户选择稍后更新");
        if (!accepted) return;

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
                StringComparison.OrdinalIgnoreCase) ||
            !TryRegisterRequest(packet.RequestId))
            return;

        var context = new RollbackCommandContext(
            packet.RequestId, controller.Address.ToString(), request.TargetVersion);
        var decision = await GetRollbackDecisionAsync(context);
        var accepted = decision == UpdateDecision.InstallNow;
        await SendAcceptedAsync(packet.RequestId, controller, accepted,
            accepted ? "设备已接受版本回退" : "用户选择稍后处理");
        if (!accepted) return;

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

    private Task SendResultAsync(
        Guid requestId, IPEndPoint target, bool success, string message) =>
        SendAsync(UdpProtocol.Encode(
            UdpCommand.UpdateResult, requestId,
            new UpdateResultPayload(_options.DeviceId, success, message)), target);

    private async Task SendAsync(byte[] packet, IPEndPoint target) =>
        await _udp!.SendAsync(packet, target);

    private bool TryRegisterRequest(Guid requestId)
    {
        lock (_requestLock)
        {
            if (!_handledRequests.Add(requestId)) return false;
            if (_handledRequests.Count > 1000) _handledRequests.Clear();
            return true;
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
}

public sealed record EmbeddedClientOptions(
    string DeviceId,
    string DeviceName,
    string CurrentVersion,
    string? UpdaterExecutablePath = null,
    int Port = EmbeddedUpdateClient.DefaultPort,
    string? InstallationDirectory = null,
    string? RestartExecutablePath = null);

public enum UpdateDecision
{
    InstallNow,
    Postpone
}

public sealed record UpdateCommandContext(
    Guid RequestId, string ControllerIp, string UpdatePath);

public sealed record RollbackCommandContext(
    Guid RequestId, string ControllerIp, string? TargetVersion);
