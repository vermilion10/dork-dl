using AssetsTools.NET;
using AssetsTools.NET.Extra;
using AssetsTools.NET.Texture;

namespace DorkDl.Core.Unity;

// Slices individual sprites out of texture atlases using m_RD.textureRect. Texture decodes are cached per serialized file to avoid re-decoding the same atlas for multiple sprites.
internal sealed class SpriteSlicer(AssetsManager manager, AssetsFileInstance instance, string sourceDirectory)
    : IDisposable
{
    private readonly Dictionary<long, DecodedTexture?> _textures = [];

    // Crops sprite rectangle from its atlas texture and applies any packing rotation.
    public SpriteCut? Cut(AssetTypeValueField baseField)
    {
        var renderData = baseField["m_RD"];
        if (renderData.IsDummy)
        {
            return null;
        }

        var texturePointer = renderData["texture"];
        if (texturePointer.IsDummy)
        {
            return null;
        }

        // Skip cross-file texture references (bundles keep sprite and texture together).
        if (texturePointer["m_FileID"].AsInt != 0)
        {
            return null;
        }

        var texture = GetTexture(texturePointer["m_PathID"].AsLong);
        if (texture is null)
        {
            return null;
        }

        var rect = renderData["textureRect"];
        if (rect.IsDummy)
        {
            return null;
        }

        // Unity textureRect origin is bottom-left (matches DecodeTextureRaw row order).
        // Floor origin and round size to avoid losing edge pixels on subpixel floats.
        var x = (int)Math.Floor(rect["x"].AsFloat);
        var y = (int)Math.Floor(rect["y"].AsFloat);
        var width = (int)Math.Round(rect["width"].AsFloat);
        var height = (int)Math.Round(rect["height"].AsFloat);

        if (width <= 0 || height <= 0)
        {
            return null;
        }

        // Clamp to texture bounds rather than failing on 1-pixel overflows.
        x = Math.Clamp(x, 0, texture.Width);
        y = Math.Clamp(y, 0, texture.Height);
        width = Math.Min(width, texture.Width - x);
        height = Math.Min(height, texture.Height - y);
        if (width <= 0 || height <= 0)
        {
            return null;
        }

        var settings = new SpriteSettings(renderData["settingsRaw"].IsDummy ? 0 : renderData["settingsRaw"].AsUInt);
        var cropped = Crop(texture, x, y, width, height);
        var (oriented, orientedWidth, orientedHeight) =
            ApplyRotation(cropped, width, height, settings.Rotation);

        return new SpriteCut(oriented, orientedWidth, orientedHeight, settings);
    }

    // Crops region from bottom-up RGBA buffer and flips rows top-to-bottom for PNG export.
    private static byte[] Crop(DecodedTexture texture, int x, int y, int width, int height)
    {
        var cropped = new byte[width * height * 4];
        var sourceStride = texture.Width * 4;
        var targetStride = width * 4;

        for (var row = 0; row < height; row++)
        {
            var sourceRow = y + row;
            // Invert row index: row 0 in output is the last row in bottom-up source.
            var targetRow = height - 1 - row;
            Array.Copy(
                texture.Pixels,
                sourceRow * sourceStride + x * 4,
                cropped,
                targetRow * targetStride,
                targetStride);
        }

        return cropped;
    }

    // Undoes atlas packing rotation if present.
    private static (byte[] Pixels, int Width, int Height) ApplyRotation(
        byte[] pixels, int width, int height, SpritePackingRotation rotation)
    {
        switch (rotation)
        {
            case SpritePackingRotation.None:
                return (pixels, width, height);

            case SpritePackingRotation.FlipHorizontal:
                return (Map(pixels, width, height, width, height, (x, y) => (width - 1 - x, y)), width, height);

            case SpritePackingRotation.FlipVertical:
                return (Map(pixels, width, height, width, height, (x, y) => (x, height - 1 - y)), width, height);

            case SpritePackingRotation.Rotate180:
                return (Map(pixels, width, height, width, height, (x, y) => (width - 1 - x, height - 1 - y)), width, height);

            // Swap width and height back for 90-degree rotated sprites.
            case SpritePackingRotation.Rotate90:
                return (Map(pixels, width, height, height, width, (x, y) => (y, height - 1 - x)), height, width);

            default:
                return (pixels, width, height);
        }
    }

    // Remaps pixels according to coordinate transform function.
    private static byte[] Map(
        byte[] pixels,
        int width,
        int height,
        int targetWidth,
        int targetHeight,
        Func<int, int, (int X, int Y)> source)
    {
        var mapped = new byte[targetWidth * targetHeight * 4];
        for (var y = 0; y < targetHeight; y++)
        {
            for (var x = 0; x < targetWidth; x++)
            {
                var (sourceX, sourceY) = source(x, y);
                if (sourceX < 0 || sourceY < 0 || sourceX >= width || sourceY >= height)
                {
                    continue;
                }

                Array.Copy(pixels, (sourceY * width + sourceX) * 4, mapped, (y * targetWidth + x) * 4, 4);
            }
        }

        return mapped;
    }

    private DecodedTexture? GetTexture(long pathId)
    {
        if (_textures.TryGetValue(pathId, out var cached))
        {
            return cached;
        }

        var decoded = Decode(pathId);
        _textures[pathId] = decoded;
        return decoded;
    }

    private DecodedTexture? Decode(long pathId)
    {
        var info = instance.file.GetAssetInfo(pathId);
        if (info is null || (AssetClassID)info.TypeId != AssetClassID.Texture2D)
        {
            return null;
        }

        var baseField = manager.GetBaseField(instance, info, AssetReadFlags.None);
        if (baseField is null || baseField.IsDummy)
        {
            return null;
        }

        var texture = TextureFile.ReadTextureFile(baseField);
        if (texture.m_Width <= 0 || texture.m_Height <= 0)
        {
            return null;
        }

        var data = AssetConverter.ReadPixels(texture, baseField, sourceDirectory);
        if (data is null || data.Length == 0)
        {
            return null;
        }

        // DecodeTextureRaw returns bottom-up RGBA matching Unity internal layout.
        var pixels = texture.DecodeTextureRaw(data, useBgra: false);
        if (pixels is null || pixels.Length != texture.m_Width * texture.m_Height * 4)
        {
            return null;
        }

        return new DecodedTexture(pixels, texture.m_Width, texture.m_Height);
    }

    public void Dispose() => _textures.Clear();

    private sealed record DecodedTexture(byte[] Pixels, int Width, int Height);
}

// Top-up RGBA pixels for a sliced sprite.
internal sealed record SpriteCut(byte[] Pixels, int Width, int Height, SpriteSettings Settings);

// Unpacks m_RD.settingsRaw bitfield (atlas flag, packing mode, rotation).
internal readonly struct SpriteSettings(uint raw)
{
    public bool Packed => (raw & 1) != 0;

    public bool TightlyPacked => Packed && ((raw >> 1) & 1) == 0;

    public SpritePackingRotation Rotation => (SpritePackingRotation)((raw >> 2) & 0xF);
}

internal enum SpritePackingRotation
{
    None = 0,
    FlipHorizontal = 1,
    FlipVertical = 2,
    Rotate180 = 3,
    Rotate90 = 4,
}
