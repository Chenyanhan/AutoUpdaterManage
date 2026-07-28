using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AutoUpdater.Updater;

internal sealed record UpdateManifest
{
    [JsonPropertyName("version")]
    public required string Version { get; init; }

    [JsonPropertyName("package")]
    public required string Package { get; init; }

    [JsonPropertyName("sha256")]
    public required string Sha256 { get; init; }

    [JsonPropertyName("executable")]
    public string? Executable { get; init; }

    [JsonPropertyName("releaseNotes")]
    public string? ReleaseNotes { get; init; }

    [JsonPropertyName("mandatory")]
    public bool Mandatory { get; init; }

    public static async Task<UpdateManifest> LoadAsync(
        string source, HttpClient httpClient, CancellationToken cancellationToken)
    {
        source = NormalizeManifestSource(source);
        string json;
        if (Uri.TryCreate(source, UriKind.Absolute, out var uri) &&
            uri.Scheme is "http" or "https")
            json = await httpClient.GetStringAsync(uri, cancellationToken);
        else
            json = await File.ReadAllTextAsync(Path.GetFullPath(source), cancellationToken);

        var manifest = JsonSerializer.Deserialize<UpdateManifest>(json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException("更新清单内容为空。");
        if (!System.Version.TryParse(manifest.Version, out _))
            throw new InvalidDataException("清单 version 不是有效版本号。");
        if (string.IsNullOrWhiteSpace(manifest.Package) ||
            string.IsNullOrWhiteSpace(manifest.Sha256))
            throw new InvalidDataException("清单缺少 package 或 sha256。");
        return manifest;
    }

    private static string NormalizeManifestSource(string source)
    {
        var trimmed = source.Trim();
        if (Directory.Exists(trimmed))
        {
            var manifestPath = Path.Combine(trimmed, "manifest.json");
            if (!File.Exists(manifestPath))
                throw new FileNotFoundException(
                    $"更新目录中不存在 manifest.json：{trimmed}", manifestPath);
            return manifestPath;
        }
        return trimmed;
    }
}
