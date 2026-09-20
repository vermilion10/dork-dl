using AssetsTools.NET;
using AssetsTools.NET.Extra;
using AssetsTools.NET.Texture;

namespace DorkDl.Core.Unity;

public sealed record ConvertedAsset(string Name, string Path, string Kind);

public sealed record ConvertResult(
    IReadOnlyList<ConvertedAsset> Assets,
    int Skipped,
    IReadOnlyList<string> Errors);

// Converts Unity serialized files to usable formats: Texture2D to PNG, sprites via SpriteSlicer, and TextAsset to raw bytes.
// Resolves external pixel streams from sibling .resS/.resource files.
public static class AssetConverter
{
    public const string SpriteDirectoryName = "sprites";

    // Converts serialized files in sourceDirectory to outputDirectory. Optionally filters by kind (Texture2D, Sprite, TextAsset).
    public static ConvertResult ConvertDirectory(
        string sourceDirectory,
        string outputDirectory,
        IReadOnlySet<string>? kinds = null)
    {
        var assets = new List<ConvertedAsset>();
        var errors = new List<string>();
        var skipped = 0;

        var candidates = Directory.EnumerateFiles(sourceDirectory)
            .Where(p => !p.EndsWith(".resS", StringComparison.OrdinalIgnoreCase)
                        && !p.EndsWith(".resource", StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.Ordinal);

        foreach (var path in candidates)
        {
            var manager = new AssetsManager();
            try
            {
                using var stream = File.OpenRead(path);
                var instance = manager.LoadAssetsFile(stream, path, loadDeps: false, null);
                if (instance?.file is null)
                {
                    skipped++;
                    continue;
                }

                using var slicer = new SpriteSlicer(manager, instance, sourceDirectory);
                foreach (var info in instance.file.AssetInfos)
                {
                    try
                    {
                        var converted = ConvertOne(
                            manager, instance, info, slicer, sourceDirectory, outputDirectory, kinds);
                        if (converted is null)
                        {
                            skipped++;
                        }
                        else
                        {
                            assets.Add(converted);
                        }
                    }
                    catch (Exception e) when (e is not OutOfMemoryException)
                    {
                        errors.Add($"{Path.GetFileName(path)}#{info.PathId}: {e.Message}");
                    }
                }
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                // Non-serialized or unreadable files are skipped.
                errors.Add($"{Path.GetFileName(path)}: {e.Message}");
            }
            finally
            {
                manager.UnloadAll(true);
            }
        }

        return new ConvertResult(assets, skipped, errors);
    }

    private static ConvertedAsset? ConvertOne(
        AssetsManager manager,
        AssetsFileInstance instance,
        AssetFileInfo info,
        SpriteSlicer slicer,
        string sourceDirectory,
        string outputDirectory,
        IReadOnlySet<string>? kinds)
    {
        var typeId = (AssetClassID)info.TypeId;
        if (typeId is not (AssetClassID.Texture2D or AssetClassID.TextAsset or AssetClassID.Sprite))
        {
            return null;
        }

        // Filter by kind early before reading type trees.
        if (kinds is not null && !kinds.Contains(typeId.ToString()))
        {
            return null;
        }

        var baseField = manager.GetBaseField(instance, info, AssetReadFlags.None);
        if (baseField is null || baseField.IsDummy)
        {
            return null;
        }

        var name = baseField["m_Name"].AsString;
        if (string.IsNullOrWhiteSpace(name))
        {
            name = $"{typeId}_{info.PathId}";
        }

        Directory.CreateDirectory(outputDirectory);

        if (typeId == AssetClassID.Sprite)
        {
            var cut = slicer.Cut(baseField);
            if (cut is null)
            {
                return null;
            }

            // Save sprites in a dedicated subfolder to avoid name collisions with parent atlas textures.
            var spriteDirectory = Path.Combine(outputDirectory, SpriteDirectoryName);
            Directory.CreateDirectory(spriteDirectory);

            var spriteTarget = UniquePath(spriteDirectory, name, ".png");
            using (var output = File.Create(spriteTarget))
            {
                new StbImageWriteSharp.ImageWriter().WritePng(
                    cut.Pixels,
                    cut.Width,
                    cut.Height,
                    StbImageWriteSharp.ColorComponents.RedGreenBlueAlpha,
                    output);
            }

            return new ConvertedAsset(name, spriteTarget, "Sprite");
        }

        if (typeId == AssetClassID.Texture2D)
        {
            var texture = TextureFile.ReadTextureFile(baseField);
            if (texture.m_Width <= 0 || texture.m_Height <= 0)
            {
                return null;
            }

            var pixels = ReadPixels(texture, baseField, sourceDirectory);
            if (pixels is null || pixels.Length == 0)
            {
                return null;
            }

            var target = UniquePath(outputDirectory, name, ".png");
            if (!texture.DecodeTextureImage(pixels, target, ImageExportType.Png, 100))
            {
                throw new InvalidOperationException(
                    $"could not decode texture format {(TextureFormat)texture.m_TextureFormat}");
            }

            return new ConvertedAsset(name, target, "Texture2D");
        }

        // TextAsset: m_Script often holds binary, so grab raw bytes rather than decoding as text.
        var script = baseField["m_Script"];
        if (script.IsDummy)
        {
            return null;
        }

        var bytes = script.Value?.ValueType == AssetValueType.ByteArray
            ? script.AsByteArray
            : System.Text.Encoding.UTF8.GetBytes(script.AsString ?? string.Empty);

        var textTarget = UniquePath(outputDirectory, name, LooksLikeText(bytes) ? ".txt" : ".bytes");
        File.WriteAllBytes(textTarget, bytes);
        return new ConvertedAsset(name, textTarget, "TextAsset");
    }

    // Reads texture pixels, resolving m_StreamData to sibling .resS files on disk.
    internal static byte[]? ReadPixels(TextureFile texture, AssetTypeValueField baseField, string sourceDirectory)
    {
        var streamData = baseField["m_StreamData"];
        var streamPath = streamData.IsDummy ? null : streamData["path"].AsString;

        if (string.IsNullOrEmpty(streamPath))
        {
            return texture.pictureData;
        }

        var resourceName = Path.GetFileName(streamPath.Replace('\\', '/'));
        var resourcePath = Path.Combine(sourceDirectory, resourceName);
        if (!File.Exists(resourcePath))
        {
            return texture.pictureData;
        }

        var offset = streamData["offset"].AsLong;
        var size = streamData["size"].AsLong;
        if (size <= 0 || offset < 0)
        {
            return texture.pictureData;
        }

        using var stream = File.OpenRead(resourcePath);
        if (offset + size > stream.Length)
        {
            throw new InvalidOperationException(
                $"{resourceName} is {stream.Length} bytes but the texture wants {offset}..{offset + size}");
        }

        var pixels = new byte[size];
        stream.Seek(offset, SeekOrigin.Begin);
        stream.ReadExactly(pixels);
        return pixels;
    }

    // Appends counter suffix if filename already exists.
    private static string UniquePath(string directory, string name, string extension)
    {
        var safe = string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        var candidate = Path.Combine(directory, safe + extension);
        var counter = 1;
        while (File.Exists(candidate))
        {
            candidate = Path.Combine(directory, $"{safe}_{counter++}{extension}");
        }

        return candidate;
    }

    private static bool LooksLikeText(byte[] bytes)
    {
        var sample = bytes.Take(512).ToArray();
        if (sample.Length == 0)
        {
            return true;
        }

        var printable = sample.Count(b => b is >= 0x20 and < 0x7F or 0x09 or 0x0A or 0x0D);
        return printable * 10 >= sample.Length * 9;
    }
}
