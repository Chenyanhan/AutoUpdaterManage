using System.Net;
using System.Net.Sockets;
using AutoUpdater.Protocol;

namespace AutoUpdater.Updater;

internal sealed class UdpResultReporter(UpdaterOptions options)
{
    public async Task ReportAsync(
        bool success, string message, string? currentVersion = null)
    {
        if (options.ControllerAddress is null) return;
        var payload = new UpdateResultPayload(
            options.DeviceId, success, message, currentVersion);
        var packet = UdpProtocol.Encode(UdpCommand.UpdateResult, options.RequestId, payload);
        using var udp = new UdpClient(AddressFamily.InterNetwork);
        await udp.SendAsync(packet,
            new IPEndPoint(options.ControllerAddress, options.ControllerPort));
    }
}
