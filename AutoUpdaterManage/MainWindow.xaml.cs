using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Data;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using DevExpress.Xpf.Core;
using AutoUpdaterManage.Models;
using AutoUpdaterManage.Services;
using AutoUpdater.Protocol;
using MessageBox = System.Windows.MessageBox;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;

namespace AutoUpdaterManage;

public partial class MainWindow : ThemedWindow
{
    private readonly UdpControllerService _controllerService = new();
    private readonly TaskHistoryStore _taskHistoryStore = new();
    private IDatabaseProvider _databaseProvider =
        new MySqlDatabaseProvider(["data_result", "plc_user_manage"]);
    private readonly ICollectionView _deviceView;
    private readonly ICollectionView _taskView;
    private System.Net.IPAddress? _broadcastAddress;
    private DatabaseTableInfo? _currentDatabaseTable;
    private int _databasePageNumber = 1;
    private const int DatabasePageSize = 100;
    private DatabasePage? _databasePage;
    private readonly Dictionary<Guid, DatabaseSyncBatch> _databaseSyncBatches = [];

    public ObservableCollection<DeviceInfo> Devices { get; } = [];
    public ObservableCollection<UpdateTaskRecord> Tasks { get; } = [];
    public ObservableCollection<UpdateTaskRecord> DatabaseSyncTasks { get; } = [];
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
        _controllerService.DatabaseSyncStatusReceived += ApplyDatabaseSyncStatus;
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
        DatabaseSyncTasks.Clear();
        foreach (var record in records)
        {
            Tasks.Add(record);
            if (record.Operation == UpdateTaskOperation.DatabaseSync)
                DatabaseSyncTasks.Add(record);
        }
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

    private async void ConnectDatabase_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var selectedProvider =
                (DatabaseProviderBox.SelectedItem as ComboBoxItem)?.Content?.ToString();
            await _databaseProvider.DisposeAsync();
            string connectionValue;
            if (selectedProvider == "SQLite")
            {
                var dialog = new OpenFileDialog
                {
                    Title = "选择SQLite数据库",
                    Filter = "SQLite数据库 (*.db;*.sqlite;*.sqlite3)|*.db;*.sqlite;*.sqlite3|所有文件 (*.*)|*.*",
                    CheckFileExists = true
                };
                if (dialog.ShowDialog(this) != true) return;
                _databaseProvider = new SqliteDatabaseProvider();
                connectionValue = dialog.FileName;
                DatabaseStatusText.Text = "正在连接SQLite数据库…";
            }
            else
            {
                if (!uint.TryParse(DatabasePortBox.Text.Trim(), out var port) ||
                    port is 0 or > 65535)
                    throw new FormatException("MySQL端口必须在1到65535之间。");
                _databaseProvider = new MySqlDatabaseProvider(
                    ["data_result", "plc_user_manage"]);
                connectionValue = new MySqlConnector.MySqlConnectionStringBuilder
                {
                    Server = DatabaseHostBox.Text.Trim(),
                    Port = port,
                    UserID = DatabaseUserBox.Text.Trim(),
                    Password = DatabasePasswordBox.Password,
                    Database = DatabaseNameBox.Text.Trim(),
                    // 当前用于可信内网中的 MySQL。跨公网部署时应改为
                    // VerifyCA/VerifyFull，并配置服务器证书。
                    SslMode = MySqlConnector.MySqlSslMode.None
                }.ConnectionString;
                DatabaseStatusText.Text = "正在连接MySQL数据库…";
            }
            await _databaseProvider.ConnectAsync(connectionValue);
            var tables = await _databaseProvider.GetTablesAsync();
            DatabaseTablesList.ItemsSource = tables;
            DatabaseConnectionText.Text =
                $"{_databaseProvider.ProviderName} · {tables.Count} 张表";
            DatabaseStatusText.Text =
                $"连接成功：{_databaseProvider.ProviderName} / " +
                $"{DatabaseNameBox.Text.Trim()}";
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

