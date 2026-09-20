using System.Buffers.Binary;

namespace DorkDl.Core.Bundle;

// Bundle entry in the GFF index. PlainHash is the MD5 of the un-obfuscated UnityFS bundle (not the raw file on disk and not the catalog hash). NameHash is the MD5 name parsed back to 16 bytes.
public sealed record GffEntry(string Name, byte[] NameHash, byte[] PlainHash);

// Parser for the GFF container (the ~235 MB .bundle file that's actually an index of all ~56k bundles).
// Layout:
//   0x00: "GFF\0" magic
//   0x04: 4-byte XOR key
//   0x08: entry count (repeated 3x)
//   0x14: count x 12-byte index table (redundant positional IDs, skipped)
//   ...   count x 256-byte name records: [len byte] + [XORed name] + [random padding]
//   ...   count x 4096-byte hash blocks: mostly zeros, name hash at +8, plain bundle MD5 at +32
public sealed class GffContainer
{
    private static ReadOnlySpan<byte> Magic => "GFF\0"u8;

    public const int HeaderLength = 20;
    public const int IndexEntryLength = 12;
    public const int NameRecordLength = 256;
    public const int HashBlockLength = 4096;
    public const int KeyLength = 4;

    // Hash block offsets (the rest of the 4KB block is zero padding).
    private const int NameHashOffset = 8;
    private const int PlainHashOffset = 32;
    private const int HashLength = 16;

    private GffContainer(IReadOnlyList<GffEntry> entries)
    {
        Entries = entries;
    }

    public IReadOnlyList<GffEntry> Entries { get; }

    public static bool IsGff(ReadOnlySpan<byte> header) =>
        header.Length >= Magic.Length && header[..Magic.Length].SequenceEqual(Magic);

    public static GffContainer Load(string path)
    {
        using var stream = File.OpenRead(path);
        return Read(stream);
    }

    public static GffContainer Read(Stream stream)
    {
        var header = new byte[HeaderLength];
        stream.ReadExactly(header);
        if (!IsGff(header))
        {
            throw new BundleFormatException("not a GFF container");
        }

        var key = header.AsSpan(4, KeyLength).ToArray();
        var count = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8));
        if (count is 0 or > 1_000_000)
        {
            throw new BundleFormatException($"implausible GFF entry count {count}");
        }

        var entries = (int)count;
        var expected = HeaderLength
                       + (long)entries * (IndexEntryLength + NameRecordLength + HashBlockLength);
        if (stream.Length < expected)
        {
            throw new BundleFormatException(
                $"GFF declares {entries} entries, which needs {expected} bytes, but the file is {stream.Length}");
        }

        var nameBase = HeaderLength + (long)entries * IndexEntryLength;
        var hashBase = nameBase + (long)entries * NameRecordLength;

        var names = ReadNames(stream, nameBase, entries, key);
        var result = new List<GffEntry>(entries);

        var block = new byte[HashBlockLength];
        stream.Seek(hashBase, SeekOrigin.Begin);
        for (var i = 0; i < entries; i++)
        {
            stream.ReadExactly(block);
            result.Add(new GffEntry(
                names[i],
                block.AsSpan(NameHashOffset, HashLength).ToArray(),
                block.AsSpan(PlainHashOffset, HashLength).ToArray()));
        }

        return new GffContainer(result);
    }

    private static string[] ReadNames(Stream stream, long nameBase, int entries, byte[] key)
    {
        var names = new string[entries];
        var record = new byte[NameRecordLength];

        stream.Seek(nameBase, SeekOrigin.Begin);
        for (var i = 0; i < entries; i++)
        {
            stream.ReadExactly(record);

            // Length byte is plaintext. The 4-byte key starts at the name itself, indexed from byte 0.
            var length = record[0];
            if (length == 0 || length > NameRecordLength - 1)
            {
                throw new BundleFormatException($"GFF name record {i} claims a length of {length}");
            }

            var name = new char[length];
            for (var j = 0; j < length; j++)
            {
                name[j] = (char)(record[1 + j] ^ key[j % KeyLength]);
            }

            names[i] = new string(name);
        }

        return names;
    }
}
