using AutoUpdaterManage.Models;
using Microsoft.Data.Sqlite;
using System.IO;

namespace AutoUpdaterManage.Services;

public sealed class TaskHistoryStore : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _connectionString;
    private bool _initialized;

    public TaskHistoryStore(string? databasePath = null)
    {
        databasePath ??= Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AutoUpdaterManage",
            "tasks.db");
        DatabasePath = Path.GetFullPath(databasePath);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        }.ToString();
    }

    public string DatabasePath { get; }
    public event Action<Exception>? PersistenceError;

    public async Task<IReadOnlyList<UpdateTaskRecord>> LoadAsync(
        int limit = 1000,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await _gate.WaitAsync(cancellationToken);
            try
            {
                await EnsureInitializedCoreAsync(cancellationToken);
                await using var connection = await OpenAsync(cancellationToken);
                await using var command = connection.CreateCommand();
                command.CommandText =
                    """
                    SELECT request_id, device_id, device_name, ip_address,
                           operation, source, source_version, target_version,
                           state, message, created_at, updated_at, result_version
                    FROM update_tasks
                    ORDER BY created_at DESC
                    LIMIT $limit;
                    """;
                command.Parameters.AddWithValue("$limit", limit);
                var result = new List<UpdateTaskRecord>();
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    result.Add(new UpdateTaskRecord(
                        Guid.Parse(reader.GetString(0)),
                        reader.GetString(1),
                        reader.GetString(2),
                        reader.GetString(3),
                        Enum.Parse<UpdateTaskOperation>(reader.GetString(4)),
                        GetNullableString(reader, 5),
                        GetNullableString(reader, 6),
                        GetNullableString(reader, 7),
                        Enum.Parse<UpdateTaskState>(reader.GetString(8)),
                        reader.GetString(9),
                        DateTime.Parse(reader.GetString(10), null,
                            System.Globalization.DateTimeStyles.RoundtripKind),
                        DateTime.Parse(reader.GetString(11), null,
                            System.Globalization.DateTimeStyles.RoundtripKind),
                        GetNullableString(reader, 12)));
                }
                return result;
            }
            finally
            {
                _gate.Release();
            }
        }
        catch (Exception ex)
        {
            PersistenceError?.Invoke(ex);
            return [];
        }
    }

    public async Task SaveAsync(
        UpdateTaskRecord record,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await _gate.WaitAsync(cancellationToken);
            try
            {
                await EnsureInitializedCoreAsync(cancellationToken);
                await using var connection = await OpenAsync(cancellationToken);
                await using var command = connection.CreateCommand();
                command.CommandText =
                    """
                    INSERT INTO update_tasks (
                        request_id, device_id, device_name, ip_address,
                        operation, source, source_version, target_version,
                        state, message, created_at, updated_at, result_version)
                    VALUES (
                        $request_id, $device_id, $device_name, $ip_address,
                        $operation, $source, $source_version, $target_version,
                        $state, $message, $created_at, $updated_at, $result_version)
                    ON CONFLICT(request_id) DO UPDATE SET
                        state = excluded.state,
                        message = excluded.message,
                        updated_at = excluded.updated_at,
                        result_version = excluded.result_version;
                    """;
                AddParameters(command, record);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
            finally
            {
                _gate.Release();
            }
        }
        catch (Exception ex)
        {
            PersistenceError?.Invoke(ex);
        }
    }

    private async Task EnsureInitializedCoreAsync(CancellationToken cancellationToken)
    {
        if (_initialized) return;
        Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);
        SQLitePCL.Batteries_V2.Init();
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            CREATE TABLE IF NOT EXISTS update_tasks (
                request_id TEXT PRIMARY KEY,
                device_id TEXT NOT NULL,
                device_name TEXT NOT NULL,
                ip_address TEXT NOT NULL,
                operation TEXT NOT NULL,
                source TEXT NULL,
                source_version TEXT NULL,
                target_version TEXT NULL,
                state TEXT NOT NULL,
                message TEXT NOT NULL,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                result_version TEXT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_update_tasks_created_at
                ON update_tasks(created_at DESC);
            CREATE INDEX IF NOT EXISTS ix_update_tasks_device_id
                ON update_tasks(device_id);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
        _initialized = true;
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private static string? GetNullableString(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static void AddParameters(
        SqliteCommand command, UpdateTaskRecord record)
    {
        command.Parameters.AddWithValue("$request_id", record.RequestId.ToString("D"));
        command.Parameters.AddWithValue("$device_id", record.DeviceId);
        command.Parameters.AddWithValue("$device_name", record.DeviceName);
        command.Parameters.AddWithValue("$ip_address", record.IpAddress);
        command.Parameters.AddWithValue("$operation", record.Operation.ToString());
        command.Parameters.AddWithValue("$source", (object?)record.Source ?? DBNull.Value);
        command.Parameters.AddWithValue("$source_version",
            (object?)record.SourceVersion ?? DBNull.Value);
        command.Parameters.AddWithValue("$target_version",
            (object?)record.TargetVersion ?? DBNull.Value);
        command.Parameters.AddWithValue("$state", record.State.ToString());
        command.Parameters.AddWithValue("$message", record.Message);
        command.Parameters.AddWithValue("$created_at", record.CreatedAt.ToString("O"));
        command.Parameters.AddWithValue("$updated_at", record.UpdatedAt.ToString("O"));
        command.Parameters.AddWithValue("$result_version",
            (object?)record.ResultVersion ?? DBNull.Value);
    }

    public void Dispose() => _gate.Dispose();
}
