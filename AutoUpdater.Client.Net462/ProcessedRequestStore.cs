using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;

namespace AutoUpdater.Client.Net462
{
    internal sealed class ProcessedRequest
    {
        public Guid RequestId { get; set; }
        public string Operation { get; set; }
        public bool Success { get; set; }
        public string Message { get; set; }
        public DateTime ProcessedAt { get; set; }
        public int AcceptedChanges { get; set; }
    }

    internal sealed class ProcessedRequestStore
    {
        private readonly object _gate = new object();
        private readonly string _path;
        private readonly Dictionary<Guid, ProcessedRequest> _records;

        public ProcessedRequestStore(string installationDirectory)
        {
            _path = Path.Combine(
                installationDirectory,
                ".autoupdater",
                "processed-requests-net462.json");
            _records = Load();
        }

        public bool TryGet(Guid requestId, out ProcessedRequest record)
        {
            lock (_gate)
                return _records.TryGetValue(requestId, out record);
        }

        public void Save(ProcessedRequest record)
        {
            lock (_gate)
            {
                _records[record.RequestId] = record;
                var expiration = DateTime.UtcNow.AddDays(-30);
                foreach (var id in _records
                             .Where(item =>
                                 item.Value.ProcessedAt < expiration)
                             .Select(item => item.Key)
                             .ToArray())
                    _records.Remove(id);
                foreach (var id in _records.Values
                             .OrderByDescending(item => item.ProcessedAt)
                             .Skip(2000)
                             .Select(item => item.RequestId)
                             .ToArray())
                    _records.Remove(id);

                Directory.CreateDirectory(Path.GetDirectoryName(_path));
                var temporaryPath = _path + ".tmp";
                File.WriteAllText(
                    temporaryPath,
                    JsonConvert.SerializeObject(
                        _records.Values,
                        Formatting.Indented));
                if (File.Exists(_path))
                    File.Delete(_path);
                File.Move(temporaryPath, _path);
            }
        }

        private Dictionary<Guid, ProcessedRequest> Load()
        {
            try
            {
                if (!File.Exists(_path))
                    return new Dictionary<Guid, ProcessedRequest>();
                var records =
                    JsonConvert.DeserializeObject<List<ProcessedRequest>>(
                        File.ReadAllText(_path)) ??
                    new List<ProcessedRequest>();
                return records
                    .GroupBy(item => item.RequestId)
                    .ToDictionary(
                        group => group.Key,
                        group => group.Last());
            }
            catch
            {
                return new Dictionary<Guid, ProcessedRequest>();
            }
        }
    }
}
