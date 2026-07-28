using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using AutoUpdaterManage.Models;
using AutoUpdaterManage.Services;
using DevExpress.Xpf.Core;
using Microsoft.Win32;
using MessageBox = System.Windows.MessageBox;

namespace AutoUpdaterManage;

public partial class PackageBuilderWindow : ThemedWindow
{
    private readonly UpdatePackageBuilder _builder = new();
    public ObservableCollection<PackageFileItem> Files { get; } = [];
    public string? GeneratedManifestPath { get; private set; }

    public PackageBuilderWindow()
    {
        InitializeComponent();
        DataContext = this;
    }

    private void ChooseSource_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "选择待打包程序的根目录" };
        if (dialog.ShowDialog(this) != true) return;
        SourceDirectoryBox.Text = dialog.FolderName;
        LoadFiles(dialog.FolderName);
        if (string.IsNullOrWhiteSpace(OutputDirectoryBox.Text))
            OutputDirectoryBox.Text = Path.Combine(
                Directory.GetParent(dialog.FolderName)?.FullName ?? dialog.FolderName,
                "UpdateOutput");
    }

    private void ChooseOutput_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "选择更新包输出目录或网络共享目录" };
        if (dialog.ShowDialog(this) == true)
            OutputDirectoryBox.Text = dialog.FolderName;
    }

    private void LoadFiles(string sourceDirectory)
    {
        Files.Clear();
        foreach (var path in Directory.EnumerateFiles(
                     sourceDirectory, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourceDirectory, path);
            var firstSegment = relative.Split(
                [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                2, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (firstSegment is ".autoupdater" or "AutoUpdater" ||
                string.Equals(firstSegment, ".autoupdater", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(firstSegment, "AutoUpdater", StringComparison.OrdinalIgnoreCase))
                continue;
            var info = new FileInfo(path);
            var item = new PackageFileItem
            {
                FullPath = path,
                RelativePath = relative,
                Length = info.Length
            };
            item.PropertyChanged += (_, _) => UpdateSummary();
            Files.Add(item);
        }
        UpdateSummary();
    }

    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var file in Files) file.IsSelected = true;
    }

    private void HeaderSelectAll_Checked(object sender, RoutedEventArgs e)
    {
        foreach (var file in Files) file.IsSelected = true;
    }

    private void HeaderSelectAll_Unchecked(object sender, RoutedEventArgs e)
    {
        foreach (var file in Files) file.IsSelected = false;
    }

    private void InvertSelection_Click(object sender, RoutedEventArgs e)
    {
        foreach (var file in Files) file.IsSelected = !file.IsSelected;
    }

    private async void BuildPackage_Click(object sender, RoutedEventArgs e)
    {
        BuildButton.IsEnabled = false;
        BuildButton.Content = "正在生成…";
        try
        {
            var result = await _builder.BuildAsync(new PackageBuildRequest(
                SourceDirectoryBox.Text,
                OutputDirectoryBox.Text,
                VersionBox.Text.Trim(),
                Files));
            MessageBox.Show(
                $"更新包生成成功。\n\n文件数：{result.FileCount}\nZIP：{result.PackagePath}\n" +
                $"清单：{result.ManifestPath}\nSHA-256：{result.Sha256}",
                "生成成功", MessageBoxButton.OK, MessageBoxImage.Information);
            GeneratedManifestPath = result.ManifestPath;
            DialogResult = true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"无法生成更新包。\n\n错误类型：{ex.GetType().Name}\n原因：{ex.Message}",
                "生成失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            BuildButton.IsEnabled = true;
            BuildButton.Content = "生成发布包";
        }
    }

    private void UpdateSummary() =>
        SelectionSummary.Text = $"已选择 {Files.Count(file => file.IsSelected)} / {Files.Count} 个文件";

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
