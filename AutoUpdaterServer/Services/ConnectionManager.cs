using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace AutoUpdaterServer.Services;

public sealed class ConnectionManager
{
    private readonly ConcurrentDictionary<Guid, WebSocket> _managers = new();
    private readonly ConcurrentDictionary<string, DeviceConnection> _devices =
        new(StringComparer.OrdinalIgnoreCase);

    public int ManagerCount => _managers.Count;
    public int DeviceCount => _devices.Count;

    public async Task HandleManagerAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        var connectionId = Guid.NewGuid();
        _managers[connectionId] = socket;

        try
        {
            await SendDeviceSnapshotAsync(socket, cancellationToken);
            await ReceiveMessagesAsync(socket,
                json => HandleManagerMessageAsync(json, cancellationToken),
                cancellationToken);
        }
        finally
        {
            _managers.TryRemove(connectionId, out _);
            await CloseSafelyAsync(socket);
        }
    }

    public async Task HandleDeviceAsync(
        string deviceId,
        WebSocket socket,
        CancellationToken cancellationToken)
    {
        var connection = new DeviceConnection(deviceId, socket);
        if (_devices.TryGetValue(deviceId, out var previous))
            await CloseSafelyAsync(previous.Socket);

        _devices[deviceId] = connection;
        await BroadcastDeviceStatusAsync(connection, "online", cancellationToken);

        try
        {
            await ReceiveMessagesAsync(socket,
                json => HandleDeviceMessageAsync(connection, json, cancellationToken),
                cancellationToken);
        }
        finally
        {
            _devices.TryRemove(new KeyValuePair<string, DeviceConnection>(deviceId, connection));
            await BroadcastDeviceStatusAsync(connection, "offline", CancellationToken.None);
            await CloseSafelyAsync(socket);
        }
    }

    private async Task HandleManagerMessageAsync(string json, CancellationToken cancellationToken)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (!root.TryGetProperty("type", out var type) || type.GetString() != "update.dispatch")
            return;

        if (!root.TryGetProperty("deviceIds", out var ids) || ids.ValueKind != JsonValueKind.Array)
            return;

        foreach (var idElement in ids.EnumerateArray())
        {
            var deviceId = idElement.GetString();
            if (deviceId is not null && _devices.TryGetValue(deviceId, out var device))
                await SendTextAsync(device.Socket, json, cancellationToken);
        }
    }

    private async Task HandleDeviceMessageAsync(
        DeviceConnection connection,
        string json,
        CancellationToken cancellationToken)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (!root.TryGetProperty("type", out var type))
            return;

        if (type.GetString() == "device.status")
        {
            connection.UpdateFrom(root);
            await BroadcastTextAsync(json, cancellationToken);
        }
        else if (type.GetString() == "update.status")
        {
            await BroadcastTextAsync(json, cancellationToken);
        }
    }

    private async Task SendDeviceSnapshotAsync(WebSocket manager, CancellationToken cancellationToken)
    {
        foreach (var device in _devices.Values)
        {
            var json = CreateDeviceStatusJson(device, "online");
            await SendTextAsync(manager, json, cancellationToken);
        }
    }

    private Task BroadcastDeviceStatusAsync(
        DeviceConnection device,
        string status,
        CancellationToken cancellationToken) =>
        BroadcastTextAsync(CreateDeviceStatusJson(device, status), cancellationToken);

    private static string CreateDeviceStatusJson(DeviceConnection device, string status) =>
        JsonSerializer.Serialize(new
        {
            type = "device.status",
            deviceId = device.DeviceId,
            name = device.Name,
            ipAddress = device.IpAddress,
            currentVersion = device.CurrentVersion,
            status,
            lastSeen = DateTimeOffset.UtcNow
        });

    private async Task BroadcastTextAsync(string json, CancellationToken cancellationToken)
    {
        foreach (var manager in _managers.ToArray())
        {
            try
            {
                await SendTextAsync(manager.Value, json, cancellationToken);
            }
            catch
            {
                _managers.TryRemove(manager.Key, out _);
            }
        }
    }

    private static async Task ReceiveMessagesAsync(
        WebSocket socket,
        Func<string, Task> handler,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];
        using var stream = new MemoryStream();

        while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
        {
            stream.SetLength(0);
            WebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(buffer, cancellationToken);
                if (result.MessageType == WebSocketMessageType.Close)
                    return;
                stream.Write(buffer, 0, result.Count);
            } while (!result.EndOfMessage);

            if (result.MessageType == WebSocketMessageType.Text)
            {
                try
                {
                    await handler(Encoding.UTF8.GetString(stream.GetBuffer(), 0, (int)stream.Length));
                }
                catch (JsonException)
                {
                    // 单条无效 JSON 不影响连接。
                }
            }
        }
    }

    private static async Task SendTextAsync(
        WebSocket socket,
        string json,
        CancellationToken cancellationToken)
    {
        if (socket.State != WebSocketState.Open)
            return;
        var bytes = Encoding.UTF8.GetBytes(json);
        await socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken);
    }

    private static async Task CloseSafelyAsync(WebSocket socket)
    {
        try
        {
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "closed", CancellationToken.None);
        }
        catch (WebSocketException)
        {
        }
        finally
        {
            socket.Dispose();
        }
    }

    private sealed class DeviceConnection(string deviceId, WebSocket socket)
    {
        public string DeviceId { get; } = deviceId;
        public WebSocket Socket { get; } = socket;
        public string Name { get; private set; } = deviceId;
        public string IpAddress { get; private set; } = string.Empty;
        public string CurrentVersion { get; private set; } = string.Empty;

        public void UpdateFrom(JsonElement message)
        {
            if (message.TryGetProperty("name", out var name))
                Name = name.GetString() ?? Name;
            if (message.TryGetProperty("ipAddress", out var ip))
                IpAddress = ip.GetString() ?? IpAddress;
            if (message.TryGetProperty("currentVersion", out var version))
                CurrentVersion = version.GetString() ?? CurrentVersion;
        }
    }
}
