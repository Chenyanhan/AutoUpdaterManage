using System.Net;
using System.Net.Sockets;
using System.Net.NetworkInformation;
using System.Collections.Concurrent;
using AutoUpdater.Protocol;

namespace AutoUpdaterManage.Services;

public sealed record DiscoveredDevice(
    string DeviceId, string Name, string IpAddress, string Version, int ListenPort);
public sealed record UpdateStatusInfo(
    Guid RequestId,
    string DeviceId,
    bool Success,
    string Message,
    bool IsFinal,
    string? CurrentVersion = null);
public sealed record DispatchAcknowledgement(
    bool Received,
    bool Accepted,
    string Message,
    int Attempts);

public sealed class UdpControllerService : IDisposable
{
    public const int ControllerPort = 45677;
    public const int DevicePort = 45678;
    private UdpClient? _udp;
    private CancellationTokenSource? _cancellation;
    private readonly ConcurrentDictionary<Guid,
        TaskCompletionSource<TaskReceivedPayload>> _pendingAcknowledgements = new();

    public event Action<DiscoveredDevice>? DeviceDiscovered;
    public event Action<UpdateStatusInfo>? UpdateStatusReceived;

    public Task StartAsync()
    {
        if (_udp is not null) return Task.CompletedTask;
        _udp = new UdpClient(AddressFamily.InterNetwork);
        _udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _udp.Client.Bind(new IPEndPoint(IPAddress.Any, ControllerPort));
        _udp.EnableBroadcast = true;
        _cancellation = new CancellationTokenSource();
        _ = ReceiveLoopAsync(_cancellation.Token);
        return Task.CompletedTask;
    }

    public Task DiscoverAsync(
        IPAddress? broadcastAddress = null,
        CancellationToken cancellationToken = default) =>
        SendAsync(UdpProtocol.Encode<object>(UdpCommand.DiscoverRequest, Guid.NewGuid()),
            broadcastAddress ?? IPAddress.Broadcast, cancellationToken);

    public static IPAddress CalculateBroadcastAddress(string ipWithPrefix)
    {
        var parts = ipWithPrefix.Trim().Split('/', 2);
        if (!IPAddress.TryParse(parts[0], out var address) ||
            address.AddressFamily != AddressFamily.InterNetwork)
            throw new FormatException("请输入有效的 IPv4 地址。");

        var prefix = 24;
        if (parts.Length == 2 &&
            (!int.TryParse(parts[1], out prefix) || prefix is < 0 or > 32))
            throw new FormatException("CIDR 前缀必须在 0 到 32 之间。");

        var ipBytes = address.GetAddressBytes();
        var mask = prefix == 0 ? 0u : uint.MaxValue << (32 - prefix);
        var ipValue = ((uint)ipBytes[0] << 24) | ((uint)ipBytes[1] << 16) |
                      ((uint)ipBytes[2] << 8) | ipBytes[3];
        var broadcast = ipValue | ~mask;
        return new IPAddress(new[]
        {
            (byte)(broadcast >> 24), (byte)(broadcast >> 16),
            (byte)(broadcast >> 8), (byte)broadcast
        });
    }

    public static string GetPreferredLocalIpv4() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up &&
                        n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
            ?.Address.ToString() ?? "127.0.0.1";

    public Task SendUpdateAsync(
        string targetIp, int targetPort, string targetDeviceId, string updatePath,
        Guid? requestId = null,
        CancellationToken cancellationToken = default)
    {
        var payload = new UpdateRequestPayload(
            Environment.MachineName, targetDeviceId, updatePath);
        return SendAsync(UdpProtocol.Encode(
                UdpCommand.UpdateRequest, requestId ?? Guid.NewGuid(), payload),
            IPAddress.Parse(targetIp), targetPort, cancellationToken);
    }

    public Task SendRollbackAsync(
        string targetIp, int targetPort, string targetDeviceId, string? targetVersion = null,
        Guid? requestId = null,
        CancellationToken cancellationToken = default)
    {
        var payload = new RollbackRequestPayload(
            Environment.MachineName, targetDeviceId, targetVersion);
        return SendAsync(UdpProtocol.Encode(
                UdpCommand.RollbackRequest, requestId ?? Guid.NewGuid(), payload),
            IPAddress.Parse(targetIp), targetPort, cancellationToken);
    }

    public Task<DispatchAcknowledgement> SendUpdateReliableAsync(
        string targetIp,
        int targetPort,
        string targetDeviceId,
        string updatePath,
        Guid requestId,
        Action<int>? attemptStarted = null,
        int maxAttempts = 3,
        TimeSpan? acknowledgementTimeout = null,
        CancellationToken cancellationToken = default)
    {
        var payload = new UpdateRequestPayload(
            Environment.MachineName, targetDeviceId, updatePath);
        var packet = UdpProtocol.Encode(
            UdpCommand.UpdateRequest, requestId, payload);
        return SendReliableAsync(
            packet,
            IPAddress.Parse(targetIp),
            targetPort,
            requestId,
            attemptStarted,
            maxAttempts,
            acknowledgementTimeout ?? TimeSpan.FromSeconds(2),
            cancellationToken);
    }

