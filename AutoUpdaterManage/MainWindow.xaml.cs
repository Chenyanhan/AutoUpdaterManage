using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using DevExpress.Xpf.Core;
using AutoUpdaterManage.Models;
using AutoUpdaterManage.Services;
using MessageBox = System.Windows.MessageBox;

namespace AutoUpdaterManage;

public partial class MainWindow : ThemedWindow
{
    private readonly UdpControllerService _controllerService = new();
    private readonly TaskHistoryStore _taskHistoryStore = new();
    private readonly ICollectionView _deviceView;
    private readonly ICollectionView _taskView;
    private System.Net.IPAddress? _broadcastAddress;

    public ObservableCollection<DeviceInfo> Devices { get; } = [];
    public ObservableCollection<UpdateTaskRecord> Tasks { get; } = [];

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;
        _deviceView = CollectionViewSource.GetDefaultView(Devices);
        _deviceView.Filter = FilterDevice;
        _taskView = CollectionViewSource.GetDefaultView(Tasks);
        _taskView.Filter = FilterTask;

        _controllerService.DeviceDiscovered += ApplyDiscoveredDevice;
        _controllerService.UpdateStatusReceived += ApplyUpdateStatus;
        _taskHistoryStore.PersistenceError += _ =>
            Dispatcher.BeginInvoke(() =>
                TaskStorageStatusText.Text = "任务记录暂时无法写入，但不影响设备操作");

