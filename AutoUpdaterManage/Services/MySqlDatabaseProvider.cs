using System.Data;
using AutoUpdaterManage.Models;
using MySqlConnector;

namespace AutoUpdaterManage.Services;

public sealed class MySqlDatabaseProvider(
    IReadOnlyCollection<string>? allowedTables = null) : IDatabaseProvider
{
    private readonly HashSet<string> _allowedTables = new(
        allowedTables ?? [],
        StringComparer.OrdinalIgnoreCase);
    private MySqlConnection? _connection;
    private string? _databaseName;
    private HashSet<string> _knownTables = new(StringComparer.OrdinalIgnoreCase);

    public string ProviderName => "MySQL";
    public bool IsConnected => _connection?.State == ConnectionState.Open;

    public async Task ConnectAsync(
        string connectionValue,
        CancellationToken cancellationToken = default)
    {
        var builder = new MySqlConnectionStringBuilder(connectionValue)
        {
            CharacterSet = "utf8mb4",
            ConnectionTimeout = 10,
            DefaultCommandTimeout = 30,
            Pooling = true
        };
        if (string.IsNullOrWhiteSpace(builder.Database))
            throw new ArgumentException("请输入数据库名。", nameof(connectionValue));

        if (_connection is not null)
            await _connection.DisposeAsync();
        _connection = new MySqlConnection(builder.ConnectionString);
        await _connection.OpenAsync(cancellationToken);
        _databaseName = builder.Database;
        _knownTables = (await GetTablesCoreAsync(cancellationToken))
            .Select(table => table.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public async Task<IReadOnlyList<DatabaseTableInfo>> GetTablesAsync(
        CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        var result = await GetTablesCoreAsync(cancellationToken);
        _knownTables = result.Select(table => table.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return result;
    }

    private async Task<IReadOnlyList<DatabaseTableInfo>> GetTablesCoreAsync(
        CancellationToken cancellationToken)
    {
        var result = new List<DatabaseTableInfo>();
        await using var command = _connection!.CreateCommand();
        command.CommandText =
            """
            SELECT TABLE_NAME
            FROM information_schema.TABLES
            WHERE TABLE_SCHEMA = @schema
              AND TABLE_TYPE = 'BASE TABLE'
            ORDER BY TABLE_NAME;
            """;
        command.Parameters.AddWithValue("@schema", _databaseName);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var name = reader.GetString(0);
            if (_allowedTables.Count == 0 || _allowedTables.Contains(name))
                result.Add(new DatabaseTableInfo(name));
        }
        return result;
    }

    public async Task<IReadOnlyList<DatabaseColumnInfo>> GetColumnsAsync(
        string tableName,
        CancellationToken cancellationToken = default)
    {
        EnsureKnownTable(tableName);
        var result = new List<DatabaseColumnInfo>();
        await using var command = _connection!.CreateCommand();
        command.CommandText =
            """
            SELECT COLUMN_NAME, COLUMN_TYPE, IS_NULLABLE,
                   COLUMN_KEY, COLUMN_DEFAULT, EXTRA
            FROM information_schema.COLUMNS
            WHERE TABLE_SCHEMA = @schema AND TABLE_NAME = @table
            ORDER BY ORDINAL_POSITION;
            """;
        command.Parameters.AddWithValue("@schema", _databaseName);
        command.Parameters.AddWithValue("@table", tableName);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new DatabaseColumnInfo(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2).Equals("YES", StringComparison.OrdinalIgnoreCase),
                reader.GetString(3).Equals("PRI", StringComparison.OrdinalIgnoreCase),
                reader.IsDBNull(4) ? null : reader.GetValue(4),
                reader.GetString(5).Contains(
                    "auto_increment", StringComparison.OrdinalIgnoreCase)));
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

        await using var count = _connection!.CreateCommand();
        count.CommandText = $"SELECT COUNT(*) FROM {quotedTable};";
        var totalRows = Convert.ToInt64(
            await count.ExecuteScalarAsync(cancellationToken));

        var columns = await GetColumnsAsync(tableName, cancellationToken);
        var primaryKey = columns.FirstOrDefault(column => column.IsPrimaryKey);
        await using var command = _connection.CreateCommand();
        command.CommandText =
            $"SELECT * FROM {quotedTable}" +
            (primaryKey is null
                ? ""
                : $" ORDER BY {QuoteIdentifier(primaryKey.Name)}") +
            " LIMIT @limit OFFSET @offset;";
        command.Parameters.AddWithValue("@limit", pageSize);
        command.Parameters.AddWithValue("@offset", (pageNumber - 1) * pageSize);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var table = new DataTable(tableName);
        table.Load(reader);
        return new DatabasePage(table, pageNumber, pageSize, totalRows);
    }

    public async Task<int> ApplyChangesAsync(
        IReadOnlyList<DatabaseChangeDraft> changes,
        CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        if (changes.Count == 0) return 0;
        foreach (var change in changes)
            EnsureKnownTable(change.TableName);

        await using var transaction =
            await _connection!.BeginTransactionAsync(cancellationToken);
        try
        {
            var affectedRows = 0;
            foreach (var change in changes)
            {
                await using var command = _connection.CreateCommand();
                command.Transaction = transaction;
                BuildChangeCommand(command, change);
                var affected = await command.ExecuteNonQueryAsync(cancellationToken);
                if (change.Operation is "UPDATE" or "DELETE" && affected != 1)
                    throw new InvalidOperationException(
                        $"{change.Operation} {change.TableName} 应影响1行，实际影响{affected}行。");
                affectedRows += affected;
            }
            await transaction.CommitAsync(cancellationToken);
            return affectedRows;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    private static void BuildChangeCommand(
        MySqlCommand command,
        DatabaseChangeDraft change)
    {
        var table = QuoteIdentifier(change.TableName);
        switch (change.Operation)
        {
            case "INSERT":
                if (change.Values.Count == 0)
                    throw new InvalidOperationException("新增草稿没有字段。");
                var insertNames = change.Values.Keys.ToArray();
                command.CommandText =
                    $"INSERT INTO {table} (" +
                    string.Join(", ", insertNames.Select(QuoteIdentifier)) +
                    ") VALUES (" +
                    string.Join(", ", insertNames.Select((_, index) => $"@v{index}")) +
                    ");";
                AddParameters(command, change.Values, "v");
                break;
            case "UPDATE":
                EnsureKeys(change);
                if (change.Values.Count == 0)
                    throw new InvalidOperationException("编辑草稿没有字段。");
                command.CommandText =
                    $"UPDATE {table} SET " +
                    string.Join(", ", change.Values.Keys.Select(
                        (name, index) => $"{QuoteIdentifier(name)}=@v{index}")) +
                    " WHERE " +
                    string.Join(" AND ", change.KeyValues.Keys.Select(
                        (name, index) => $"{QuoteIdentifier(name)} <=> @k{index}")) +
                    ";";
                AddParameters(command, change.Values, "v");
                AddParameters(command, change.KeyValues, "k");
                break;
            case "DELETE":
                EnsureKeys(change);
                command.CommandText =
                    $"DELETE FROM {table} WHERE " +
                    string.Join(" AND ", change.KeyValues.Keys.Select(
                        (name, index) => $"{QuoteIdentifier(name)} <=> @k{index}")) +
                    ";";
                AddParameters(command, change.KeyValues, "k");
                break;
            default:
                throw new InvalidOperationException($"不支持的数据库操作：{change.Operation}");
        }
    }

    private static void AddParameters(
        MySqlCommand command,
        IReadOnlyDictionary<string, object?> values,
        string prefix)
    {
        var index = 0;
        foreach (var value in values.Values)
            command.Parameters.AddWithValue(
                $"@{prefix}{index++}", value ?? DBNull.Value);
    }

    private static void EnsureKeys(DatabaseChangeDraft change)
    {
        if (change.KeyValues.Count == 0)
            throw new InvalidOperationException(
                $"{change.Operation} {change.TableName} 缺少主键条件。");
    }

    private void EnsureConnected()
    {
        if (!IsConnected)
            throw new InvalidOperationException("请先连接MySQL数据库。");
    }

    private void EnsureKnownTable(string tableName)
    {
        EnsureConnected();
        if (!_knownTables.Contains(tableName))
            throw new InvalidOperationException($"数据表不在允许范围内：{tableName}");
    }

    private static string QuoteIdentifier(string value) =>
        $"`{value.Replace("`", "``", StringComparison.Ordinal)}`";

    public async ValueTask DisposeAsync()
    {
        if (_connection is not null)
            await _connection.DisposeAsync();
    }
}
