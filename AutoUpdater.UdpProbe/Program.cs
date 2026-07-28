using System.Net;
using System.Net.Sockets;
using AutoUpdater.Protocol;

var target = args.Length > 0 ? IPAddress.Parse(args[0]) : IPAddress.Loopback;
var port = args.Length > 1 ? int.Parse(args[1]) : 45678;
using var udp = new UdpClient(new IPEndPoint(IPAddress.Any, 0));
var requestId = Guid.NewGuid();
var packet = UdpProtocol.Encode<object>(UdpCommand.DiscoverRequest, requestId);
await udp.SendAsync(packet, new IPEndPoint(target, port));

using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
try
{
    var response = await udp.ReceiveAsync(timeout.Token);
    if (!UdpProtocol.TryDecode(response.Buffer, out var decoded))
        throw new InvalidDataException("响应不是有效的 AutoUpdater UDP 数据包。");
    var device = UdpProtocol.DecodePayload<DiscoverResponsePayload>(decoded);
    Console.WriteLine(
        $"OK {response.RemoteEndPoint} {decoded.Command} " +
        $"{device?.DeviceId} {device?.Version} port={device?.ListenPort}");
    return 0;
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine($"TIMEOUT {target}:{port}");
    return 1;
}
