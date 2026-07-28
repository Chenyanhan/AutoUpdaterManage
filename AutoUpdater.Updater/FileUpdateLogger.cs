using System.IO;

namespace AutoUpdater.Updater;

internal sealed class FileUpdateLogger : IAsyncDisposable
{
    private readonly StreamWriter _writer;

    public FileUpdateLogger(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _writer = new StreamWriter(path, append: true) { AutoFlush = true };
    }

    public Task WriteAsync(string message)
    {
        var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} {message}";
        Console.WriteLine(line);
        return _writer.WriteLineAsync(line);
    }

    public ValueTask DisposeAsync() => _writer.DisposeAsync();
}
