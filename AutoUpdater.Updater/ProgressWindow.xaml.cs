using System.Windows;

namespace AutoUpdater.Updater;

public partial class ProgressWindow : Window
{
    private readonly UpdaterOptions _options;
    private bool _canClose;

    internal ProgressWindow(UpdaterOptions options)
    {
        _options = options;
        InitializeComponent();
        OperationText.Text = options.Operation == UpdateOperation.Rollback
            ? "版本回退"
            : "软件更新";
        LogPathText.Text = $"日志：{options.LogPath}";
        Loaded += ProgressWindow_Loaded;
        Closing += (_, eventArgs) =>
        {
            if (!_canClose) eventArgs.Cancel = true;
        };
    }

    private async void ProgressWindow_Loaded(object sender, RoutedEventArgs e)
    {
        var progress = new Progress<UpdateProgress>(ApplyProgress);
        try
        {
            await using var logger = new FileUpdateLogger(_options.LogPath);
            var resultReporter = new UdpResultReporter(_options);
            var engine = new UpdateEngine(logger, resultReporter, progress);
            await engine.ExecuteAsync(_options);
            ApplyProgress(new UpdateProgress(
                UpdateStage.Completed, 100, "操作已完成", "上位机正在重新启动"));
            await Task.Delay(900);
            _canClose = true;
            Close();
        }
        catch (Exception ex)
        {
            Environment.ExitCode = 1;
            try
            {
                await new UdpResultReporter(_options).ReportAsync(false, ex.Message);
            }
            catch
            {
            }
            ApplyProgress(new UpdateProgress(
                UpdateStage.Failed, ProgressBar.Value is > 0
                    ? (int)ProgressBar.Value
                    : 0,
                "操作失败",
                ex.Message));
            ActivityText.Text += $"\n错误：{ex}";
            CloseButton.Visibility = Visibility.Visible;
            _canClose = true;
        }
    }

    private void ApplyProgress(UpdateProgress progress)
    {
        ProgressBar.Value = Math.Clamp(progress.Percentage, 0, 100);
        PercentageText.Text = $"{ProgressBar.Value:0}%";
        StatusText.Text = progress.Message;
        DetailText.Text = progress.Detail ?? StageDescription(progress.Stage);
        ActivityText.Text +=
            $"{DateTime.Now:HH:mm:ss}  {progress.Message}" +
            $"{(string.IsNullOrWhiteSpace(progress.Detail) ? "" : $" — {progress.Detail}")}\n";
    }

    private static string StageDescription(UpdateStage stage) => stage switch
    {
        UpdateStage.Preparing => "正在准备工作目录",
        UpdateStage.WaitingForHost => "正在等待上位机安全退出",
        UpdateStage.ReadingManifest => "正在读取更新清单",
        UpdateStage.AcquiringPackage => "正在获取更新文件",
        UpdateStage.Verifying => "正在校验更新包完整性",
        UpdateStage.Extracting => "正在解压更新文件",
        UpdateStage.BackingUp => "正在备份当前版本",
        UpdateStage.Installing => "正在替换程序文件",
        UpdateStage.Restarting => "正在启动更新后的上位机",
        UpdateStage.Completed => "操作完成",
        UpdateStage.Failed => "请查看错误信息和更新日志",
        _ => ""
    };

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}
