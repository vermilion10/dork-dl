using System.Text.Json;
using System.Text.Json.Serialization;

namespace DorkDl.Core.Download;

// Download state tracking for bundles.
public enum DownloadState
{
    Pending = 0,
    Complete = 1,
    Unavailable = 2, // 403/404 across all version folders; skipped on later runs unless retried
    Failed = 3,      // Network error, timeout, or hash mismatch
}

public sealed class DownloadRecord
{
    public DownloadState State { get; set; }

    // Version folder where this bundle was found, skipping probing on future runs.
    public int Version { get; set; }

    public long Size { get; set; }

    // S3 ETag (MD5 hash).
    public string? Etag { get; set; }

    public string? Error { get; set; }
}

// Persisted download state index. Caches resolved version numbers so future runs skip probing.
public sealed class DownloadIndex
{
    private readonly object _gate = new();
    private readonly string _path;

    private DownloadIndex(string path, Dictionary<string, DownloadRecord> records)
    {
        _path = path;
        Records = records;
    }

    [JsonInclude]
    public Dictionary<string, DownloadRecord> Records { get; }

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static DownloadIndex Load(string path)
    {
        if (!File.Exists(path))
        {
            return new DownloadIndex(path, []);
        }

        try
        {
            var records = JsonSerializer.Deserialize<Dictionary<string, DownloadRecord>>(
                File.ReadAllText(path), SerializerOptions);
            return new DownloadIndex(path, records ?? []);
        }
        catch (JsonException)
        {
            // Corrupt or interrupted index file; start fresh.
            return new DownloadIndex(path, []);
        }
    }

    public DownloadRecord Get(string bundleName)
    {
        lock (_gate)
        {
            if (!Records.TryGetValue(bundleName, out var record))
            {
                Records[bundleName] = record = new DownloadRecord();
            }

            return record;
        }
    }

    public void Set(string bundleName, DownloadRecord record)
    {
        lock (_gate)
        {
            Records[bundleName] = record;
        }
    }

    // Atomic write via temp file and rename.
    public void Save()
    {
        lock (_gate)
        {
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var temporary = _path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(Records, SerializerOptions));
            File.Move(temporary, _path, overwrite: true);
        }
    }

    public int CountOf(DownloadState state)
    {
        lock (_gate)
        {
            return Records.Values.Count(r => r.State == state);
        }
    }
}
