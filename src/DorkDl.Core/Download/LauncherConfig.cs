using System.Text.Json;

namespace DorkDl.Core.Download;

// Current build info returned by launcher config: client version, full resource version string, active CDN version folders (newest first), and platform base URL.
public sealed record LauncherConfig(
    string ClientVersion,
    string ResourceVersion,
    IReadOnlyList<int> Versions,
    string BaseUrl);

// Queries the unauthenticated launcher flexible_config endpoint to discover current version and CDN URLs.
// Note: data.flexible_config.nexon_plug is an embedded JSON string containing another JSON object with the version info.
public static class LauncherSource
{
    public static string DefaultConfigUrl => ClientValues.LauncherConfigUrl;

    // Platform subfolder under CDN root.
    private const string PlatformDirectory = "AssetBundles_Windows";

    public static async Task<LauncherConfig> FetchAsync(
        HttpClient http,
        string? configUrl = null,
        CancellationToken cancellationToken = default)
    {
        var url = configUrl ?? DefaultConfigUrl;
        var body = await http.GetStringAsync(url, cancellationToken);

        using var document = JsonDocument.Parse(body);
        if (!document.RootElement.TryGetProperty("data", out var data)
            || !data.TryGetProperty("flexible_config", out var flexible)
            || !flexible.TryGetProperty("nexon_plug", out var plugText)
            || plugText.GetString() is not { } plugJson)
        {
            throw new InvalidOperationException(
                $"{url} did not carry data.flexible_config.nexon_plug; the endpoint may have changed");
        }

        // Inner JSON is a string payload that needs a second parse.
        using var plug = JsonDocument.Parse(plugJson);
        var root = plug.RootElement;

        var resourceVersion = Text(root, "ab_resource_version")
            ?? throw new InvalidOperationException("the launcher config named no ab_resource_version");
        var clientVersion = Text(root, "game_client_version") ?? "unknown";
        var address = Text(root, "ab_resource_addr");

        var baseUrl = address is null
            ? CdnSource.DefaultBaseUrl
            : $"{address.TrimEnd('/')}/{PlatformDirectory}";

        return new LauncherConfig(clientVersion, resourceVersion, ParseVersions(resourceVersion), baseUrl);
    }

    // Extracts the last two numeric folder segments from version string (e.g. 2.7.4330.14393.27767 -> [27767, 14393]).
    public static IReadOnlyList<int> ParseVersions(string resourceVersion)
    {
        var versions = new List<int>();
        foreach (var part in resourceVersion.Split('.').Reverse().Take(2))
        {
            if (int.TryParse(part, out var value) && !versions.Contains(value))
            {
                versions.Add(value);
            }
        }

        return versions;
    }

    private static string? Text(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
