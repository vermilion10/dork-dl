using System.Text;
using System.Text.Json;

namespace DorkDl.Core.Download;

// On-demand downloadable file (voice pack, cutscene, extra bundle) outside the main Addressables catalog.
public sealed record ExtraAsset(string Section, string Group, string RemoteName, string? RelativePath)
{
    // .gf files are zip archives containing a single asset file.
    public bool IsPackaged => RemoteName.EndsWith(".gf", StringComparison.OrdinalIgnoreCase);
}

// Parser for Version_Ex1.bin (manifest for voice packs, cutscenes, extras). The file is JSON XOR-obfuscated with its own ASCII filename "Version_Ex1.bin" as repeating key.
public sealed class ExtraManifest
{
    public const string FileName = "Version_Ex1.bin";

    private ExtraManifest(IReadOnlyList<ExtraAsset> assets)
    {
        Assets = assets;
    }

    public IReadOnlyList<ExtraAsset> Assets { get; }

    public IEnumerable<string> Groups => Assets.Select(a => a.Group).Distinct().Order(StringComparer.Ordinal);

    public static ExtraManifest Load(string path)
    {
        var json = Decrypt(File.ReadAllBytes(path), Path.GetFileName(path));
        return Parse(json);
    }

    // XOR decrypts using fileName as the repeating key.
    public static byte[] Decrypt(byte[] raw, string fileName = FileName)
    {
        var key = Encoding.ASCII.GetBytes(fileName);
        if (key.Length == 0)
        {
            throw new ArgumentException("the key is the manifest's file name and cannot be empty", nameof(fileName));
        }

        var plain = new byte[raw.Length];
        for (var i = 0; i < raw.Length; i++)
        {
            plain[i] = (byte)(raw[i] ^ key[i % key.Length]);
        }

        return plain;
    }

    public static ExtraManifest Parse(byte[] json)
    {
        using var document = JsonDocument.Parse(json);
        var assets = new List<ExtraAsset>();

        foreach (var section in document.RootElement.EnumerateObject())
        {
            if (section.Value.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            foreach (var group in section.Value.EnumerateObject())
            {
                if (group.Value.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var entry in group.Value.EnumerateArray())
                {
                    var text = entry.GetString();
                    if (string.IsNullOrEmpty(text))
                    {
                        continue;
                    }

                    var separator = text.IndexOf(';');
                    var remote = separator < 0 ? text : text[..separator];
                    var relative = separator < 0 ? null : text[(separator + 1)..].Replace('\\', '/');

                    assets.Add(new ExtraAsset(section.Name, group.Name, remote, relative));
                }
            }
        }

        return new ExtraManifest(assets);
    }
}
