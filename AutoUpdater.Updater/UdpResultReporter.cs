using System.Net;
using System.Net.Sockets;
using AutoUpdater.Protocol;

namespace AutoUpdater.Updater;

internal sealed class UdpResultReporter(UpdaterOptions options)
{
    private readonly object _progressGate = new();
    private string? _lastStage;
    private int _lastPercentage = -1;
    private DateTime _lastProgressSentAt = DateTime.MinValue;

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

    public void ReportProgress(UpdateProgress progress)
    {
        if (options.ControllerAddress is null) return;
        lock (_progressGate)
        {
            var stageChanged = !string.Equals(
                _lastStage, progress.Stage.ToString(), StringComparison.Ordinal);
            var enoughProgress = progress.Percentage - _lastPercentage >= 5;
            var enoughTime =
                DateTime.UtcNow - _lastProgressSentAt >= TimeSpan.FromSeconds(1);
            if (!stageChanged && !enoughProgress && !enoughTime) return;
            _lastStage = progress.Stage.ToString();
            _lastPercentage = progress.Percentage;
            _lastProgressSentAt = DateTime.UtcNow;
        }
        _ = SendProgressSafelyAsync(progress);
    }

    private async Task SendProgressSafelyAsync(UpdateProgress progress)
    {
        try
        {
            var payload = new TaskProgressPayload(
                options.DeviceId,
                progress.Stage.ToString(),
                progress.Percentage,
                progress.Message,
                progress.Detail,
                DateTimeOffset.UtcNow);
            var packet = UdpProtocol.Encode(
                UdpCommand.TaskProgress, options.RequestId, payload);
            using var udp = new UdpClient(AddressFamily.InterNetwork);
            await udp.SendAsync(packet,
                new IPEndPoint(options.ControllerAddress!, options.ControllerPort));
        }
        catch
        {
            // Progress reporting is diagnostic and must never interrupt an update.
        }
    }
}
