using System.Text.Json;
using AutoUpdater.Protocol;
using MySqlConnector;

namespace AutoUpdater.Client;

internal static class DatabaseSyncExecutor
{
    public static async Task<int> ExecuteAsync(
        string connectionString,
        DatabaseSyncRequestPayload request,
        CancellationToken cancellationToken)
    {
        var builder = new MySqlConnectionStringBuilder(connectionString);
        if (!string.Equals(
                builder.Database,
                request.DatabaseName,
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"任务数据库 {request.DatabaseName} 与客户端配置 {builder.Database} 不一致。");

        await using var connection = new MySqlConnection(builder.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction =
            await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            var affectedRows = 0;
            foreach (var change in request.Changes)
            {
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                BuildCommand(command, change);
                var affected =
                    await command.ExecuteNonQueryAsync(cancellationToken);
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

    private static void BuildCommand(
        MySqlCommand command,
        DatabaseChangePayload change)
    {
        var table = QuoteIdentifier(change.TableName);
        switch (change.Operation)
        {
            case "INSERT":
                EnsureValues(change);
                command.CommandText =
                    $"INSERT INTO {table} (" +
                    string.Join(", ", change.Values.Keys.Select(QuoteIdentifier)) +
                    ") VALUES (" +
                    string.Join(", ", change.Values.Keys.Select(
                        (_, index) => $"@v{index}")) +
                    ");";
                AddParameters(command, change.Values, "v");
                break;
            case "UPDATE":
                EnsureValues(change);
                EnsureKeys(change);
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
                throw new InvalidOperationException(
                    $"不支持的数据库操作：{change.Operation}");
        }
    }

    private static void AddParameters(
        MySqlCommand command,
        IReadOnlyDictionary<string, JsonElement> values,
        string prefix)
    {
        var index = 0;
        foreach (var value in values.Values)
            command.Parameters.AddWithValue(
                $"@{prefix}{index++}", ConvertValue(value));
    }

    private static object ConvertValue(JsonElement value) =>
        value.ValueKind switch
        {
            JsonValueKind.Null or JsonValueKind.Undefined => DBNull.Value,
            JsonValueKind.String => value.GetString() ?? string.Empty,
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number when value.TryGetInt64(out var integer) => integer,
            JsonValueKind.Number when value.TryGetDecimal(out var number) => number,
            _ => value.GetRawText()
        };

    private static void EnsureValues(DatabaseChangePayload change)
    {
        if (change.Values.Count == 0)
            throw new InvalidOperationException(
                $"{change.Operation} {change.TableName} 没有字段。");
    }

    private static void EnsureKeys(DatabaseChangePayload change)
    {
        if (change.KeyValues.Count == 0)
            throw new InvalidOperationException(
                $"{change.Operation} {change.TableName} 缺少主键。");
    }

    private static string QuoteIdentifier(string value) =>
        $"`{value.Replace("`", "``", StringComparison.Ordinal)}`";
}
