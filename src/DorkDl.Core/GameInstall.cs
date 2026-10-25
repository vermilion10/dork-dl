namespace DorkDl.Core;

// Locates folders in a local install (bundles and tables live under <data folder>/LocalCache/Data).
public sealed class GameInstall
{
    private GameInstall(string root, string dataDirectory)
    {
        Root = root;
        DataDirectory = dataDirectory;
    }

    public string Root { get; }

    public string DataDirectory { get; }

    // Downloaded bundles, Addressables catalog, and Version_CustomDownload.txt manifest.
    public string BundleDirectory => Path.Combine(DataDirectory, "LocalCache", "Data", "AssetBundles_Windows");

    // Plaintext protobuf data tables and localization files.
    public string TableDirectory => Path.Combine(DataDirectory, "LocalCache", "Data", "Table");

    // Raw unpacked assets (CRI .acb/.awb audio, .usm cutscenes). Plain files with no XOR or bundle wrapper.
    public string LooseAssetDirectory => Path.Combine(BundleDirectory, "Assets");

    // Finds Version_Ex1.bin manifest for on-demand extras (voice packs, cutscenes).
    public string? FindExtraManifest()
    {
        var path = Path.Combine(BundleDirectory, "Version_Ex1.bin");
        return File.Exists(path) ? path : null;
    }

    // Finds the single GFF index file in BundleDirectory by checking for "GFF\0" magic.
    public string? FindGffIndex()
    {
        var magic = new byte[4];
        foreach (var path in EnumerateBundles())
        {
            using var stream = File.OpenRead(path);
            if (stream.Length >= magic.Length)
            {
                stream.ReadExactly(magic);
                if (Bundle.GffContainer.IsGff(magic))
                {
                    return path;
                }
            }
        }

        return null;
    }

    // Enumerates all .acb cue sheets under the loose asset tree.
    public IEnumerable<string> EnumerateCueSheets(string? assetDirectory = null)
    {
        var root = assetDirectory ?? LooseAssetDirectory;
        if (!Directory.Exists(root))
        {
            yield break;
        }

        var found = Directory.EnumerateFiles(root, "*.acb", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal);
        foreach (var path in found)
        {
            yield return path;
        }
    }

    // Returns sibling .awb streaming bank if present.
    public static string? FindStreamingBank(string cueSheetPath)
    {
        var candidate = Path.ChangeExtension(cueSheetPath, ".awb");
        return File.Exists(candidate) ? candidate : null;
    }

    // Enumerates all .usm cutscene videos under the loose asset tree.
    public IEnumerable<string> EnumerateVideos(string? assetDirectory = null)
    {
        var root = assetDirectory ?? LooseAssetDirectory;
        if (!Directory.Exists(root))
        {
            yield break;
        }

        var found = Directory.EnumerateFiles(root, "*.usm", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal);
        foreach (var path in found)
        {
            yield return path;
        }
    }

    // Installer-shipped bundles (StreamingAssets) rather than downloaded cache.
    public string StreamingBundleDirectory =>
        Path.Combine(DataDirectory, "StreamingAssets", "aa", "StandaloneWindows64");

    // Resolves game install from either the root folder or its data folder (ClientValues.DataFolder).
    public static GameInstall Open(string path)
    {
        var full = Path.GetFullPath(path);
        if (!Directory.Exists(full))
        {
            throw new DirectoryNotFoundException($"No such directory: {full}");
        }

        if (Path.GetFileName(full).Equals(ClientValues.DataFolder, StringComparison.OrdinalIgnoreCase))
        {
            return new GameInstall(Path.GetDirectoryName(full) ?? full, full);
        }

        var data = Path.Combine(full, ClientValues.DataFolder);
        if (Directory.Exists(data))
        {
            return new GameInstall(full, data);
        }

        throw new DirectoryNotFoundException(
            $"{full} does not look like a game install; expected a {ClientValues.DataFolder} directory inside it");
    }

    // Finds catalog_main_<version>.bin, choosing the highest version number if multiple are present.
    public string? FindCatalog()
    {
        if (!Directory.Exists(BundleDirectory))
        {
            return null;
        }

        return Directory.EnumerateFiles(BundleDirectory, "catalog_main_*.bin")
            .OrderByDescending(ParseCatalogVersion)
            .ThenByDescending(p => p, StringComparer.Ordinal)
            .FirstOrDefault();
    }

    private static long ParseCatalogVersion(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        var separator = name.LastIndexOf('_');
        return separator >= 0 && long.TryParse(name[(separator + 1)..], out var version) ? version : -1;
    }

    // Enumerates all *.bundle files in sorted order.
    public IEnumerable<string> EnumerateBundles()
    {
        if (!Directory.Exists(BundleDirectory))
        {
            yield break;
        }

        foreach (var file in Directory.EnumerateFiles(BundleDirectory, "*.bundle").Order(StringComparer.Ordinal))
        {
            yield return file;
        }
    }
}
