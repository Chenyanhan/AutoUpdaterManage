using System.Data;
using System.IO;
using AutoUpdaterManage.Models;
using Microsoft.Data.Sqlite;

namespace AutoUpdaterManage.Services;

public sealed class SqliteDatabaseProvider : IDatabaseProvider
{
    private SqliteConnection? _connection;
    private HashSet<string> _knownTables = new(StringComparer.OrdinalIgnoreCase);

    public string ProviderName => "SQLite";
    public bool IsConnected => _connection?.State == ConnectionState.Open;

    public async Task ConnectAsync(
        string connectionValue,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(connectionValue))
            throw new ArgumentException("请选择SQLite数据库文件。", nameof(connectionValue));
        var databasePath = Path.GetFullPath(connectionValue);
        if (!File.Exists(databasePath))
            throw new FileNotFoundException("找不到SQLite数据库文件。", databasePath);

        if (_connection is not null)
            await _connection.DisposeAsync();
        _connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWrite,
            Cache = SqliteCacheMode.Shared
        }.ToString());
        await _connection.OpenAsync(cancellationToken);
        _knownTables = (await GetTablesCoreAsync(cancellationToken))
            .Select(table => table.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public async Task<IReadOnlyList<DatabaseTableInfo>> GetTablesAsync(
        CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        var tables = await GetTablesCoreAsync(cancellationToken);
        _knownTables = tables.Select(table => table.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return tables;
    }

    private async Task<IReadOnlyList<DatabaseTableInfo>> GetTablesCoreAsync(
        CancellationToken cancellationToken)
    {
        var result = new List<DatabaseTableInfo>();
        await using var command = _connection!.CreateCommand();
        command.CommandText =
            """
            SELECT name
            FROM sqlite_master
            WHERE type = 'table'
              AND name NOT LIKE 'sqlite_%'
            ORDER BY name;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            result.Add(new DatabaseTableInfo(reader.GetString(0)));
        return result;
    }

    public async Task<IReadOnlyList<DatabaseColumnInfo>> GetColumnsAsync(
        string tableName,
        CancellationToken cancellationToken = default)
    {
        EnsureKnownTable(tableName);
        var result = new List<DatabaseColumnInfo>();
        await using var command = _connection!.CreateCommand();
        command.CommandText = $"PRAGMA table_info({QuoteIdentifier(tableName)});";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new DatabaseColumnInfo(
                reader.GetString(1),
                reader.IsDBNull(2) ? "TEXT" : reader.GetString(2),
                reader.GetInt32(3) == 0,
                reader.GetInt32(5) > 0,
                reader.IsDBNull(4) ? null : reader.GetValue(4)));
        }
        return result;
    }

    public async Task<DatabasePage> QueryPageAsync(
        string tableName,
        int pageNumber,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        EnsureKnownTable(tableName);
        pageNumber = Math.Max(1, pageNumber);
        pageSize = Math.Clamp(pageSize, 10, 500);
        var quotedTable = QuoteIdentifier(tableName);

        await using var countCommand = _connection!.CreateCommand();
        countCommand.CommandText = $"SELECT COUNT(*) FROM {quotedTable};";
        var totalRows = Convert.ToInt64(
            await countCommand.ExecuteScalarAsync(cancellationToken));

        await using var command = _connection.CreateCommand();
        command.CommandText =
            $"SELECT * FROM {quotedTable} LIMIT $limit OFFSET $offset;";
        command.Parameters.AddWithValue("$limit", pageSize);
        command.Parameters.AddWithValue("$offset", (pageNumber - 1) * pageSize);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var table = new DataTable(tableName);
        table.Load(reader);
        return new DatabasePage(table, pageNumber, pageSize, totalRows);
    }

    private void EnsureConnected()
    {
        if (!IsConnected)
            throw new InvalidOperationException("请先连接SQLite数据库。");
    }

    private void EnsureKnownTable(string tableName)
    {
        EnsureConnected();
        if (!_knownTables.Contains(tableName))
            throw new InvalidOperationException($"数据表不存在或尚未加载：{tableName}");
    }

    private static string QuoteIdentifier(string value) =>
        $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

    public async ValueTask DisposeAsync()
    {
        if (_connection is not null)
            await _connection.DisposeAsync();
    }
}
