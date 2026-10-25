namespace DorkDl.Core;

// The server addresses, install folder name and audio key the tools use.
// Private copy: the values are hardcoded here. The public copy has the same members but reads them from client.json,
// so tools/export-public.sh never overwrites this file.
public static class ClientValues
{
    public static bool IsConfigured => true;

    // Unused here; kept so code that reports a missing client.json in the public copy compiles unchanged.
    public static string MissingMessage => "";

    // Bundle CDN root; a bundle's URL is <root>/<version>/<md5>.bundle.
    public static string CdnUrl => "https://cdn.invalid/prod/AssetBundles_Windows";

    // The launcher's unauthenticated flexible_config endpoint (per region).
    public static string LauncherConfigUrl => "https://cdn.invalid/flexible_config";

    // The folder inside the install root that holds LocalCache and StreamingAssets.
    public static string DataFolder => "Game_Data";

    // PC HCA key from vgmstream (0x0). Android uses a different key.
    public static ulong HcaKey => 0;
}