    private async void EditDatabaseDraft_Click(
        object sender, RoutedEventArgs e)
    {
        if (_currentDatabaseTable is null ||
            DatabaseDataGrid.SelectedItem is not DataRowView row)
        {
            MessageBox.Show(
                "请先选择需要编辑的数据行。",
                "尚未选择数据",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }
        try
        {
            var columns = await _databaseProvider.GetColumnsAsync(
                _currentDatabaseTable.Name);
            if (!columns.Any(column => column.IsPrimaryKey))
                throw new InvalidOperationException("该表没有主键，不能安全编辑。");
            var values = RowToDictionary(row);
            var window = new DatabaseRowDraftWindow(
                _currentDatabaseTable.Name,
                columns,
                "UPDATE",
                values)
            {
                Owner = this
            };
            if (window.ShowDialog() == true && window.Draft is not null)
            {
                DatabaseDrafts.Add(window.Draft);
                DatabaseStatusText.Text =
                    $"已添加编辑草稿，待同步变更共 {DatabaseDrafts.Count} 条";
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                ex.Message,
                "无法创建编辑草稿",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private async void DeleteDatabaseDraft_Click(
        object sender, RoutedEventArgs e)
    {
        if (_currentDatabaseTable is null ||
            DatabaseDataGrid.SelectedItem is not DataRowView row)
        {
            MessageBox.Show(
                "请先选择需要删除的数据行。",
                "尚未选择数据",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }
        try
        {
            var columns = await _databaseProvider.GetColumnsAsync(
                _currentDatabaseTable.Name);
            var primaryKeys = columns
                .Where(column => column.IsPrimaryKey)
                .ToArray();
            if (primaryKeys.Length == 0)
                throw new InvalidOperationException("该表没有主键，不能安全删除。");
            var values = RowToDictionary(row);
            var keyValues = primaryKeys.ToDictionary(
                column => column.Name,
                column => values.GetValueOrDefault(column.Name));
            if (MessageBox.Show(
                    $"确认把 {_currentDatabaseTable.Name} 的选中行加入删除草稿？\n" +
                    string.Join(", ", keyValues.Select(
                        pair => $"{pair.Key}={pair.Value}")),
                    "确认删除草稿",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;
            DatabaseDrafts.Add(new DatabaseChangeDraft
            {
                Id = Guid.NewGuid(),
                TableName = _currentDatabaseTable.Name,
                Operation = "DELETE",
                Values = values,
                KeyValues = keyValues,
                CreatedAt = DateTime.Now
            });
            DatabaseStatusText.Text =
                $"已添加删除草稿，待同步变更共 {DatabaseDrafts.Count} 条";
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                ex.Message,
                "无法创建删除草稿",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private static IReadOnlyDictionary<string, object?> RowToDictionary(
        DataRowView row)
    {
        var table = row.DataView.Table ??
            throw new InvalidOperationException("无法读取当前数据行。");
        return table.Columns.Cast<DataColumn>().ToDictionary(
            column => column.ColumnName,
            column => row[column.ColumnName] is DBNull
                ? null
                : row[column.ColumnName]);
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

    private async void ApplyDatabaseDrafts_Click(
        object sender, RoutedEventArgs e)
    {
        if (!_databaseProvider.IsConnected)
        {
            MessageBox.Show(
                "请先连接数据库。",
                "数据库尚未连接",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }
        if (DatabaseDrafts.Count == 0)
        {
            MessageBox.Show(
                "当前没有待应用的数据库变更。",
                "没有变更",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var changes = DatabaseDrafts.ToArray();
        var summary = string.Join(
            "\n",
            changes.GroupBy(change => change.Operation)
                .Select(group => $"{group.Key}：{group.Count()} 条"));
        if (MessageBox.Show(
                $"即将在本机数据库执行 {changes.Length} 条变更：\n\n" +
                $"{summary}\n\n所有变更将在同一个事务中执行，是否继续？",
                "确认应用数据库变更",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        ApplyDatabaseDraftsButton.IsEnabled = false;
        ApplyDatabaseDraftsButton.Content = "正在应用…";
        DatabaseStatusText.Text = "正在事务中应用数据库变更…";
        try
        {
            var affectedRows =
                await _databaseProvider.ApplyChangesAsync(changes);
            DatabaseDrafts.Clear();
            await LoadDatabasePageAsync();
            DatabaseStatusText.Text =
                $"应用成功：{changes.Length} 条变更，影响 {affectedRows} 行";
            MessageBox.Show(
                $"数据库变更已全部提交。\n影响行数：{affectedRows}",
                "应用成功",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            DatabaseStatusText.Text =
                $"应用失败，事务已回滚：{ex.Message}";
            MessageBox.Show(
                $"所有变更均未提交，事务已经回滚。\n\n{ex.Message}",
                "应用数据库变更失败",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            ApplyDatabaseDraftsButton.Content = "应用到本机";
            ApplyDatabaseDraftsButton.IsEnabled = true;
        }
    }

    private async void SyncDatabaseDrafts_Click(
        object sender, RoutedEventArgs e)
    {
        var selectedDevices = Devices
            .Where(device => device.IsSelected &&
                             device.Status == DeviceStatus.Online)
            .ToArray();
        if (selectedDevices.Length == 0)
        {
            MessageBox.Show(
                "请先在设备管理页面选择至少一台在线设备。",
                "未选择设备",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }
        if (DatabaseDrafts.Count == 0)
        {
            MessageBox.Show(
                "当前没有待同步的数据库变更。",
                "没有变更",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var changes = DatabaseDrafts.Select(change =>
            new DatabaseChangePayload(
                change.Id,
                change.TableName,
                change.Operation,
                change.Values.ToDictionary(
                    pair => pair.Key,
                    pair => JsonSerializer.SerializeToElement(pair.Value)),
                change.KeyValues.ToDictionary(
                    pair => pair.Key,
                    pair => JsonSerializer.SerializeToElement(pair.Value))))
            .ToArray();
        var databaseName = string.IsNullOrWhiteSpace(DatabaseNameBox.Text)
            ? "leadchina_project"
            : DatabaseNameBox.Text.Trim();
        if (MessageBox.Show(
                $"将向 {selectedDevices.Length} 台设备下发 " +
                $"{changes.Length} 条数据库变更。\n\n" +
                "管理端将生成带SHA-256校验的同步包，客户端读取并在事务中写入MySQL；" +
                "任意一条失败时整批回滚。是否继续？",
                "确认同步数据库变更",
                MessageBoxButton.YesNo,
                MessageBoxImage.Information) != MessageBoxResult.Yes)
            return;

        SyncDatabaseDraftsButton.IsEnabled = false;
        SyncDatabaseDraftsButton.Content = "正在生成同步包…";
        DatabaseSyncPackageInfo package;
        try
        {
            package = await DatabaseSyncPackageBuilder.CreateAsync(
                DatabaseSyncDirectoryBox.Text.Trim(),
                databaseName,
                changes);
        }
        catch (Exception ex)
        {
            SyncDatabaseDraftsButton.Content = "同步到设备";
            SyncDatabaseDraftsButton.IsEnabled = true;
            MessageBox.Show(
                $"{ex.GetType().Name}：{ex.Message}",
                "生成数据库同步包失败",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            return;
        }
        SyncDatabaseDraftsButton.Content = "正在同步…";
        var batch = new DatabaseSyncBatch(
            changes.Select(change => change.ChangeId).ToHashSet(),
            selectedDevices.Select(device => device.DeviceId)
                .ToHashSet(StringComparer.OrdinalIgnoreCase));
        try
        {
            foreach (var device in selectedDevices)
            {
                var requestId = Guid.NewGuid();
                _databaseSyncBatches[requestId] = batch;
                var task = new UpdateTaskRecord(
                    requestId,
                    device.DeviceId,
                    device.Name,
                    device.IpAddress,
                    UpdateTaskOperation.DatabaseSync,
                    databaseName,
                    null,
                    $"{changes.Length} 条变更",
                    UpdateTaskState.Dispatching,
                    "准备下发数据库同步任务",
                    DateTime.Now,
                    DateTime.Now);
                Tasks.Insert(0, task);
                DatabaseSyncTasks.Insert(0, task);
                _ = _taskHistoryStore.SaveAsync(task);
                try
                {
                    device.UpdateResult = "正在下发数据库变更…";
                    var acknowledgement =
                        await _controllerService.SendDatabaseSyncFileReliableAsync(
                            device.IpAddress,
                            device.UdpPort,
                            device.DeviceId,
                            package,
                            requestId,
                            attempt =>
                            {
                                Dispatcher.Invoke(() =>
                                {
                                    task.RecordAttempt(attempt);
                                    _ = _taskHistoryStore.SaveAsync(task);
                                });
                            });
                    if (!acknowledgement.Received)
                    {
                        CompleteDatabaseSyncTarget(
                            requestId,
                            device.DeviceId,
                            false,
                            acknowledgement.Message);
                    }
                    else if (device.UpdateResult == "正在下发数据库变更…")
                    {
                        // 最终结果可能比可靠发送方法更早返回。
                        device.UpdateResult =
                            "设备已收到数据库同步任务，等待执行结果";
                        if (task.State == UpdateTaskState.Dispatching)
                        {
                            task.ApplyStatus(
                                UpdateTaskState.Sent,
                                "设备已收到任务，等待执行结果");
                            _ = _taskHistoryStore.SaveAsync(task);
                        }
                    }
                }
                catch (Exception ex)
                {
                    CompleteDatabaseSyncTarget(
                        requestId,
                        device.DeviceId,
                        false,
                        ex.Message);
                }
            }
            if (batch.PendingDeviceIds.Count > 0)
            {
                DatabaseStatusText.Text =
                    $"已下发同步包 {Path.GetFileName(package.Path)}，" +
                    $"{package.Size / 1024d:F1} KB";
            }
        }
        catch (Exception ex)
        {
            DatabaseStatusText.Text = $"数据库同步失败：{ex.Message}";
            MessageBox.Show(
                ex.Message,
                "数据库同步失败",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            SyncDatabaseDraftsButton.Content = "同步到设备";
            SyncDatabaseDraftsButton.IsEnabled = true;
        }
    }

    private void ApplyDatabaseSyncStatus(DatabaseSyncStatusInfo status)
    {
        Dispatcher.Invoke(() =>
        {
            var device = Devices.FirstOrDefault(
                item => string.Equals(
                    item.DeviceId,
                    status.DeviceId,
                    StringComparison.OrdinalIgnoreCase));
            if (device is not null)
                device.UpdateResult = status.Success
                    ? $"数据库同步成功：{status.AcceptedChanges} 条"
                    : status.Message;
            CompleteDatabaseSyncTarget(
                status.RequestId,
                status.DeviceId,
                status.Success,
                status.Message);
        });
    }

    private void CompleteDatabaseSyncTarget(
        Guid requestId,
        string deviceId,
        bool success,
        string message)
    {
        if (!_databaseSyncBatches.TryGetValue(requestId, out var batch))
            return;
        var task = Tasks.FirstOrDefault(item => item.RequestId == requestId);
        if (task is not null)
        {
            task.ApplyStatus(
                success ? UpdateTaskState.Succeeded : UpdateTaskState.Failed,
                message);
            _ = _taskHistoryStore.SaveAsync(task);
            _ = _taskHistoryStore.AddEventAsync(new TaskEventRecord(
                0,
                requestId,
                success ? "DatabaseSyncSucceeded" : "DatabaseSyncFailed",
                success ? 100 : null,
                message,
                null,
                DateTime.Now));
        }
        batch.PendingDeviceIds.Remove(deviceId);
        if (!success)
        {
            batch.Failed = true;
            batch.Errors.Add($"{deviceId}：{message}");
        }
        if (batch.PendingDeviceIds.Count > 0)
            return;

        foreach (var key in _databaseSyncBatches
                     .Where(pair => ReferenceEquals(pair.Value, batch))
                     .Select(pair => pair.Key)
                     .ToArray())
            _databaseSyncBatches.Remove(key);
        if (batch.Failed)
        {
            DatabaseStatusText.Text =
                $"部分设备同步失败，草稿已保留：{string.Join("；", batch.Errors)}";
            return;
        }

        foreach (var draft in DatabaseDrafts
                     .Where(item => batch.DraftIds.Contains(item.Id))
                     .ToArray())
            DatabaseDrafts.Remove(draft);
        DatabaseStatusText.Text =
            "所有目标设备同步成功，已从待同步列表移除本批变更";
        _ = RefreshDatabaseAfterSyncAsync();
    }

    private async Task RefreshDatabaseAfterSyncAsync()
    {
        await LoadDatabasePageAsync();
        if (_currentDatabaseTable is not null)
            DatabaseStatusText.Text =
                $"同步成功，已刷新 {_currentDatabaseTable.Name} 数据";
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

    private sealed class DatabaseSyncBatch(
        HashSet<Guid> draftIds,
        HashSet<string> pendingDeviceIds)
    {
        public HashSet<Guid> DraftIds { get; } = draftIds;
        public HashSet<string> PendingDeviceIds { get; } = pendingDeviceIds;
        public bool Failed { get; set; }
        public List<string> Errors { get; } = [];
    }
}
