using AutoUpdaterManage.Models;

namespace AutoUpdaterManage.Services;

public interface IDatabaseProvider : IAsyncDisposable
{
    string ProviderName { get; }
    bool IsConnected { get; }
    Task ConnectAsync(string connectionValue, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DatabaseTableInfo>> GetTablesAsync(
        CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DatabaseColumnInfo>> GetColumnsAsync(
        string tableName,
        CancellationToken cancellationToken = default);
    Task<DatabasePage> QueryPageAsync(
        string tableName,
        int pageNumber,
        int pageSize,
        CancellationToken cancellationToken = default);
    Task<int> ApplyChangesAsync(
        IReadOnlyList<DatabaseChangeDraft> changes,
        CancellationToken cancellationToken = default);
}
