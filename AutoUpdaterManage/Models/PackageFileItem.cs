using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace AutoUpdaterManage.Models;

public sealed class PackageFileItem : INotifyPropertyChanged
{
    private bool _isSelected = true;

    public required string FullPath { get; init; }
    public required string RelativePath { get; init; }
    public long Length { get; init; }
    public string SizeText => Length switch
    {
        >= 1024 * 1024 => $"{Length / 1024d / 1024d:F2} MB",
        >= 1024 => $"{Length / 1024d:F1} KB",
        _ => $"{Length} B"
    };

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
