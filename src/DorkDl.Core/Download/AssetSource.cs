namespace DorkDl.Core.Download;

// Resolves catalog, Version_Ex1.bin extras manifest, and CDN version folders from either a local install or CDN cache.
public sealed class AssetSource
{
    private AssetSource(CdnSource cdn, string catalogPath, string? extraManifestPath, string description)
    {
        Cdn = cdn;
        CatalogPath = catalogPath;
        ExtraManifestPath = extraManifestPath;
        Description = description;
    }

    public CdnSource Cdn { get; }

    // Path to cached or local catalog_main_*.bin.
    public string CatalogPath { get; }

    public string? ExtraManifestPath { get; }

    public string Description { get; }

    public static AssetSource FromInstall(GameInstall install, string? baseUrl = null)
    {
        var catalog = install.FindCatalog()
            ?? throw new FileNotFoundException(
                $"No catalog_main_*.bin in {install.BundleDirectory}");

        return new AssetSource(
            CdnSource.FromInstall(install, baseUrl),
            catalog,
            install.FindExtraManifest(),
            install.Root);
    }

    // Remote-only bootstrap: queries launcher endpoint, then fetches and caches manifest/catalog using ETag checks.
    public static async Task<AssetSource> FromCdnAsync(
        HttpClient http,
        string cacheDirectory,
        string? configUrl = null,
        string? baseUrl = null,
        IProgress<string>? log = null,
        CancellationToken cancellationToken = default)
    {
        var config = await LauncherSource.FetchAsync(http, configUrl, cancellationToken);
        log?.Report($"launcher reports client {config.ClientVersion}, resources {config.ResourceVersion}");

        if (config.Versions.Count == 0)
        {
            throw new InvalidOperationException(
                $"the launcher's version '{config.ResourceVersion}' named no CDN folders");
        }

        Directory.CreateDirectory(cacheDirectory);

        var root = baseUrl ?? config.BaseUrl;
        var version = config.Versions[0];
        var current = new CdnSource(root, config.Versions);

        // Download Version.txt from CDN to discover older active version folders.
        var manifestPath = await FetchCachedAsync(
            http, current.UrlFor(version, CdnSource.ManifestName),
            Path.Combine(cacheDirectory, CdnSource.ManifestName),
            required: false, log, cancellationToken);

        var cdn = CdnSource.Build(root, manifestPath, config.Versions);

        var catalogName = $"catalog_main_{version}.bin";
        var catalog = await FetchCachedAsync(
            http, current.UrlFor(version, catalogName), Path.Combine(cacheDirectory, catalogName),
            required: true, log, cancellationToken);

        var extras = await FetchCachedAsync(
            http, current.UrlFor(version, ExtraManifest.FileName),
            Path.Combine(cacheDirectory, ExtraManifest.FileName),
            required: false, log, cancellationToken);

        return new AssetSource(cdn, catalog!, extras, $"CDN {cdn.BaseUrl} at version {version}");
    }

    private static async Task<string?> FetchCachedAsync(
        HttpClient http,
        string url,
        string target,
        bool required,
        IProgress<string>? log,
        CancellationToken cancellationToken)
    {
        var name = Path.GetFileName(target);

        if (File.Exists(target))
        {
            using var head = new HttpRequestMessage(HttpMethod.Head, url);
            using var probe = await http.SendAsync(head, cancellationToken);
            if (probe.IsSuccessStatusCode && MatchesEtag(probe, target))
            {
                log?.Report($"cached {name}");
                return target;
            }
        }

        using var response = await http.GetAsync(url, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            if (required)
            {
                throw new InvalidOperationException($"{url} returned {(int)response.StatusCode}");
            }

            log?.Report($"not served: {name}");
            return null;
        }

        // Write to .part temp file to prevent half-downloaded cached files.
        var temp = target + ".part";
        await using (var output = File.Create(temp))
        {
            await response.Content.CopyToAsync(output, cancellationToken);
        }

        File.Move(temp, target, overwrite: true);
        log?.Report($"downloaded {name}, {new FileInfo(target).Length} bytes");
        return target;
    }

    // Checks cached file MD5 against S3 ETag (skips multipart ETags containing '-').
    private static bool MatchesEtag(HttpResponseMessage response, string path)
    {
        var etag = response.Headers.ETag?.Tag?.Trim('"');
        if (etag is null || etag.Contains('-'))
        {
            return false;
        }

        using var stream = File.OpenRead(path);
        var hash = Convert.ToHexString(System.Security.Cryptography.MD5.HashData(stream));
        return hash.Equals(etag, StringComparison.OrdinalIgnoreCase);
    }
}
