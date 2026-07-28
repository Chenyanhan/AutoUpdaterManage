using System.Text;
using System.IO;
using System.Windows;
using AutoUpdaterManage.Models;
using AutoUpdaterManage.Services;
using DevExpress.Xpf.Core;
using Clipboard = System.Windows.Clipboard;
using MessageBox = System.Windows.MessageBox;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;

namespace AutoUpdaterManage;

public partial class TaskDetailWindow : ThemedWindow
{
    private readonly UpdateTaskRecord _task;
    private readonly TaskHistoryStore _store;
    private IReadOnlyList<TaskEventRecord> _events = [];

    public TaskDetailWindow(
        UpdateTaskRecord task,
        TaskHistoryStore store)
    {
        _task = task;
        _store = store;
        InitializeComponent();
        RequestIdText.Text = $"RequestId：{task.RequestId:D}";
        StateText.Text = task.StateText;
        DeviceText.Text = $"{task.DeviceName}（{task.DeviceId}）";
        IpText.Text = task.IpAddress;
        OperationText.Text = task.OperationText;
        VersionText.Text =
            $"{task.SourceVersion ?? "未知"} → {task.ResultVersion ?? task.TargetVersion ?? "待确定"}";
        AttemptText.Text = $"发送 {task.AttemptCount} 次";
        UpdatedAtText.Text = $"更新于 {task.UpdatedAtText}";
        SourceText.Text = string.IsNullOrWhiteSpace(task.Source)
            ? "更新来源：—"
            : $"更新来源：{task.Source}";
        Loaded += async (_, _) => await LoadEventsAsync();
    }

    private async Task LoadEventsAsync()
    {
        _events = await _store.LoadEventsAsync(_task.RequestId);
        EventsGrid.ItemsSource = _events;
    }

    private string BuildText()
    {
        var builder = new StringBuilder();
        builder.AppendLine($"任务编号：{_task.RequestId:D}");
        builder.AppendLine($"设备：{_task.DeviceName}（{_task.DeviceId}）");
        builder.AppendLine($"IP：{_task.IpAddress}");
        builder.AppendLine($"操作：{_task.OperationText}");
        builder.AppendLine($"状态：{_task.StateText}");
        builder.AppendLine($"原版本：{_task.SourceVersion ?? "未知"}");
        builder.AppendLine($"结果版本：{_task.ResultVersion ?? "未知"}");
        builder.AppendLine($"发送次数：{_task.AttemptCount}");
        builder.AppendLine($"更新来源：{_task.Source ?? "—"}");
        builder.AppendLine($"结果：{_task.Message}");
        builder.AppendLine();
        builder.AppendLine("执行时间线：");
        foreach (var item in _events)
            builder.AppendLine(
                $"{item.TimeText}  [{item.Stage}] {item.PercentageText}  {item.DetailText}");
        return builder.ToString();
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        Clipboard.SetText(BuildText());
        MessageBox.Show(
            "任务详情已复制到剪贴板。",
            "复制成功",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "导出任务详情",
            Filter = "文本文件 (*.txt)|*.txt",
            FileName = $"update-task-{_task.ShortId}.txt"
        };
        if (dialog.ShowDialog(this) != true) return;
        File.WriteAllText(dialog.FileName, BuildText(), Encoding.UTF8);
    }
}
