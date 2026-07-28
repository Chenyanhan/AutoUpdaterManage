using AutoUpdaterManage.Models;

namespace AutoUpdaterManage.Services;

public record UpdateDispatchResult(int SuccessCount, int FailedCount);

public interface IDeviceUpdateService
{
    Task<UpdateDispatchResult> SendUpdateCommandAsync(
        IReadOnlyCollection<DeviceInfo> devices,
        CancellationToken cancellationToken = default);
}
