using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using AssetsTools.NET;
using AssetsTools.NET.Extra;
using DorkDl.Core.Bundle;

namespace DorkDl.Core.Unity;

// Resolved asset name and type for a catalog GUID.
public sealed record AssetName(string Guid, string Name, string Type, string Bundle, long PathId);

public sealed record NameIndexReport(int Bundles, int Guids, int Assets, int Failures);

// Builds a GUID-to-name lookup from AssetBundle m_Container maps so catalog hashes can be resolved to human-readable names. Only reads header serialized files without decompressing pixel data.
public static class AssetNameIndex
{
    private static readonly JsonSerializerOptions Json = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    public static NameIndexReport Build(
        IEnumerable<string> bundlePaths,
        string outputPath,
        int concurrency,
        IProgress<string>? log = null,
        CancellationToken cancellationToken = default)
    {
        var entries = new ConcurrentBag<AssetName>();
        var bundles = 0;
        var failures = 0;

        var options = new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Max(1, concurrency),
            CancellationToken = cancellationToken,
        };

        try
        {
            Parallel.ForEach(bundlePaths, options, path =>
            {
                try
                {
                    foreach (var entry in ReadBundle(path))
                    {
                        entries.Add(entry);
                    }
                }
                catch (Exception e) when (e is BundleFormatException or InvalidDataException or IOException)
                {
                    Interlocked.Increment(ref failures);
                    log?.Report($"FAIL {Path.GetFileName(path)}: {e.Message}");
                    return;
                }

                var done = Interlocked.Increment(ref bundles);
                if (done % 500 == 0)
                {
                    log?.Report($"  {done} bundles, {entries.Count} assets named");
                }
            });
        }
        catch (OperationCanceledException)
        {
            // Save whatever we resolved so far if cancelled.
        }

        var byGuid = entries
            .GroupBy(e => e.Guid, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g => g.OrderBy(e => e.Type, StringComparer.Ordinal)
                    .ThenBy(e => e.PathId)
                    .ToList(),
                StringComparer.OrdinalIgnoreCase);

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
        using (var stream = File.Create(outputPath))
        {
            JsonSerializer.Serialize(stream, byGuid, Json);
        }

        return new NameIndexReport(bundles, byGuid.Count, entries.Count, failures);
    }

    public static IReadOnlyDictionary<string, List<AssetName>> Load(string path)
    {
        using var stream = File.OpenRead(path);
        return JsonSerializer.Deserialize<Dictionary<string, List<AssetName>>>(stream, Json)
               ?? new Dictionary<string, List<AssetName>>();
    }

    // Reads AssetBundle container map from the bundle's first archive without writing to disk.
    private static List<AssetName> ReadBundle(string bundlePath)
    {
        List<AssetName> found = [];

        var bundle = BundleReader.Read(bundlePath);
        var archive = bundle.Archives.FirstOrDefault();
        if (archive is null)
        {
            return found;
        }

        // Decompress only the blocks covering the serialized file header, skipping trailing .resS textures.
        var node = archive.Nodes.FirstOrDefault(n => !n.Path.EndsWith(".resS", StringComparison.OrdinalIgnoreCase)
                                                     && !n.Path.EndsWith(".resource", StringComparison.OrdinalIgnoreCase));
        if (node is null || node.Offset < 0 || node.Offset + node.Size > archive.UncompressedPayloadSize)
        {
            return found;
        }

        byte[] payload;
        using (var stream = File.OpenRead(bundlePath))
        {
            payload = BundleExtractor.ReadPayload(stream, archive, node.Offset + node.Size);
        }

        var bundleName = Path.GetFileName(bundlePath);
        var manager = new AssetsManager();

        try
        {
            using var serialized = new MemoryStream(payload, (int)node.Offset, (int)node.Size, writable: false);
            var instance = manager.LoadAssetsFile(serialized, node.Path, loadDeps: false, null);
            if (instance?.file is null)
            {
                return found;
            }

            // Index names by PathID first so container entries can be resolved in one pass.
            var names = new Dictionary<long, (string Name, string Type)>();
            foreach (var info in instance.file.AssetInfos)
            {
                var type = (AssetClassID)info.TypeId;
                if (type == AssetClassID.AssetBundle)
                {
                    continue;
                }

                try
                {
                    var field = manager.GetBaseField(instance, info, AssetReadFlags.None);
                    var name = field?["m_Name"];
                    if (name is not null && !name.IsDummy)
                    {
                        names[info.PathId] = (name.AsString ?? string.Empty, type.ToString());
                    }
                }
                catch (Exception e) when (e is not OutOfMemoryException)
                {
                    // Ignore unreadable assets
                }
            }

            foreach (var info in instance.file.AssetInfos)
            {
                if ((AssetClassID)info.TypeId != AssetClassID.AssetBundle)
                {
                    continue;
                }

                var field = manager.GetBaseField(instance, info, AssetReadFlags.None);
                var container = field?["m_Container"]["Array"];
                if (container is null || container.IsDummy)
                {
                    continue;
                }

                foreach (var pair in container.Children)
                {
                    var guid = pair["first"].AsString;
                    var pathId = pair["second"]["asset"]["m_PathID"].AsLong;
                    if (string.IsNullOrEmpty(guid) || !names.TryGetValue(pathId, out var asset))
                    {
                        continue;
                    }

                    found.Add(new AssetName(guid, asset.Name, asset.Type, bundleName, pathId));
                }
            }
        }
        finally
        {
            manager.UnloadAll(true);
        }

        return found;
    }
}
