using System.Text.RegularExpressions;

namespace DorkDl.Core.Download;

// CDN source and version candidate resolver. Bundle URL is <base>/<version>/<md5>.bundle. Wrong version returns 403, so known version folders are probed in priority order.
public sealed partial class CdnSource(string baseUrl, IReadOnlyList<int> versions)
{
    public static string DefaultBaseUrl => ClientValues.CdnUrl;

    public string BaseUrl { get; } = baseUrl.TrimEnd('/');

    // Candidate version folders, ordered by priority.
    public IReadOnlyList<int> Versions { get; } = versions;

    // Named Version.txt on CDN, but Version_CustomDownload.txt in local install.
    public const string ManifestName = "Version.txt";

    public const string InstalledManifestName = "Version_CustomDownload.txt";

    public string UrlFor(int version, string bundleName) => $"{BaseUrl}/{version}/{bundleName}";

    // Builds CDN source using version numbers discovered in local install manifests and config.ini.
    public static CdnSource FromInstall(GameInstall install, string? baseUrl = null) =>
        Build(
            baseUrl ?? DefaultBaseUrl,
            Path.Combine(install.BundleDirectory, InstalledManifestName),
            ReadConfigVersions(install));

    // Builds CDN source prioritizing versions that appear most often in the download manifest.
    public static CdnSource Build(string baseUrl, string? manifestPath, IEnumerable<int> fallback)
    {
        var counts = new Dictionary<int, int>();

        foreach (var version in ReadManifestVersions(manifestPath))
        {
            counts[version] = counts.GetValueOrDefault(version) + 1;
        }

        foreach (var version in fallback)
        {
            counts.TryAdd(version, 0);
        }

        var ordered = counts.OrderByDescending(kv => kv.Value).Select(kv => kv.Key).ToList();
        return new CdnSource(baseUrl, ordered);
    }

    // Parses version column from lines formatted as: {0}/<md5>.bundle#<MD5>#<size>#<version>
    private static IEnumerable<int> ReadManifestVersions(string? path)
    {
        if (path is null || !File.Exists(path))
        {
            yield break;
        }

        foreach (var line in File.ReadLines(path))
        {
            var fields = line.Split('#');
            if (fields.Length >= 4 && int.TryParse(fields[^1], out var version))
            {
                yield return version;
            }
        }
    }

    // Extracts trailing folder numbers from dotted versions in launcher config.ini.
    private static IEnumerable<int> ReadConfigVersions(GameInstall install)
    {
        var path = Path.Combine(Path.GetDirectoryName(install.Root) ?? install.Root, "config.ini");
        if (!File.Exists(path))
        {
            yield break;
        }

        foreach (Match match in VersionLine().Matches(File.ReadAllText(path)))
        {
            var parts = match.Groups["v"].Value.Split('.');
            foreach (var part in parts.Reverse().Take(2))
            {
                if (int.TryParse(part, out var version) && version > 1000)
                {
                    yield return version;
                }
            }
        }
    }

    [GeneratedRegex(@"^\s*(?:f_ab_v|f_r_ab_v|download_ab_version)\s*=\s*(?<v>[\d.]+)\s*$",
        RegexOptions.Multiline)]
    private static partial Regex VersionLine();
}
