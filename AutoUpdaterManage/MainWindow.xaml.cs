using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using DevExpress.Xpf.Core;
using AutoUpdaterManage.Models;
using AutoUpdaterManage.Services;
using MessageBox = System.Windows.MessageBox;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;

namespace AutoUpdaterManage;

public partial class MainWindow : ThemedWindow
{
    private readonly UdpControllerService _controllerService = new();
    private readonly TaskHistoryStore _taskHistoryStore = new();
    private readonly IDatabaseProvider _databaseProvider =
        new SqliteDatabaseProvider();
    private readonly ICollectionView _deviceView;
    private readonly ICollectionView _taskView;
    private System.Net.IPAddress? _broadcastAddress;
    private DatabaseTableInfo? _currentDatabaseTable;
    private int _databasePageNumber = 1;
    private const int DatabasePageSize = 100;
    private DatabasePage? _databasePage;

    public ObservableCollection<DeviceInfo> Devices { get; } = [];
    public ObservableCollection<UpdateTaskRecord> Tasks { get; } = [];
    public ObservableCollection<DatabaseChangeDraft> DatabaseDrafts { get; } = [];

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
        _controllerService.TaskProgressReceived += ApplyTaskProgress;
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
            _ = _databaseProvider.DisposeAsync();
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

    private void TaskGrid_MouseDoubleClick(
        object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (TaskGrid.SelectedItem is not UpdateTaskRecord task) return;
        new TaskDetailWindow(task, _taskHistoryStore)
        {
            Owner = this
        }.ShowDialog();
    }

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
                var acknowledgement = await _controllerService.SendUpdateReliableAsync(
                    device.IpAddress, device.UdpPort, device.DeviceId, updatePath,
                    requestId,
                    attempt =>
                    {
                        task.RecordAttempt(attempt);
                        _ = _taskHistoryStore.SaveAsync(task);
                        AddTaskEvent(
                            task,
                            attempt == 1 ? "Dispatch" : "Retry",
                            null,
                            attempt == 1
                                ? "正在下发指令"
                                : $"第 {attempt} 次重试发送");
                    });
                if (!acknowledgement.Received)
                {
                    task.ApplyStatus(
                        UpdateTaskState.NoResponse, acknowledgement.Message);
                    _ = _taskHistoryStore.SaveAsync(task);
                    device.UpdateResult = acknowledgement.Message;
                }
                else if (task.State == UpdateTaskState.Dispatching)
                {
                    task.ApplyStatus(
                        UpdateTaskState.Sent, "设备已收到指令，等待用户确认");
                    _ = _taskHistoryStore.SaveAsync(task);
                    device.UpdateResult = "设备已收到指令，等待用户确认";
                    AddTaskEvent(
                        task, "Delivered", null, device.UpdateResult);
                }
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

