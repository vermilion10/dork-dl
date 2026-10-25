using System.Globalization;
using System.Text.Json;

namespace DorkDl.Core;

// The server addresses, install folder name and audio key the tools use. None of these ship with the source: they're
// read from client.json (fields as in client.example.json), looked for next to the executable, then in the user's
// application-data folder (dork-dl/client.json). Without the file each value throws FileNotFoundException at first
// use, which the CLI reports as one line.
public static class ClientValues
{
    public const string FileName = "client.json";

    // Declared before Config, which reads it while the type initializes.
    public static IReadOnlyList<string> SearchPaths { get; } =
    [
        Path.Combine(AppContext.BaseDirectory, FileName),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "dork-dl", FileName),
    ];

    private static readonly ClientFile? Config = Load();

    public static bool IsConfigured => Config is not null;

    public static string MissingMessage =>
        $"No {FileName} found. Copy client.example.json to {SearchPaths[^1]} and fill in the server addresses and audio key.";

    // Bundle CDN root; a bundle's URL is <root>/<version>/<md5>.bundle.
    public static string CdnUrl => Require().CdnUrl.TrimEnd('/');

    // The launcher's unauthenticated flexible_config endpoint (per region).
    public static string LauncherConfigUrl => Require().LauncherConfigUrl;

    // The folder inside the install root that holds LocalCache and StreamingAssets.
    public static string DataFolder => Require().DataFolder;

    // The PC HCA audio key.
    public static ulong HcaKey => ulong.Parse(Require().HcaKey.Replace("0x", "", StringComparison.OrdinalIgnoreCase),
        NumberStyles.HexNumber, CultureInfo.InvariantCulture);

    private static ClientFile Require() => Config ?? throw new FileNotFoundException(MissingMessage);

    private static ClientFile? Load()
    {
        if (SearchPaths.FirstOrDefault(File.Exists) is not { } path)
        {
            return null;
        }

        try
        {
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip };
            return JsonSerializer.Deserialize<ClientFile>(File.ReadAllText(path), options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed record ClientFile(string CdnUrl, string LauncherConfigUrl, string DataFolder, string HcaKey);
}
