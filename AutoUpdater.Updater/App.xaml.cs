using System.Windows;

namespace AutoUpdater.Updater;

public partial class App : System.Windows.Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        try
        {
            var options = UpdaterOptions.Parse(e.Args);
            MainWindow = new ProgressWindow(options);
            MainWindow.Show();
        }
        catch (OptionsException ex)
        {
            MessageBox.Show(
                $"{ex.Message}\n\n{UpdaterOptions.Usage}",
                "更新器参数错误",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Environment.ExitCode = 2;
            Shutdown(2);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                ex.Message,
                "更新器启动失败",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Environment.ExitCode = 1;
            Shutdown(1);
        }
    }
}
