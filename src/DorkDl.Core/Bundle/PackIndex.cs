using System.Collections.Concurrent;
using System.Text.Json;

namespace DorkDl.Core.Bundle;

// Physical location of a catalog bundle on disk (file, sub-archive index, offset, size).
public sealed record PackedBundle(string Name, string File, int Archive, long Offset, long Size);

public sealed record PackIndexReport(int Files, int Packs, int Archives, int Resolved, int Failures);

// Maps catalog bundle names to disk files.
// Only ~7,654 of 56k catalog bundles ship as standalone files. The rest are packed into ~258 multi-archive files. We match archives to catalog bundles by hashing each sub-archive (plain MD5) and checking GFF index entries.
public static class PackIndex
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    public static PackIndexReport Build(
        GameInstall install,
        GffContainer gff,
        string outputPath,
        int concurrency,
        IProgress<string>? log = null,
        CancellationToken cancellationToken = default)
    {
        var byHash = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in gff.Entries)
        {
            byHash[Convert.ToHexString(entry.PlainHash)] = entry.Name;
            names.Add(entry.Name);
        }

        var gffPath = install.FindGffIndex();
        var entries = new ConcurrentBag<PackedBundle>();
        var files = 0;
        var packs = 0;
        var archives = 0;
        var failures = 0;

        var options = new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Max(1, concurrency),
            CancellationToken = cancellationToken,
        };

        Parallel.ForEach(install.EnumerateBundles().Where(p => p != gffPath), options, path =>
        {
            var fileName = Path.GetFileName(path);
            try
            {
                var bundle = BundleReader.Read(path);
                Interlocked.Increment(ref files);

                // A file the catalog names is its own bundle; no hashing needed.
                if (bundle.Archives.Count == 1 && names.Contains(fileName))
                {
                    var only = bundle.Archives[0];
                    entries.Add(new PackedBundle(fileName, fileName, 0, only.ArchiveOffset, only.ArchiveSize));
                    return;
                }

                var hashes = BundleExtractor.ComputeArchivePlainHashes(path, bundle);
                Interlocked.Increment(ref packs);
                for (var i = 0; i < hashes.Count; i++)
                {
                    Interlocked.Increment(ref archives);
                    var archive = bundle.Archives[i];
                    if (byHash.TryGetValue(Convert.ToHexString(hashes[i]), out var name))
                    {
                        entries.Add(new PackedBundle(name, fileName, i, archive.ArchiveOffset, archive.ArchiveSize));
                    }
                    else
                    {
                        log?.Report($"  {fileName}#{i} matches no catalog bundle");
                    }
                }
            }
            catch (Exception e) when (e is BundleFormatException or IOException)
            {
                Interlocked.Increment(ref failures);
                log?.Report($"FAIL {fileName}: {e.Message}");
            }
        });

        var ordered = entries.OrderBy(e => e.Name, StringComparer.Ordinal).ToList();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
        using (var stream = File.Create(outputPath))
        {
            JsonSerializer.Serialize(stream, ordered, Json);
        }

        return new PackIndexReport(files, packs, archives, ordered.Count, failures);
    }

    // Loads index into dictionary keyed by catalog bundle name.
    public static IReadOnlyDictionary<string, PackedBundle> Load(string path)
    {
        using var stream = File.OpenRead(path);
        var entries = JsonSerializer.Deserialize<List<PackedBundle>>(stream, Json) ?? [];
        return entries.ToDictionary(e => e.Name, StringComparer.OrdinalIgnoreCase);
    }
}
