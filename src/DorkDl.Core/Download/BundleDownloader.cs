using System.Net;
using System.Security.Cryptography;

namespace DorkDl.Core.Download;

public sealed record DownloadOptions(
    string OutputDirectory,
    int Concurrency = 4,
    bool DryRun = false,
    bool RetryUnavailable = false,
    bool Overwrite = false,
    int Limit = 0);

public sealed record DownloadProgress(
    int Completed,
    int Unavailable,
    int Failed,
    int Skipped,
    long BytesDownloaded);

// Mirrors bundles from CDN. Probes candidate version folders, caches hits in index.json, and flags 403s across all versions as Unavailable so they aren't retried every run.
public sealed class BundleDownloader(HttpClient http, CdnSource source, DownloadIndex index)
{
    // Auto-save download index every N finished bundles.
    private const int SaveEvery = 100;

    public async Task<DownloadProgress> DownloadAsync(
        IEnumerable<BundleRequest> requests,
        DownloadOptions options,
        IProgress<string>? log = null,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(options.OutputDirectory);

        var completed = 0;
        var unavailable = 0;
        var failed = 0;
        var skipped = 0;
        long bytes = 0;
        var finished = 0;

        using var limiter = new SemaphoreSlim(options.Concurrency);
        var tasks = new List<Task>();

        foreach (var request in Select(requests, options))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await limiter.WaitAsync(cancellationToken);

            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    var outcome = await DownloadOneAsync(request, options, log, cancellationToken);
                    switch (outcome.State)
                    {
                        case DownloadState.Complete:
                            Interlocked.Increment(ref completed);
                            Interlocked.Add(ref bytes, outcome.Size);
                            break;
                        case DownloadState.Unavailable:
                            Interlocked.Increment(ref unavailable);
                            break;
                        default:
                            Interlocked.Increment(ref failed);
                            break;
                    }

                    if (Interlocked.Increment(ref finished) % SaveEvery == 0)
                    {
                        index.Save();
                    }
                }
                finally
                {
                    limiter.Release();
                }
            }, cancellationToken));
        }

        await Task.WhenAll(tasks);
        index.Save();
        return new DownloadProgress(completed, unavailable, failed, skipped, bytes);
    }

    private IEnumerable<BundleRequest> Select(IEnumerable<BundleRequest> requests, DownloadOptions options)
    {
        var taken = 0;
        foreach (var request in requests)
        {
            var record = index.Get(request.Name);
            var target = Path.Combine(options.OutputDirectory, request.Name);

            if (!options.Overwrite && record.State == DownloadState.Complete && File.Exists(target))
            {
                continue;
            }

            if (record.State == DownloadState.Unavailable && !options.RetryUnavailable)
            {
                continue;
            }

            yield return request;

            if (options.Limit > 0 && ++taken >= options.Limit)
            {
                yield break;
            }
        }
    }

    private async Task<DownloadRecord> DownloadOneAsync(
        BundleRequest request,
        DownloadOptions options,
        IProgress<string>? log,
        CancellationToken cancellationToken)
    {
        var record = index.Get(request.Name);
        var target = Path.Combine(options.OutputDirectory, request.Name);

        // Try previously working version first before probing the rest.
        var candidates = record.Version > 0
            ? new[] { record.Version }.Concat(source.Versions.Where(v => v != record.Version))
            : source.Versions;

        foreach (var version in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var url = source.UrlFor(version, request.Name);

            HttpResponseMessage response;
            try
            {
                response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
            {
                record.State = DownloadState.Failed;
                record.Error = e.Message;
                index.Set(request.Name, record);
                log?.Report($"FAIL {request.Name}: {e.Message}");
                return record;
            }

            using (response)
            {
                if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
                {
                    // 403/404 means wrong version folder or unserved bundle; try next candidate.
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                {
                    record.State = DownloadState.Failed;
                    record.Error = $"HTTP {(int)response.StatusCode}";
                    index.Set(request.Name, record);
                    log?.Report($"FAIL {request.Name}: HTTP {(int)response.StatusCode}");
                    return record;
                }

                var etag = response.Headers.ETag?.Tag.Trim('"');

                if (options.DryRun)
                {
                    record.State = DownloadState.Complete;
                    record.Version = version;
                    record.Size = response.Content.Headers.ContentLength ?? 0;
                    record.Etag = etag;
                    index.Set(request.Name, record);
                    log?.Report($"DRY  {request.Name}  v{version}  {record.Size} bytes");
                    return record;
                }

                // Stream to .part temp file first so interrupted downloads aren't mistaken for complete bundles.
                var temporary = target + ".part";
                string hash;
                long written;
                await using (var output = File.Create(temporary))
                await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken))
                using (var md5 = MD5.Create())
                await using (var digest = new CryptoStream(output, md5, CryptoStreamMode.Write))
                {
                    await input.CopyToAsync(digest, cancellationToken);
                    await digest.FlushFinalBlockAsync(cancellationToken);
                    written = output.Length;
                    hash = Convert.ToHexString(md5.Hash!).ToLowerInvariant();
                }

                if (etag is not null && !string.Equals(etag, hash, StringComparison.OrdinalIgnoreCase))
                {
                    File.Delete(temporary);
                    record.State = DownloadState.Failed;
                    record.Error = $"MD5 {hash} did not match ETag {etag}";
                    index.Set(request.Name, record);
                    log?.Report($"FAIL {request.Name}: checksum mismatch");
                    return record;
                }

                File.Move(temporary, target, overwrite: true);

                record.State = DownloadState.Complete;
                record.Version = version;
                record.Size = written;
                record.Etag = etag ?? hash;
                record.Error = null;
                index.Set(request.Name, record);
                log?.Report($"OK   {request.Name}  v{version}  {written} bytes");
                return record;
            }
        }

        record.State = DownloadState.Unavailable;
        record.Error = $"not served under any of: {string.Join(", ", source.Versions)}";
        index.Set(request.Name, record);
        log?.Report($"MISS {request.Name}");
        return record;
    }
}

// Bundle download request with expected size from catalog.
public sealed record BundleRequest(string Name, long ExpectedSize = 0);
