using System.Data;

namespace AutoUpdaterManage.Models;

public sealed record DatabaseTableInfo(string Name)
{
    public override string ToString() => Name;
}

public sealed record DatabaseColumnInfo(
    string Name,
    string DataType,
    bool IsNullable,
    bool IsPrimaryKey,
    object? DefaultValue,
    bool IsAutoIncrement = false);

public sealed record DatabasePage(
    DataTable Data,
    int PageNumber,
    int PageSize,
    long TotalRows)
{
    public int TotalPages => Math.Max(
        1, (int)Math.Ceiling(TotalRows / (double)PageSize));
}

public sealed class DatabaseChangeDraft
{
    public required Guid Id { get; init; }
    public required string TableName { get; init; }
    public required string Operation { get; init; }
    public required IReadOnlyDictionary<string, object?> Values { get; init; }
    public IReadOnlyDictionary<string, object?> KeyValues { get; init; } =
        new Dictionary<string, object?>();
    public required DateTime CreatedAt { get; init; }

    public string Summary =>
        $"{Operation} {TableName} · " +
        string.Join(", ", Values.Select(pair => $"{pair.Key}={FormatValue(pair.Value)}"));

    private static string FormatValue(object? value) =>
        value is null or DBNull ? "NULL" : Convert.ToString(value) ?? string.Empty;
}