    public Task<DispatchAcknowledgement> SendRollbackReliableAsync(
        string targetIp,
        int targetPort,
        string targetDeviceId,
        string? targetVersion,
        Guid requestId,
        Action<int>? attemptStarted = null,
        int maxAttempts = 3,
        TimeSpan? acknowledgementTimeout = null,
        CancellationToken cancellationToken = default)
    {
        var payload = new RollbackRequestPayload(
            Environment.MachineName, targetDeviceId, targetVersion);
        var packet = UdpProtocol.Encode(
            UdpCommand.RollbackRequest, requestId, payload);
        return SendReliableAsync(
            packet,
            IPAddress.Parse(targetIp),
            targetPort,
            requestId,
            attemptStarted,
            maxAttempts,
            acknowledgementTimeout ?? TimeSpan.FromSeconds(2),
            cancellationToken);
    }

    private async Task<DispatchAcknowledgement> SendReliableAsync(
        byte[] packet,
        IPAddress target,
        int targetPort,
        Guid requestId,
        Action<int>? attemptStarted,
        int maxAttempts,
        TimeSpan acknowledgementTimeout,
        CancellationToken cancellationToken)
    {
        if (maxAttempts < 1)
            throw new ArgumentOutOfRangeException(
                nameof(maxAttempts), "至少需要发送一次。");

        var completion = new TaskCompletionSource<TaskReceivedPayload>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pendingAcknowledgements.TryAdd(requestId, completion))
            throw new InvalidOperationException($"任务 {requestId:N} 正在等待设备确认。");

        try
        {
            for (var attempt = 1; attempt <= maxAttempts; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                attemptStarted?.Invoke(attempt);
                await SendAsync(packet, target, targetPort, cancellationToken);

                var timeoutTask = Task.Delay(
                    acknowledgementTimeout, cancellationToken);
                var completed = await Task.WhenAny(completion.Task, timeoutTask);
                if (completed == completion.Task)
                {
                    var response = await completion.Task;
                    return new DispatchAcknowledgement(
                        true,
                        true,
                        "设备已收到指令",
                        attempt);
                }
                await timeoutTask;
            }

            return new DispatchAcknowledgement(
                false,
                false,
                $"发送 {maxAttempts} 次后设备仍未确认",
                maxAttempts);
        }
        finally
        {
            _pendingAcknowledgements.TryRemove(requestId, out _);
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var datagram = await _udp!.ReceiveAsync(cancellationToken);
                if (!UdpProtocol.TryDecode(datagram.Buffer, out var packet)) continue;
                switch (packet.Command)
                {
                    case UdpCommand.DiscoverResponse:
                        var device = UdpProtocol.DecodePayload<DiscoverResponsePayload>(packet);
                        if (device is not null)
                            DeviceDiscovered?.Invoke(new DiscoveredDevice(
                                device.DeviceId, device.Name,
                                datagram.RemoteEndPoint.Address.ToString(),
                                device.Version, device.ListenPort));
                        break;
                    case UdpCommand.UpdateAccepted:
                        var accepted = UdpProtocol.DecodePayload<UpdateAcceptedPayload>(packet);
                        if (accepted is not null)
                        {
                            UpdateStatusReceived?.Invoke(new UpdateStatusInfo(
                                packet.RequestId,
                                accepted.DeviceId,
                                accepted.Accepted,
                                accepted.Message,
                                IsFinal: false));
                        }
                        break;
                    case UdpCommand.TaskReceived:
                        var receipt = UdpProtocol.DecodePayload<TaskReceivedPayload>(packet);
                        if (receipt is not null &&
                            _pendingAcknowledgements.TryGetValue(
                                packet.RequestId, out var pending))
                            pending.TrySetResult(receipt);
                        break;
                    case UdpCommand.UpdateResult:
                        var result = UdpProtocol.DecodePayload<UpdateResultPayload>(packet);
                        if (result is not null)
                            UpdateStatusReceived?.Invoke(new UpdateStatusInfo(
                                packet.RequestId,
                                result.DeviceId,
                                result.Success,
                                result.Message,
                                IsFinal: true,
                                result.CurrentVersion));
                        break;
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch
            {
                // 忽略单个无效数据包。
            }
        }
    }

    private async Task SendAsync(
        byte[] packet, IPAddress target, CancellationToken cancellationToken)
        => await SendAsync(packet, target, DevicePort, cancellationToken);

    private async Task SendAsync(
        byte[] packet, IPAddress target, int targetPort, CancellationToken cancellationToken)
    {
        if (_udp is null) throw new InvalidOperationException("UDP 控制服务尚未启动。");
        await _udp.SendAsync(packet, new IPEndPoint(target, targetPort), cancellationToken);
    }

    public void Dispose()
    {
        _cancellation?.Cancel();
        foreach (var pending in _pendingAcknowledgements.Values)
            pending.TrySetCanceled();
        _pendingAcknowledgements.Clear();
        _udp?.Dispose();
        _cancellation?.Dispose();
    }
}
