using System.Text.Json;
using System.IO;

namespace AutoUpdater.Client;

internal sealed class ProcessedRequestStore
{
    private readonly object _gate = new();
    private readonly string _path;
    private readonly Dictionary<Guid, ProcessedRequest> _records;

    public ProcessedRequestStore(string installationDirectory)
    {
        _path = Path.Combine(
            installationDirectory, ".autoupdater", "processed-requests.json");
        _records = Load();
    }

    public bool TryGet(Guid requestId, out ProcessedRequest record)
    {
        lock (_gate)
            return _records.TryGetValue(requestId, out record!);
    }

    public void Save(ProcessedRequest record)
    {
        lock (_gate)
        {
            _records[record.RequestId] = record;
            var expiration = DateTimeOffset.UtcNow.AddDays(-30);
            foreach (var requestId in _records
                         .Where(pair => pair.Value.ProcessedAt < expiration)
                         .Select(pair => pair.Key)
                         .ToArray())
                _records.Remove(requestId);

            if (_records.Count > 2000)
            {
                foreach (var requestId in _records.Values
                             .OrderByDescending(item => item.ProcessedAt)
                             .Skip(2000)
                             .Select(item => item.RequestId)
                             .ToArray())
                    _records.Remove(requestId);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temporaryPath = _path + ".tmp";
            File.WriteAllText(
                temporaryPath,
                JsonSerializer.Serialize(_records.Values,
                    new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporaryPath, _path, overwrite: true);
        }
    }

    private Dictionary<Guid, ProcessedRequest> Load()
    {
        try
        {
            if (!File.Exists(_path)) return [];
            var records = JsonSerializer.Deserialize<List<ProcessedRequest>>(
                              File.ReadAllText(_path)) ?? [];
            return records
                .GroupBy(item => item.RequestId)
                .ToDictionary(group => group.Key, group => group.Last());
        }
        catch
        {
            return [];
        }
    }
}

internal sealed record ProcessedRequest(
    Guid RequestId,
    string Operation,
    bool Accepted,
    string Message,
    DateTimeOffset ProcessedAt);
