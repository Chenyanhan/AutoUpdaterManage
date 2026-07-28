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
    private readonly string _operation;
    private readonly IReadOnlyDictionary<string, object?> _keyValues;

    public DatabaseRowDraftWindow(
        string tableName,
        IReadOnlyList<DatabaseColumnInfo> columns,
        string operation = "INSERT",
        IReadOnlyDictionary<string, object?>? initialValues = null)
    {
        _tableName = tableName;
        _operation = operation;
        _keyValues = columns
            .Where(column => column.IsPrimaryKey)
            .ToDictionary(
                column => column.Name,
                column => initialValues?.GetValueOrDefault(column.Name));
        Fields = new ObservableCollection<DraftField>(
            columns.Select(column => new DraftField(
                column, initialValues?.GetValueOrDefault(column.Name))));
        InitializeComponent();
        DataContext = this;
        HeadingText.Text = operation == "UPDATE"
            ? $"编辑 {tableName} 数据"
            : $"向 {tableName} 新增数据";
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
                if (field.Column.IsAutoIncrement ||
                    field.Column.DefaultValue is not null)
                    continue;
                if (!field.Column.IsNullable &&
                    !field.Column.IsPrimaryKey)
                {
                    MessageBox.Show(
                        $"字段 {field.Name} 不能为空。",
                        "数据不完整",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                    return;
                }
                if (_operation == "UPDATE")
                    values[field.Name] = null;
                continue;
            }
            values[field.Name] = ConvertValue(field.Value, field.Column.DataType);
        }
        Draft = new DatabaseChangeDraft
        {
            Id = Guid.NewGuid(),
            TableName = _tableName,
            Operation = _operation,
            Values = values,
            KeyValues = _keyValues,
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

    public DraftField(DatabaseColumnInfo column, object? initialValue = null)
    {
        Column = column;
        Name = column.Name;
        Description =
            $"{column.DataType}" +
            $"{(column.IsPrimaryKey ? " · 主键" : "")}" +
            $"{(column.IsAutoIncrement ? " · 自增" : "")}" +
            $"{(column.IsNullable ? " · 可空" : " · 必填")}";
        _value = initialValue is null or DBNull
            ? null
            : Convert.ToString(initialValue);
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
