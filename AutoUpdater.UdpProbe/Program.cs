using System.Net;
using System.Net.Sockets;
using AutoUpdater.Protocol;

if (args.FirstOrDefault()?.Equals(
        "reliability", StringComparison.OrdinalIgnoreCase) == true)
    return await RunReliabilityProbeAsync(args.Skip(1).ToArray());

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

static async Task<int> RunReliabilityProbeAsync(string[] arguments)
{
    var target = arguments.Length > 0
        ? IPAddress.Parse(arguments[0])
        : IPAddress.Loopback;
    var port = arguments.Length > 1 ? int.Parse(arguments[1]) : 45678;
    var deviceId = arguments.Length > 2
        ? arguments[2]
        : $"TEST-{Environment.MachineName}";
    var requestId = arguments.Length > 3
        ? Guid.Parse(arguments[3])
        : Guid.NewGuid();
    var payload = new UpdateRequestPayload(
        "UDP-PROBE", deviceId, @"D:\Nonexistent\manifest.json");
    var packet = UdpProtocol.Encode(
        UdpCommand.UpdateRequest, requestId, payload);

    using var udp = new UdpClient(new IPEndPoint(IPAddress.Any, 0));
    var endpoint = new IPEndPoint(target, port);
    var receipts = 0;
    var decisions = 0;

    for (var send = 1; send <= 2; send++)
    {
        await udp.SendAsync(packet, endpoint);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (!timeout.IsCancellationRequested)
        {
            try
            {
                var response = await udp.ReceiveAsync(timeout.Token);
                if (!UdpProtocol.TryDecode(response.Buffer, out var decoded) ||
                    decoded.RequestId != requestId)
                    continue;
                if (decoded.Command == UdpCommand.TaskReceived) receipts++;
                if (decoded.Command == UdpCommand.UpdateAccepted)
                {
                    decisions++;
                    break;
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    Console.WriteLine(
        $"request={requestId:D} receipts={receipts} decisions={decisions}");
    if (receipts == 2 && decisions == 2)
    {
        Console.WriteLine("RELIABILITY_OK duplicate request was acknowledged twice.");
        return 0;
    }

    Console.Error.WriteLine("RELIABILITY_FAILED");
    return 1;
}
