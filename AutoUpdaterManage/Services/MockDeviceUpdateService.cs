using AutoUpdaterManage.Models;

namespace AutoUpdaterManage.Services;

public sealed class MockDeviceUpdateService : IDeviceUpdateService
{
    public async Task<UpdateDispatchResult> SendUpdateCommandAsync(
        IReadOnlyCollection<DeviceInfo> devices,
        CancellationToken cancellationToken = default)
    {
        await Task.Delay(500, cancellationToken);
        return new UpdateDispatchResult(devices.Count, 0);
    }
}
