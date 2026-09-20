namespace DorkDl.Core.Bundle;

// Bundle obfuscation logic:
// - UnityFS header XORed with a 16-byte repeating key: [mask(4B), 0,0,0,0, mask[0]^0x02, mask[1..3], 0,0,0,0].
// - Bytes 4..7 of the key are zero, so "yFS\0" survives in the clear at offset 4 (or bitwise-inverted as 86 B9 AC FF).
// - Offset 0 must decrypt to "Unit", so: mask = header[0..4] ^ "Unit" (undoing bitwise inversion first if inverted).
// - Only the first 32 KiB (ObfuscatedWindow) of each archive is scrambled; everything past that is raw data.
public static class BundleObfuscation
{
    // "yFS\0" signature left unmasked at offset 4.
    private static ReadOnlySpan<byte> PlainSignature => [0x79, 0x46, 0x53, 0x00];

    // Bitwise NOT of PlainSignature.
    private static ReadOnlySpan<byte> InvertedSignature => [0x86, 0xB9, 0xAC, 0xFF];

    // Expected plaintext for offset 0..3.
    private static ReadOnlySpan<byte> MagicPrefix => "Unit"u8;

    // Magic for non-UnityFS GFF index containers.
    private static ReadOnlySpan<byte> GffMagic => "GFF\0"u8;

    public const int KeyLength = 16;

    // Only the first 32 KB of an archive is obfuscated; data after this is plaintext.
    public const int ObfuscatedWindow = 0x8000;

    // Minimum header bytes needed to derive key.
    public const int MinimumHeaderLength = 8;

    // Derives the XOR key and inversion flag from the first 8 bytes.
    public static bool TryDeriveKey(ReadOnlySpan<byte> header, out BundleKey key)
    {
        key = default;
        if (header.Length < MinimumHeaderLength)
        {
            return false;
        }

        bool inverted;
        if (header[4..8].SequenceEqual(PlainSignature))
        {
            inverted = false;
        }
        else if (header[4..8].SequenceEqual(InvertedSignature))
        {
            inverted = true;
        }
        else
        {
            return false;
        }

        // Invert the first 4 bytes if needed so we can XOR against "Unit".
        Span<byte> prefix = stackalloc byte[4];
        for (var i = 0; i < 4; i++)
        {
            prefix[i] = inverted ? (byte)~header[i] : header[i];
        }

        var mask = new byte[4];
        for (var i = 0; i < 4; i++)
        {
            mask[i] = (byte)(prefix[i] ^ MagicPrefix[i]);
        }

        key = new BundleKey(mask, inverted);
        return true;
    }

    // Checks if the file is a GFF container instead of a UnityFS bundle.
    public static bool IsGffContainer(ReadOnlySpan<byte> header) =>
        header.Length >= 4 && header[..4].SequenceEqual(GffMagic);
}

// 16-byte repeating XOR key and inversion state for an archive.
public readonly struct BundleKey
{
    private readonly byte[] _key;

    // Reconstructs the key from a known mask and inversion flag.
    public static BundleKey FromMask(uint mask, bool inverted) => new(
        [(byte)(mask >> 24), (byte)(mask >> 16), (byte)(mask >> 8), (byte)mask], inverted);

    internal BundleKey(byte[] mask, bool inverted)
    {
        Inverted = inverted;
        Mask = (uint)(mask[0] << 24 | mask[1] << 16 | mask[2] << 8 | mask[3]);

        // Key pattern: [mask, 0,0,0,0, mask[0]^0x02, mask[1..3], 0,0,0,0]
        _key =
        [
            mask[0], mask[1], mask[2], mask[3], 0, 0, 0, 0,
            (byte)(mask[0] ^ 0x02), mask[1], mask[2], mask[3], 0, 0, 0, 0,
        ];
    }

    public bool Inverted { get; }

    public uint Mask { get; }

    // Decrypts buffer in-place. archiveOffset is relative to the archive start to keep key phase. Bytes past ObfuscatedWindow are left untouched.
    public void Decrypt(Span<byte> buffer, long archiveOffset)
    {
        for (var i = 0; i < buffer.Length; i++)
        {
            var offset = archiveOffset + i;
            if (offset >= BundleObfuscation.ObfuscatedWindow)
            {
                break;
            }

            var b = buffer[i];
            if (Inverted)
            {
                b = (byte)~b;
            }

            buffer[i] = (byte)(b ^ _key[offset % BundleObfuscation.KeyLength]);
        }
    }
}
