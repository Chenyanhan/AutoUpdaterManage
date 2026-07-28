using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace AutoUpdaterManage.Models;

public enum DeviceStatus
{
    Online,
    Offline,
    Updating
}

public sealed class DeviceInfo : INotifyPropertyChanged
{
    private bool _isSelected;
    private string _updateResult = "—";
    private DeviceStatus _status;

    public DeviceInfo(string deviceId, string name, string ipAddress, string currentVersion,
        DeviceStatus status, DateTime lastSeen, int udpPort = 45678)
    {
        DeviceId = deviceId;
        Name = name;
        IpAddress = ipAddress;
        CurrentVersion = currentVersion;
        Status = status;
        LastSeen = lastSeen;
        UdpPort = udpPort;
    }

    public string DeviceId { get; }
    public string Name { get; private set; }
    public string IpAddress { get; private set; }
    public string CurrentVersion { get; private set; }
    public int UdpPort { get; private set; }
    public DeviceStatus Status
    {
        get => _status;
        set
        {
            if (_status == value) return;
            _status = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(StatusText));
        }
    }
    public DateTime LastSeen { get; set; }
    public string StatusText => Status switch
    {
        DeviceStatus.Online => "在线",
        DeviceStatus.Offline => "离线",
        DeviceStatus.Updating => "更新中",
        _ => "未知"
    };
    public string LastSeenText => LastSeen.ToString("yyyy-MM-dd HH:mm");
    public string UpdateResult
    {
        get => _updateResult;
        set
        {
            if (_updateResult == value) return;
            _updateResult = value;
            OnPropertyChanged();
        }
    }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public void UpdateRuntimeState(string name, string ipAddress, string currentVersion,
        DeviceStatus status, DateTime lastSeen, int? udpPort = null)
    {
        Name = name;
        IpAddress = ipAddress;
        CurrentVersion = currentVersion;
        Status = status;
        LastSeen = lastSeen;
        if (udpPort.HasValue) UdpPort = udpPort.Value;
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(IpAddress));
        OnPropertyChanged(nameof(CurrentVersion));
        OnPropertyChanged(nameof(Status));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(LastSeenText));
        OnPropertyChanged(nameof(UdpPort));
    }

    public void SetCurrentVersion(string currentVersion)
    {
        if (CurrentVersion == currentVersion) return;
        CurrentVersion = currentVersion;
        OnPropertyChanged(nameof(CurrentVersion));
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