        Loaded += async (_, _) =>
        {
            await LoadTaskHistoryAsync();
            try
            {
                await _controllerService.StartAsync();
                LocalIpBox.Text = $"{UdpControllerService.GetPreferredLocalIpv4()}/24";
                await DiscoverAsync();
            }
            catch (Exception ex)
            {
                ConnectionText.Text = $"UDP 启动失败：{ex.Message}";
            }
        };
        Closed += (_, _) =>
        {
            _controllerService.Dispose();
            _taskHistoryStore.Dispose();
        };
        UpdateSummary();
    }

    private bool FilterDevice(object item)
    {
        if (item is not DeviceInfo device) return false;
        var keyword = SearchBox?.Text?.Trim() ?? "";
        var matchesKeyword = string.IsNullOrEmpty(keyword)
            || device.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase)
            || device.DeviceId.Contains(keyword, StringComparison.OrdinalIgnoreCase)
            || device.IpAddress.Contains(keyword, StringComparison.OrdinalIgnoreCase);
        var selectedStatus = (StatusFilter?.SelectedItem as ComboBoxItem)?.Content?.ToString();
        return matchesKeyword && (selectedStatus is null or "全部状态" || device.StatusText == selectedStatus);
    }

    private void Filter_Changed(object sender, EventArgs e)
    {
        if (_deviceView is null) return;
        _deviceView.Refresh();
        UpdateSummary();
    }

    private bool FilterTask(object item)
    {
        if (item is not UpdateTaskRecord task) return false;
        var keyword = TaskSearchBox?.Text?.Trim() ?? "";
        var matchesKeyword = string.IsNullOrWhiteSpace(keyword)
            || task.DeviceName.Contains(keyword, StringComparison.OrdinalIgnoreCase)
            || task.DeviceId.Contains(keyword, StringComparison.OrdinalIgnoreCase)
            || task.IpAddress.Contains(keyword, StringComparison.OrdinalIgnoreCase)
            || task.Message.Contains(keyword, StringComparison.OrdinalIgnoreCase)
            || task.ShortId.Contains(keyword, StringComparison.OrdinalIgnoreCase);
        var selectedState =
            (TaskStatusFilter?.SelectedItem as ComboBoxItem)?.Content?.ToString();
        return matchesKeyword &&
               (selectedState is null or "全部状态" ||
                task.StateText == selectedState);
    }

    private void TaskFilter_Changed(object sender, EventArgs e)
    {
        if (_taskView is null) return;
        _taskView.Refresh();
        UpdateTaskSummary();
    }

    private async void RefreshTasks_Click(object sender, RoutedEventArgs e) =>
        await LoadTaskHistoryAsync();

    private async Task LoadTaskHistoryAsync()
    {
        var records = await _taskHistoryStore.LoadAsync();
        Tasks.Clear();
        foreach (var record in records)
            Tasks.Add(record);
        _taskView.Refresh();
        UpdateTaskSummary();
        TaskStorageStatusText.Text =
            $"本地记录：{_taskHistoryStore.DatabasePath}";
    }

    private async void Discover_Click(object sender, RoutedEventArgs e) => await DiscoverAsync();

    private async Task DiscoverAsync()
    {
        foreach (var device in Devices)
            device.Status = DeviceStatus.Offline;
        if (_broadcastAddress is null)
        {
            MessageBox.Show("请先输入正确的本机 IP 和子网前缀，例如 192.168.1.25/24。",
                "广播地址无效", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        await _controllerService.DiscoverAsync(_broadcastAddress);
        UpdateSummary();
    }

    private void LocalIpBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        try
        {
            _broadcastAddress = UdpControllerService.CalculateBroadcastAddress(LocalIpBox.Text);
            BroadcastAddressText.Text = _broadcastAddress.ToString();
            LocalIpBox.ClearValue(ToolTipProperty);
        }
        catch (FormatException ex)
        {
            _broadcastAddress = null;
            BroadcastAddressText.Text = "输入无效";
            LocalIpBox.ToolTip = ex.Message;
        }
    }

    private async void SendUpdate_Click(object sender, RoutedEventArgs e)
    {
        var selected = Devices.Where(d => d.IsSelected && d.Status == DeviceStatus.Online).ToList();
        if (selected.Count == 0)
        {
            MessageBox.Show("请先选择在线设备。", "未选择设备", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var updatePath = UpdatePathBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(updatePath))
        {
            MessageBox.Show("请输入设备能够访问的 manifest.json 路径或 HTTP 地址。", "缺少更新清单",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (MessageBox.Show($"向 {selected.Count} 台设备下发更新？\n更新来源：{updatePath}",
                "确认下发", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        foreach (var device in selected)
        {
            var requestId = Guid.NewGuid();
            var task = CreateTaskRecord(
                requestId,
                device,
                UpdateTaskOperation.Update,
                updatePath,
                targetVersion: null);
            AddTask(task);
            try
            {
                await _controllerService.SendUpdateAsync(
                    device.IpAddress, device.UdpPort, device.DeviceId, updatePath,
                    requestId);
                if (task.State == UpdateTaskState.Dispatching)
                {
                    task.ApplyStatus(UpdateTaskState.Sent, "更新指令已下发");
                    _ = _taskHistoryStore.SaveAsync(task);
                }
                device.UpdateResult = "指令已发送";
            }
            catch (Exception ex)
            {
                device.UpdateResult = $"发送失败：{ex.Message}";
                task.ApplyStatus(UpdateTaskState.Failed, device.UpdateResult);
                _ = _taskHistoryStore.SaveAsync(task);
            }
        }
    }

    private void OpenPackageBuilder_Click(object sender, RoutedEventArgs e)
    {
        var window = new PackageBuilderWindow { Owner = this };
        if (window.ShowDialog() == true &&
            !string.IsNullOrWhiteSpace(window.GeneratedManifestPath))
            UpdatePathBox.Text = window.GeneratedManifestPath;
    }

    private async void Rollback_Click(object sender, RoutedEventArgs e)
    {
        var selected = Devices.Where(d => d.IsSelected && d.Status == DeviceStatus.Online).ToList();
        if (selected.Count == 0)
        {
            MessageBox.Show("请先选择需要回退的在线设备。", "未选择设备",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (MessageBox.Show(
                $"确认让 {selected.Count} 台设备回退到最近一次备份版本？",
                "确认版本回退", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        foreach (var device in selected)
        {
            var requestId = Guid.NewGuid();
            var task = CreateTaskRecord(
                requestId,
                device,
                UpdateTaskOperation.Rollback,
                source: null,
                targetVersion: null);
            AddTask(task);
            try
            {
                await _controllerService.SendRollbackAsync(
                    device.IpAddress, device.UdpPort, device.DeviceId,
                    targetVersion: null, requestId);
                if (task.State == UpdateTaskState.Dispatching)
                {
                    task.ApplyStatus(UpdateTaskState.Sent, "回退指令已下发");
                    _ = _taskHistoryStore.SaveAsync(task);
                }
                device.UpdateResult = "回退指令已发送";
            }
            catch (Exception ex)
            {
                device.UpdateResult = $"回退发送失败：{ex.Message}";
                task.ApplyStatus(UpdateTaskState.Failed, device.UpdateResult);
                _ = _taskHistoryStore.SaveAsync(task);
            }
        }
    }

    private void ApplyDiscoveredDevice(DiscoveredDevice info)
    {
        Dispatcher.Invoke(() =>
        {
            var device = Devices.FirstOrDefault(d => d.DeviceId == info.DeviceId);
            if (device is null)
            {
                device = new DeviceInfo(info.DeviceId, info.Name, info.IpAddress,
                    info.Version, DeviceStatus.Online, DateTime.Now, info.ListenPort);
                device.PropertyChanged += (_, args) =>
                {
                    if (args.PropertyName == nameof(DeviceInfo.IsSelected)) Dispatcher.Invoke(UpdateSelection);
                };
                Devices.Add(device);
            }
            else
            {
                device.UpdateRuntimeState(info.Name, info.IpAddress, info.Version,
                    DeviceStatus.Online, DateTime.Now, info.ListenPort);
            }
            _deviceView.Refresh();
            UpdateSummary();
        });
    }

    private void ApplyUpdateStatus(UpdateStatusInfo status)
    {
        Dispatcher.Invoke(() =>
        {
            var task = Tasks.FirstOrDefault(t => t.RequestId == status.RequestId);
            if (task is not null)
            {
                var state = status.IsFinal
                    ? status.Success
                        ? UpdateTaskState.Succeeded
                        : UpdateTaskState.Failed
                    : status.Success
                        ? UpdateTaskState.Accepted
                        : UpdateTaskState.Postponed;
                task.ApplyStatus(state, status.Message, status.CurrentVersion);
                _ = _taskHistoryStore.SaveAsync(task);
                _taskView.Refresh();
                UpdateTaskSummary();
            }

            var device = Devices.FirstOrDefault(d => d.DeviceId == status.DeviceId);
            if (device is not null)
            {
                device.UpdateResult = status.IsFinal && !status.Success
                    ? $"更新失败：{status.Message}"
                    : status.Message;
                if (status.Success && !string.IsNullOrWhiteSpace(status.CurrentVersion))
                    device.SetCurrentVersion(status.CurrentVersion);
            }
        });
    }

    private UpdateTaskRecord CreateTaskRecord(
        Guid requestId,
        DeviceInfo device,
        UpdateTaskOperation operation,
        string? source,
        string? targetVersion)
    {
        var now = DateTime.Now;
        return new UpdateTaskRecord(
            requestId,
            device.DeviceId,
            device.Name,
            device.IpAddress,
            operation,
            source,
            device.CurrentVersion,
            targetVersion,
            UpdateTaskState.Dispatching,
            operation == UpdateTaskOperation.Update
                ? "正在下发更新指令"
                : "正在下发回退指令",
            now,
            now);
    }

    private void AddTask(UpdateTaskRecord task)
    {
        Tasks.Insert(0, task);
        _ = _taskHistoryStore.SaveAsync(task);
        _taskView.Refresh();
        UpdateTaskSummary();
    }

    private void UpdateTaskSummary()
    {
        if (TaskSummaryText is null) return;
        TaskSummaryText.Text =
            $"共 {Tasks.Count} 条 · 成功 {Tasks.Count(t => t.State == UpdateTaskState.Succeeded)} 条" +
            $" · 稍后 {Tasks.Count(t => t.State == UpdateTaskState.Postponed)} 条" +
            $" · 失败 {Tasks.Count(t => t.State == UpdateTaskState.Failed)} 条";
    }

    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (DeviceInfo device in _deviceView.Cast<object>()) device.IsSelected = true;
    }
    private void InvertSelection_Click(object sender, RoutedEventArgs e)
    {
        foreach (DeviceInfo device in _deviceView.Cast<object>()) device.IsSelected = !device.IsSelected;
    }
    private void ClearSelection_Click(object sender, RoutedEventArgs e)
    {
        foreach (var device in Devices) device.IsSelected = false;
    }

    private void HeaderSelectAll_Checked(object sender, RoutedEventArgs e)
    {
        foreach (DeviceInfo device in _deviceView.Cast<object>())
            device.IsSelected = true;
    }

    private void HeaderSelectAll_Unchecked(object sender, RoutedEventArgs e)
    {
        foreach (DeviceInfo device in _deviceView.Cast<object>())
            device.IsSelected = false;
    }
    private void UpdateSelection()
    {
        var count = Devices.Count(d => d.IsSelected);
        SelectionText.Text = count == 0 ? "尚未选择设备" : $"已选择 {count} 台设备";
    }
    private void UpdateSummary()
    {
        if (SummaryText is null) return;
        SummaryText.Text = $"共 {Devices.Count} 台 · 在线 {Devices.Count(d => d.Status == DeviceStatus.Online)} 台";
        UpdateSelection();
    }
}
