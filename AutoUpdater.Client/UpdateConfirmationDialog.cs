using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace AutoUpdater.Client;

public static class DesktopUpdatePrompt
{
    public static Task<UpdateDecision> ShowUpdateAsync(
        UpdateCommandContext context, string deviceName) =>
        ShowOnStaThreadAsync(
            "发现软件更新",
            $"{deviceName} 收到管理端下发的软件更新指令。",
            $"更新来源：{context.UpdatePath}\n\n立即更新会关闭当前上位机，安装完成后自动重新启动。",
            "立即更新",
            "稍后更新");

    public static Task<UpdateDecision> ShowRollbackAsync(
        RollbackCommandContext context, string deviceName) =>
        ShowOnStaThreadAsync(
            "确认版本回退",
            $"{deviceName} 收到管理端下发的版本回退指令。",
            $"目标版本：{context.TargetVersion ?? "最近一次备份"}\n\n立即回退会关闭当前上位机，完成后自动重新启动。",
            "立即回退",
            "稍后处理");

    private static Task<UpdateDecision> ShowOnStaThreadAsync(
        string title, string heading, string detail,
        string acceptText, string postponeText)
    {
        var completion = new TaskCompletionSource<UpdateDecision>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                completion.TrySetResult(ShowDialog(
                    title, heading, detail, acceptText, postponeText));
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
        })
        {
            IsBackground = true,
            Name = "AutoUpdater confirmation"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    private static UpdateDecision ShowDialog(
        string title, string heading, string detail,
        string acceptText, string postponeText)
    {
        var decision = UpdateDecision.Postpone;
        var window = new Window
        {
            Title = title,
            Width = 520,
            SizeToContent = SizeToContent.Height,
            MinHeight = 260,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            ShowInTaskbar = true,
            Topmost = true,
            Background = Brushes.White,
            FontFamily = new FontFamily("Microsoft YaHei UI")
        };

        var root = new Grid { Margin = new Thickness(28) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        root.Children.Add(new TextBlock
        {
            Text = heading,
            FontSize = 19,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(23, 32, 51)),
            TextWrapping = TextWrapping.Wrap
        });

        var detailText = new TextBlock
        {
            Text = detail,
            Margin = new Thickness(0, 16, 0, 24),
            FontSize = 13,
            LineHeight = 22,
            Foreground = new SolidColorBrush(Color.FromRgb(89, 103, 128)),
            TextWrapping = TextWrapping.Wrap
        };
        Grid.SetRow(detailText, 1);
        root.Children.Add(detailText);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        var postponeButton = CreateButton(
            postponeText, new SolidColorBrush(Color.FromRgb(232, 237, 245)),
            new SolidColorBrush(Color.FromRgb(39, 54, 79)));
        postponeButton.IsCancel = true;
        postponeButton.Click += (_, _) =>
        {
            decision = UpdateDecision.Postpone;
            window.DialogResult = false;
        };
        var acceptButton = CreateButton(
            acceptText, new SolidColorBrush(Color.FromRgb(37, 99, 235)),
            Brushes.White);
        acceptButton.Margin = new Thickness(12, 0, 0, 0);
        acceptButton.IsDefault = true;
        acceptButton.Click += (_, _) =>
        {
            decision = UpdateDecision.InstallNow;
            window.DialogResult = true;
        };
        buttons.Children.Add(postponeButton);
        buttons.Children.Add(acceptButton);
        Grid.SetRow(buttons, 2);
        root.Children.Add(buttons);

        window.Content = root;
        window.ShowDialog();
        return decision;
    }

    private static Button CreateButton(
        string text, Brush background, Brush foreground) =>
        new()
        {
            Content = text,
            MinWidth = 106,
            Padding = new Thickness(16, 9, 16, 9),
            BorderThickness = new Thickness(0),
            Background = background,
            Foreground = foreground,
            Cursor = System.Windows.Input.Cursors.Hand
        };
}