    private void BrowseDatabase_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择SQLite数据库",
            Filter = "SQLite数据库 (*.db;*.sqlite;*.sqlite3)|*.db;*.sqlite;*.sqlite3|所有文件 (*.*)|*.*",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) == true)
            DatabasePathBox.Text = dialog.FileName;
    }

    private async void ConnectDatabase_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            DatabaseStatusText.Text = "正在连接SQLite数据库…";
            await _databaseProvider.ConnectAsync(DatabasePathBox.Text.Trim());
            var tables = await _databaseProvider.GetTablesAsync();
            DatabaseTablesList.ItemsSource = tables;
            DatabaseConnectionText.Text =
                $"SQLite · {tables.Count} 张表";
            DatabaseStatusText.Text =
                $"连接成功：{DatabasePathBox.Text.Trim()}";
            _currentDatabaseTable = null;
            DatabaseDataGrid.ItemsSource = null;
            CurrentTableText.Text = "请选择数据表";
            DatabasePageText.Text = "第 0 / 0 页";
        }
        catch (Exception ex)
        {
            DatabaseStatusText.Text = $"连接失败：{ex.Message}";
            MessageBox.Show(
                ex.Message,
                "数据库连接失败",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private async void DatabaseTable_SelectionChanged(
        object sender, SelectionChangedEventArgs e)
    {
        if (DatabaseTablesList.SelectedItem is not DatabaseTableInfo table)
            return;
        _currentDatabaseTable = table;
        _databasePageNumber = 1;
        await LoadDatabasePageAsync();
    }

    private async Task LoadDatabasePageAsync()
    {
        if (_currentDatabaseTable is null) return;
        try
        {
            DatabaseStatusText.Text =
                $"正在读取 {_currentDatabaseTable.Name}…";
            _databasePage = await _databaseProvider.QueryPageAsync(
                _currentDatabaseTable.Name,
                _databasePageNumber,
                DatabasePageSize);
            DatabaseDataGrid.ItemsSource = _databasePage.Data.DefaultView;
            CurrentTableText.Text =
                $"{_currentDatabaseTable.Name} · {_databasePage.TotalRows} 行";
            DatabasePageText.Text =
                $"第 {_databasePage.PageNumber} / {_databasePage.TotalPages} 页";
            DatabaseStatusText.Text =
                $"已读取 {_databasePage.Data.Rows.Count} 行，每页最多 {DatabasePageSize} 行";
        }
        catch (Exception ex)
        {
            DatabaseStatusText.Text = $"读取失败：{ex.Message}";
        }
    }

    private async void PreviousDatabasePage_Click(
        object sender, RoutedEventArgs e)
    {
        if (_databasePageNumber <= 1) return;
        _databasePageNumber--;
        await LoadDatabasePageAsync();
    }

    private async void NextDatabasePage_Click(
        object sender, RoutedEventArgs e)
    {
        if (_databasePage is null ||
            _databasePageNumber >= _databasePage.TotalPages)
            return;
        _databasePageNumber++;
        await LoadDatabasePageAsync();
    }

    private async void AddDatabaseDraft_Click(
        object sender, RoutedEventArgs e)
    {
        if (_currentDatabaseTable is null)
        {
            MessageBox.Show(
                "请先选择数据表。",
                "尚未选择数据表",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }
        try
        {
            var columns = await _databaseProvider.GetColumnsAsync(
                _currentDatabaseTable.Name);
            var window = new DatabaseRowDraftWindow(
                _currentDatabaseTable.Name, columns)
            {
                Owner = this
            };
            if (window.ShowDialog() == true && window.Draft is not null)
            {
                DatabaseDrafts.Add(window.Draft);
                DatabaseStatusText.Text =
                    $"已添加草稿，待同步变更共 {DatabaseDrafts.Count} 条";
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                ex.Message,
                "无法创建数据草稿",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void RemoveDatabaseDraft_Click(
        object sender, RoutedEventArgs e)
    {
        if (DatabaseDraftsList.SelectedItem is not DatabaseChangeDraft draft)
            return;
        DatabaseDrafts.Remove(draft);
        DatabaseStatusText.Text =
            $"待同步变更共 {DatabaseDrafts.Count} 条";
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
                var acknowledgement = await _controllerService.SendRollbackReliableAsync(
                    device.IpAddress, device.UdpPort, device.DeviceId,
                    targetVersion: null,
                    requestId,
                    attempt =>
                    {
                        task.RecordAttempt(attempt);
                        _ = _taskHistoryStore.SaveAsync(task);
                        AddTaskEvent(
                            task,
                            attempt == 1 ? "Dispatch" : "Retry",
                            null,
                            attempt == 1
                                ? "正在下发指令"
                                : $"第 {attempt} 次重试发送");
                    });
                if (!acknowledgement.Received)
                {
                    task.ApplyStatus(
                        UpdateTaskState.NoResponse, acknowledgement.Message);
                    _ = _taskHistoryStore.SaveAsync(task);
                    device.UpdateResult = acknowledgement.Message;
                }
                else if (task.State == UpdateTaskState.Dispatching)
                {
                    task.ApplyStatus(
                        UpdateTaskState.Sent, "设备已收到指令，等待用户确认");
                    _ = _taskHistoryStore.SaveAsync(task);
                    device.UpdateResult = "设备已收到指令，等待用户确认";
                    AddTaskEvent(
                        task, "Delivered", null, device.UpdateResult);
                }
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
                AddTaskEvent(
                    task,
                    status.IsFinal ? (status.Success ? "Completed" : "Failed")
                        : status.Success ? "Accepted" : "Postponed",
                    status.IsFinal && status.Success ? 100 : null,
                    status.Message,
                    status.CurrentVersion is null
                        ? null
                        : $"当前版本：{status.CurrentVersion}");
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

    private void ApplyTaskProgress(TaskProgressInfo progress)
    {
        Dispatcher.Invoke(() =>
        {
            var task = Tasks.FirstOrDefault(
                item => item.RequestId == progress.RequestId);
            if (task is null) return;
            task.ApplyStatus(
                task.State,
                $"{progress.Percentage}% {progress.Message}",
                updatedAt: progress.OccurredAt.LocalDateTime);
            _ = _taskHistoryStore.SaveAsync(task);
            AddTaskEvent(
                task,
                progress.Stage,
                progress.Percentage,
                progress.Message,
                progress.Detail,
                progress.OccurredAt.LocalDateTime);
            _taskView.Refresh();
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
        AddTaskEvent(task, "Created", null, task.Message);
        _taskView.Refresh();
        UpdateTaskSummary();
    }

    private void AddTaskEvent(
        UpdateTaskRecord task,
        string stage,
        int? percentage,
        string message,
        string? detail = null,
        DateTime? occurredAt = null)
    {
        _ = _taskHistoryStore.AddEventAsync(new TaskEventRecord(
            0,
            task.RequestId,
            stage,
            percentage,
            message,
            detail,
            occurredAt ?? DateTime.Now));
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
