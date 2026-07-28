using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using AutoUpdaterManage.Models;

namespace AutoUpdaterManage.Services;

/// <summary>
/// 管理端 WebSocket 客户端。服务端需要接收 update.dispatch 消息并返回带相同 requestId 的响应。
/// </summary>
public sealed class WebSocketDeviceUpdateService : IDeviceUpdateService, IDisposable
{
    private readonly Uri _serverUri;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private ClientWebSocket? _socket;
    private CancellationTokenSource? _receiveCancellation;

    public WebSocketDeviceUpdateService(Uri serverUri) => _serverUri = serverUri;

    public event Action<bool, string>? ConnectionStateChanged;
    public event Action<DeviceStatusMessage>? DeviceStatusReceived;

    public bool IsConnected => _socket?.State == WebSocketState.Open;

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (IsConnected) return;

        _socket?.Dispose();
        _receiveCancellation?.Cancel();
        _receiveCancellation?.Dispose();
        _socket = new ClientWebSocket();
        _receiveCancellation = new CancellationTokenSource();
        _socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);

        ConnectionStateChanged?.Invoke(false, $"正在连接 {_serverUri}");
        await _socket.ConnectAsync(_serverUri, cancellationToken);
        ConnectionStateChanged?.Invoke(true, "更新服务已连接");
        _ = ReceiveLoopAsync(_receiveCancellation.Token);
    }

    public async Task<UpdateDispatchResult> SendUpdateCommandAsync(
        IReadOnlyCollection<DeviceInfo> devices,
        CancellationToken cancellationToken = default)
    {
        if (!IsConnected)
            throw new InvalidOperationException("WebSocket 尚未连接，请先连接更新服务。");

        var request = new UpdateDispatchMessage(
            "update.dispatch",
            Guid.NewGuid().ToString("N"),
            DateTimeOffset.UtcNow,
            devices.Select(device => device.DeviceId).ToArray(),
            true);

        var json = JsonSerializer.Serialize(request, JsonOptions);
        var bytes = Encoding.UTF8.GetBytes(json);

        await _sendLock.WaitAsync(cancellationToken);
        try
        {
            await _socket!.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken);
        }
        catch
        {
            ConnectionStateChanged?.Invoke(false, "更新服务连接已断开");
            throw;
        }
        finally
        {
            _sendLock.Release();
        }

        // 该结果表示指令已成功发送到服务端；设备接受/拒绝状态由后续事件消息更新。
        return new UpdateDispatchResult(devices.Count, 0);
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];
        using var message = new MemoryStream();

        try
        {
            while (_socket?.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
            {
                message.SetLength(0);
                WebSocketReceiveResult result;
                do
                {
                    result = await _socket.ReceiveAsync(buffer, cancellationToken);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        ConnectionStateChanged?.Invoke(false, "更新服务连接已关闭");
                        return;
                    }

                    message.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);

                if (result.MessageType == WebSocketMessageType.Text)
                    ProcessMessage(Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length));
            }
        }
        catch (OperationCanceledException)
        {
            // 主动关闭或重连。
        }
        catch (Exception ex)
        {
            ConnectionStateChanged?.Invoke(false, $"连接中断：{ex.Message}");
        }
    }

    private void ProcessMessage(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (!root.TryGetProperty("type", out var type) || type.GetString() != "device.status")
                return;

            var status = JsonSerializer.Deserialize<DeviceStatusMessage>(json, JsonOptions);
            if (status is not null)
                DeviceStatusReceived?.Invoke(status);
        }
        catch (JsonException)
        {
            // 忽略无法识别的消息，避免单条异常数据终止接收循环。
        }
    }

    public void Dispose()
    {
        _receiveCancellation?.Cancel();
        _receiveCancellation?.Dispose();
        _socket?.Dispose();
        _sendLock.Dispose();
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private sealed record UpdateDispatchMessage(
        string Type,
        string RequestId,
        DateTimeOffset SentAt,
        string[] DeviceIds,
        bool RequireConfirmation);
}

public sealed record DeviceStatusMessage(
    string Type,
    string DeviceId,
    string Name,
    string IpAddress,
    string CurrentVersion,
    string Status,
    DateTimeOffset LastSeen);
