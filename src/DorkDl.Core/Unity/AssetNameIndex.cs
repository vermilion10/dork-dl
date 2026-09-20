using System.Text.Json;
using System.Text.Json.Serialization;
using AssetsTools.NET;
using AssetsTools.NET.Extra;
using DorkDl.Core.Bundle;
using DorkDl.Core.Extraction;

namespace DorkDl.Core.Unity;

// Resolved asset name and metadata for a GUID.
public sealed record AssetName(string Guid, string Name, string Type, string Bundle, long PathId);

public sealed record NameIndexReport(int Bundles, int Skipped, int Guids, int Assets, int Failures);

// Builds the GUID-to-name index from bundle AssetBundle.m_Container maps.
// Reads only the head serialized file in each bundle (no texture decoding) and records progress incrementally.
public static class AssetNameIndex
{
    private static readonly JsonSerializerOptions Json = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    // Returns .progress.jsonl path alongside output index.
    public static string ProgressPathFor(string outputPath) => outputPath + ".progress.jsonl";

    // bundleNamer assigns catalog names to pack archives (defaults to filename#index).
    public static NameIndexReport Build(
        IEnumerable<string> bundlePaths,
        string outputPath,
        int concurrency,
        long memoryLimit = 0,
        IProgress<string>? log = null,
        Func<string, int, string>? bundleNamer = null,
        CancellationToken cancellationToken = default)
    {
        bundleNamer ??= (file, index) => index == 0 ? file : $"{file}#{index}";

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
        var progressPath = ProgressPathFor(outputPath);

        var done = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var entries = new List<AssetName>();
        foreach (var line in ReadProgress(progressPath))
        {
            done.Add(line.Bundle);
            entries.AddRange(line.Assets);
        }

        if (done.Count > 0)
        {
            log?.Report($"resuming: {done.Count} bundles already in {Path.GetFileName(progressPath)}");
        }

        var bundles = 0;
        var skipped = 0;
        var failures = 0;
        var budget = new MemoryBudget(memoryLimit > 0 ? memoryLimit : MemoryBudget.DefaultLimit);
        var progressGate = new object();

        var options = new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Max(1, concurrency),
            CancellationToken = cancellationToken,
        };

        try
        {
            using var progress = new StreamWriter(progressPath, append: true);

            Parallel.ForEach(bundlePaths, options, path =>
            {
                var bundleName = Path.GetFileName(path);
                if (done.Contains(bundleName))
                {
                    Interlocked.Increment(ref skipped);
                    return;
                }

                List<AssetName> found;
                try
                {
                    found = ReadBundle(path, budget, bundleNamer, cancellationToken);
                }
                catch (Exception e) when (e is BundleFormatException or InvalidDataException or IOException)
                {
                    Interlocked.Increment(ref failures);
                    log?.Report($"FAIL {bundleName}: {e.Message}");
                    return;
                }

                // Flushed immediately to progress file so interrupted runs resume cleanly.
                var line = JsonSerializer.Serialize(new ProgressLine(bundleName, found), Json);
                lock (progressGate)
                {
                    entries.AddRange(found);
                    progress.WriteLine(line);
                    progress.Flush();
                }

                var count = Interlocked.Increment(ref bundles);
                if (count % 500 == 0)
                {
                    log?.Report($"  {count} bundles, {entries.Count} assets named");
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

        return new NameIndexReport(bundles, skipped, byGuid.Count, entries.Count, failures);
    }

    private sealed record ProgressLine(string Bundle, List<AssetName> Assets);

    private static IEnumerable<ProgressLine> ReadProgress(string path)
    {
        if (!File.Exists(path))
        {
            yield break;
        }

        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            ProgressLine? parsed;
            try
            {
                parsed = JsonSerializer.Deserialize<ProgressLine>(line, Json);
            }
            catch (JsonException)
            {
                // A torn last line from a killed run; that bundle is simply read again.
                continue;
            }

            if (parsed is not null)
            {
                yield return parsed;
            }
        }
    }

    public static IReadOnlyDictionary<string, List<AssetName>> Load(string path)
    {
        using var stream = File.OpenRead(path);
        return JsonSerializer.Deserialize<Dictionary<string, List<AssetName>>>(stream, Json)
               ?? new Dictionary<string, List<AssetName>>();
    }

    // Reads AssetBundle container map from every archive in a file without writing to disk.
    private static List<AssetName> ReadBundle(
        string bundlePath, MemoryBudget budget, Func<string, int, string> bundleNamer, CancellationToken cancellationToken)
    {
        List<AssetName> found = [];
        var bundle = BundleReader.Read(bundlePath);
        var fileName = Path.GetFileName(bundlePath);

        using var stream = File.OpenRead(bundlePath);
        for (var i = 0; i < bundle.Archives.Count; i++)
        {
            ReadArchive(stream, bundle.Archives[i], bundleNamer(fileName, i), budget, found, cancellationToken);
        }

        return found;
    }

    private static void ReadArchive(
        FileStream stream,
        BundleArchive archive,
        string bundleName,
        MemoryBudget budget,
        List<AssetName> found,
        CancellationToken cancellationToken)
    {
        // Decompress only the blocks covering the serialized file header, skipping trailing .resS textures.
        var node = archive.Nodes.FirstOrDefault(n => !n.Path.EndsWith(".resS", StringComparison.OrdinalIgnoreCase)
                                                     && !n.Path.EndsWith(".resource", StringComparison.OrdinalIgnoreCase));
        if (node is null || node.Offset < 0 || node.Offset + node.Size > archive.UncompressedPayloadSize)
        {
            return;
        }

        var needed = node.Offset + node.Size;
        using var lease = budget.Reserve(BundleExtractor.BytesToDecompress(archive, needed), cancellationToken);
        var payload = BundleExtractor.ReadPayload(stream, archive, needed);

        var manager = new AssetsManager();

        try
        {
            using var serialized = new MemoryStream(payload, (int)node.Offset, (int)node.Size, writable: false);
            var instance = manager.LoadAssetsFile(serialized, node.Path, loadDeps: false, null);
            if (instance?.file is null)
            {
                return;
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
                    var name = ReadNameFast(instance.file, info);
                    if (name is null)
                    {
                        var field = manager.GetBaseField(instance, info, AssetReadFlags.None);
                        var nameField = field?["m_Name"];
                        if (nameField is not null && !nameField.IsDummy)
                        {
                            name = nameField.AsString ?? string.Empty;
                        }
                    }

                    if (name is not null)
                    {
                        names[info.PathId] = (name, type.ToString());
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
    }

    // Reads m_Name directly off the asset stream if type tree confirms it is the first field (skips full deserialization).
    private static string? ReadNameFast(AssetsFile file, AssetFileInfo info)
    {
        var type = file.Metadata.FindTypeTreeTypeByID(info.TypeId, info.GetScriptIndex(file));
        if (type is null || type.Nodes.Count < 2)
        {
            return null;
        }

        var first = type.Nodes[1];
        if (first.Level != 1
            || first.GetNameString(type.StringBufferBytes) != "m_Name"
            || first.GetTypeString(type.StringBufferBytes) != "string")
        {
            return null;
        }

        var reader = file.Reader;
        lock (reader)
        {
            reader.Position = info.GetAbsoluteByteOffset(file);
            var length = reader.ReadInt32();
            if (length < 0 || length > info.ByteSize)
            {
                return null;
            }

            return System.Text.Encoding.UTF8.GetString(reader.ReadBytes(length));
        }
    }
}
