using System.Collections.Concurrent;
using DorkDl.Core.Bundle;
using DorkDl.Core.Unity;

namespace DorkDl.Core.Extraction;

public sealed record ExtractOptions(
    string OutputDirectory,
    int Concurrency,
    int Limit = 0,
    bool Verify = false,
    bool Raw = false,
    bool KeepRaw = false,
    IReadOnlySet<string>? Kinds = null);

public sealed record ExtractProgress(
    int Bundles,
    int Files,
    int Converted,
    int Failures,
    long Bytes,
    IReadOnlyDictionary<string, int> Kinds);

// Unpacks and converts bundles in parallel. Concurrency is capped to avoid blowing up memory with multiple huge uncompressed payloads.
public static class BundleExtraction
{
    // Default worker count clamped to 8 (memory/disk bound, not CPU bound).
    public static int DefaultConcurrency => Math.Clamp(Environment.ProcessorCount, 1, 8);

    public static ExtractProgress Run(
        IEnumerable<string> bundlePaths,
        ExtractOptions options,
        IProgress<string>? log = null,
        CancellationToken cancellationToken = default)
    {
        var state = new RunState();
        var kinds = new ConcurrentDictionary<string, int>();
        var paths = options.Limit > 0 ? bundlePaths.Take(options.Limit) : bundlePaths;

        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Max(1, options.Concurrency),
            CancellationToken = cancellationToken,
        };

        try
        {
            Parallel.ForEach(paths, parallelOptions, path => ProcessOne(path, options, state, kinds, log));
        }
        catch (OperationCanceledException)
        {
            // Report partial progress on cancellation.
        }

        return new ExtractProgress(
            state.Bundles, state.Files, state.Converted, state.Failures, state.Bytes, kinds);
    }

    private static void ProcessOne(
        string path,
        ExtractOptions options,
        RunState state,
        ConcurrentDictionary<string, int> kinds,
        IProgress<string>? log)
    {
        var name = Path.GetFileName(path);

        try
        {
            if (options.Verify)
            {
                var bundle = BundleReader.Read(path);
                using var stream = File.OpenRead(path);
                foreach (var archive in bundle.Archives)
                {
                    state.AddBytes(BundleExtractor.ReadPayload(stream, archive).Length);
                    state.AddFiles(archive.Nodes.Count);
                }
            }
            else
            {
                var target = Path.Combine(options.OutputDirectory, Path.GetFileNameWithoutExtension(name));
                foreach (var extracted in BundleExtractor.ExtractAll(path, target))
                {
                    state.AddFiles(1);
                    state.AddBytes(extracted.Size);
                }

                if (!options.Raw)
                {
                    var result = AssetConverter.ConvertDirectory(target, target, options.Kinds);
                    foreach (var asset in result.Assets)
                    {
                        state.AddConverted(1);
                        kinds.AddOrUpdate(asset.Kind, 1, (_, count) => count + 1);
                    }

                    foreach (var error in result.Errors.Take(3))
                    {
                        log?.Report($"WARN {name}: {error}");
                    }

                    if (!options.KeepRaw)
                    {
                        RemoveSerializedFiles(target);
                    }
                }
            }
        }
        catch (Exception e) when (e is BundleFormatException or InvalidDataException or IOException)
        {
            state.AddFailure();
            log?.Report($"FAIL {name}: {e.Message}");
            return;
        }

        var done = state.CompleteBundle();
        if (done % 200 == 0)
        {
            log?.Report($"  {done} bundles, {state.Files} files");
        }
    }

    // Cleans up raw CAB-* files and .resS/.resource data after conversion to leave only extracted assets.
    private static void RemoveSerializedFiles(string directory)
    {
        foreach (var file in Directory.EnumerateFiles(directory))
        {
            var name = Path.GetFileName(file);
            if (name.StartsWith("CAB-", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith(".resS", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith(".resource", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    File.Delete(file);
                }
                catch (IOException)
                {
                    // Ignore locked temp files.
                }
            }
        }
    }

    // Thread-safe progress counters across workers.
    private sealed class RunState
    {
        private int _bundles;
        private int _files;
        private int _converted;
        private int _failures;
        private long _bytes;

        public int Bundles => Volatile.Read(ref _bundles);

        public int Files => Volatile.Read(ref _files);

        public int Converted => Volatile.Read(ref _converted);

        public int Failures => Volatile.Read(ref _failures);

        public long Bytes => Interlocked.Read(ref _bytes);

        public int CompleteBundle() => Interlocked.Increment(ref _bundles);

        public void AddFiles(int count) => Interlocked.Add(ref _files, count);

        public void AddConverted(int count) => Interlocked.Add(ref _converted, count);

        public void AddFailure() => Interlocked.Increment(ref _failures);

        public void AddBytes(long count) => Interlocked.Add(ref _bytes, count);
    }
}
