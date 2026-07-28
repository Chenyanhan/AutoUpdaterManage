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
    object? DefaultValue);

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
    public required DateTime CreatedAt { get; init; }

    public string Summary =>
        $"{Operation} {TableName} · " +
        string.Join(", ", Values.Take(3).Select(pair => $"{pair.Key}={pair.Value}"));
}
