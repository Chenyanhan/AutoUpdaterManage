using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace AutoUpdaterManage.Models;

public enum UpdateTaskOperation
{
    Update,
    Rollback,
    DatabaseSync
}

public enum UpdateTaskState
{
    Dispatching,
    Sent,
    Accepted,
    Postponed,
    Succeeded,
    Failed,
    NoResponse
}

public sealed class UpdateTaskRecord : INotifyPropertyChanged
{
    private UpdateTaskState _state;
    private string _message;
    private string? _resultVersion;
    private DateTime _updatedAt;
    private int _attemptCount;
    private DateTime? _lastSentAt;

    public UpdateTaskRecord(
        Guid requestId,
        string deviceId,
        string deviceName,
        string ipAddress,
        UpdateTaskOperation operation,
        string? source,
        string? sourceVersion,
        string? targetVersion,
        UpdateTaskState state,
        string message,
        DateTime createdAt,
        DateTime updatedAt,
        string? resultVersion = null,
        int attemptCount = 0,
        DateTime? lastSentAt = null)
    {
        RequestId = requestId;
        DeviceId = deviceId;
        DeviceName = deviceName;
        IpAddress = ipAddress;
        Operation = operation;
        Source = source;
        SourceVersion = sourceVersion;
        TargetVersion = targetVersion;
        _state = state;
        _message = message;
        CreatedAt = createdAt;
        _updatedAt = updatedAt;
        _resultVersion = resultVersion;
        _attemptCount = attemptCount;
        _lastSentAt = lastSentAt;
    }

    public Guid RequestId { get; }
    public string ShortId => RequestId.ToString("N")[..8];
    public string DeviceId { get; }
    public string DeviceName { get; }
    public string IpAddress { get; }
    public UpdateTaskOperation Operation { get; }
    public string OperationText => Operation switch
    {
        UpdateTaskOperation.Update => "软件更新",
        UpdateTaskOperation.Rollback => "版本回退",
        UpdateTaskOperation.DatabaseSync => "数据库同步",
        _ => "未知"
    };
    public string? Source { get; }
    public string? SourceVersion { get; }
    public string? TargetVersion { get; }
    public DateTime CreatedAt { get; }
    public string CreatedAtText => CreatedAt.ToString("yyyy-MM-dd HH:mm:ss");
    public DateTime UpdatedAt => _updatedAt;
    public string UpdatedAtText => UpdatedAt.ToString("yyyy-MM-dd HH:mm:ss");
    public UpdateTaskState State => _state;
    public string StateText => State switch
    {
        UpdateTaskState.Dispatching => "正在下发",
        UpdateTaskState.Sent => "已下发",
        UpdateTaskState.Accepted => "设备已接受",
        UpdateTaskState.Postponed => "稍后更新",
        UpdateTaskState.Succeeded => "成功",
        UpdateTaskState.Failed => "失败",
        UpdateTaskState.NoResponse => "设备无响应",
        _ => "未知"
    };
    public string Message => _message;
    public string? ResultVersion => _resultVersion;
    public int AttemptCount => _attemptCount;
    public string AttemptText => _attemptCount == 0 ? "—" : $"{_attemptCount}/3";
    public DateTime? LastSentAt => _lastSentAt;
    public string LastSentAtText => _lastSentAt?.ToString("HH:mm:ss") ?? "—";

    public event PropertyChangedEventHandler? PropertyChanged;

    public void ApplyStatus(
        UpdateTaskState state,
        string message,
        string? resultVersion = null,
        DateTime? updatedAt = null)
    {
        _state = state;
        _message = message;
        _resultVersion = resultVersion ?? _resultVersion;
        _updatedAt = updatedAt ?? DateTime.Now;
        OnPropertyChanged(nameof(State));
        OnPropertyChanged(nameof(StateText));
        OnPropertyChanged(nameof(Message));
        OnPropertyChanged(nameof(ResultVersion));
        OnPropertyChanged(nameof(UpdatedAt));
        OnPropertyChanged(nameof(UpdatedAtText));
    }

    public void RecordAttempt(int attempt, DateTime? sentAt = null)
    {
        _attemptCount = attempt;
        _lastSentAt = sentAt ?? DateTime.Now;
        _updatedAt = _lastSentAt.Value;
        _message = attempt == 1
            ? "正在发送指令"
            : $"第 {attempt} 次重试发送";
        OnPropertyChanged(nameof(AttemptCount));
        OnPropertyChanged(nameof(AttemptText));
        OnPropertyChanged(nameof(LastSentAt));
        OnPropertyChanged(nameof(LastSentAtText));
        OnPropertyChanged(nameof(Message));
        OnPropertyChanged(nameof(UpdatedAt));
        OnPropertyChanged(nameof(UpdatedAtText));
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
