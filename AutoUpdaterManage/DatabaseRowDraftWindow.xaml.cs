using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using AutoUpdaterManage.Models;
using DevExpress.Xpf.Core;
using MessageBox = System.Windows.MessageBox;

namespace AutoUpdaterManage;

public partial class DatabaseRowDraftWindow : ThemedWindow
{
    private readonly string _tableName;

    public DatabaseRowDraftWindow(
        string tableName,
        IReadOnlyList<DatabaseColumnInfo> columns)
    {
        _tableName = tableName;
        Fields = new ObservableCollection<DraftField>(
            columns.Select(column => new DraftField(column)));
        InitializeComponent();
        DataContext = this;
        HeadingText.Text = $"向 {tableName} 新增数据";
    }

    public ObservableCollection<DraftField> Fields { get; }
    public DatabaseChangeDraft? Draft { get; private set; }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var values = new Dictionary<string, object?>();
        foreach (var field in Fields)
        {
            if (string.IsNullOrWhiteSpace(field.Value))
            {
                if (!field.Column.IsNullable &&
                    field.Column.DefaultValue is null &&
                    !field.Column.IsPrimaryKey)
                {
                    MessageBox.Show(
                        $"字段 {field.Name} 不能为空。",
                        "数据不完整",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                    return;
                }
                values[field.Name] = null;
                continue;
            }
            values[field.Name] = ConvertValue(field.Value, field.Column.DataType);
        }
        Draft = new DatabaseChangeDraft
        {
            Id = Guid.NewGuid(),
            TableName = _tableName,
            Operation = "INSERT",
            Values = values,
            CreatedAt = DateTime.Now
        };
        DialogResult = true;
    }

    private static object ConvertValue(string value, string dataType)
    {
        var normalized = dataType.ToUpperInvariant();
        if (normalized.Contains("INT") && long.TryParse(value, out var integer))
            return integer;
        if ((normalized.Contains("REAL") ||
             normalized.Contains("DOUBLE") ||
             normalized.Contains("FLOAT") ||
             normalized.Contains("DECIMAL")) &&
            decimal.TryParse(value, out var number))
            return number;
        if (normalized.Contains("BOOL") && bool.TryParse(value, out var boolean))
            return boolean;
        return value;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) =>
        DialogResult = false;
}

public sealed class DraftField : INotifyPropertyChanged
{
    private string? _value;

    public DraftField(DatabaseColumnInfo column)
    {
        Column = column;
        Name = column.Name;
        Description =
            $"{column.DataType}" +
            $"{(column.IsPrimaryKey ? " · 主键" : "")}" +
            $"{(column.IsNullable ? " · 可空" : " · 必填")}";
    }

    public DatabaseColumnInfo Column { get; }
    public string Name { get; }
    public string Description { get; }
    public string? Value
    {
        get => _value;
        set
        {
            if (_value == value) return;
            _value = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Value)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
