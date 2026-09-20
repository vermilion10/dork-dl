using System.IO.Compression;

namespace DorkDl.Core.Download;

public sealed record ExtraDownloadOptions(
    string OutputDirectory,
    int Concurrency = 4,
    bool Overwrite = false,
    bool KeepPackages = false);

public sealed record ExtraDownloadProgress(int Downloaded, int Skipped, int Unavailable, long Bytes);

// Downloads on-demand files from Version_Ex1.bin and extracts any .gf packages into target paths.
public sealed class ExtraDownloader(HttpClient http, CdnSource source)
{
    public async Task<ExtraDownloadProgress> DownloadAsync(
        IEnumerable<ExtraAsset> assets,
        ExtraDownloadOptions options,
        IProgress<string>? log = null,
        CancellationToken cancellationToken = default)
    {
        var downloaded = 0;
        var skipped = 0;
        var unavailable = 0;
        long bytes = 0;

        // Cache preferred version folder to avoid 403 probes on every file.
        var preferred = source.Versions.FirstOrDefault();

        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Max(1, options.Concurrency),
            CancellationToken = cancellationToken,
        };

        await Parallel.ForEachAsync(assets, parallelOptions, async (asset, token) =>
        {
            var relative = asset.RelativePath ?? asset.RemoteName;
            var target = Path.Combine(options.OutputDirectory, relative.Replace('/', Path.DirectorySeparatorChar));

            if (!options.Overwrite && File.Exists(target))
            {
                Interlocked.Increment(ref skipped);
                return;
            }

            var payload = await FetchAsync(asset, preferred, token);
            if (payload is null)
            {
                Interlocked.Increment(ref unavailable);
                log?.Report($"MISS {asset.RemoteName} ({asset.Group})");
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(target))!);

            long written;
            if (asset.IsPackaged)
            {
                written = Unpack(payload, target);
                if (options.KeepPackages)
                {
                    await File.WriteAllBytesAsync(
                        Path.Combine(options.OutputDirectory, asset.RemoteName), payload, token);
                }
            }
            else
            {
                await File.WriteAllBytesAsync(target, payload, token);
                written = payload.Length;
            }

            Interlocked.Increment(ref downloaded);
            Interlocked.Add(ref bytes, written);
            log?.Report($"OK   {relative}");
        });

        return new ExtraDownloadProgress(downloaded, skipped, unavailable, bytes);
    }

    // Extracts single entry from .gf zip, using target path to prevent path traversal.
    private static long Unpack(byte[] payload, string target)
    {
        using var archive = new ZipArchive(new MemoryStream(payload), ZipArchiveMode.Read);
        var entry = archive.Entries.FirstOrDefault()
            ?? throw new InvalidDataException("the .gf package is empty");

        using var input = entry.Open();
        using var output = File.Create(target);
        input.CopyTo(output);
        return entry.Length;
    }

    private async Task<byte[]?> FetchAsync(ExtraAsset asset, int preferred, CancellationToken cancellationToken)
    {
        var candidates = source.Versions.OrderByDescending(v => v == preferred);
        foreach (var version in candidates)
        {
            using var response = await http.GetAsync(
                source.UrlFor(version, asset.RemoteName), cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                return await response.Content.ReadAsByteArrayAsync(cancellationToken);
            }
        }

        return null;
    }
}
