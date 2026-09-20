using System.Buffers.Binary;
using System.Text;
using K4os.Compression.LZ4;

namespace DorkDl.Core.Bundle;

// Reads UnityFS container metadata (archive chain, headers, block tables, node lists). Only decrypts headers so sweeping all bundles stays fast.
public static class BundleReader
{
    // Generous buffer for probing the header and block table (usually a few KB).
    private const int HeaderProbeLength = 64 * 1024;

    // Circuit breaker to avoid infinite loops on garbage files.
    private const int MaxArchivesPerFile = 4096;

    public static BundleFile Read(string path)
    {
        using var stream = File.OpenRead(path);
        return Read(stream, Path.GetFileName(path));
    }

    public static BundleFile Read(Stream stream, string fileName)
    {
        var fileSize = stream.Length;
        var archives = new List<BundleArchive>();
        long offset = 0;
        string? trailingReason = null;

        while (offset < fileSize)
        {
            if (archives.Count >= MaxArchivesPerFile)
            {
                trailingReason = $"stopped after {MaxArchivesPerFile} archives";
                break;
            }

            BundleArchive archive;
            try
            {
                archive = ReadArchive(stream, fileSize, offset, fileName);
            }
            catch (BundleFormatException e)
            {
                // Keep any archives read so far and record why the walk stopped.
                if (archives.Count == 0)
                {
                    throw;
                }

                trailingReason = e.Message;
                break;
            }

            archives.Add(archive);

            if (archive.ArchiveSize <= 0)
            {
                trailingReason = $"archive at offset {offset} declares a size of {archive.ArchiveSize}";
                break;
            }

            offset += archive.ArchiveSize;
        }

        return new BundleFile(fileName, fileSize, archives, offset, trailingReason);
    }

    private static BundleArchive ReadArchive(Stream stream, long fileSize, long archiveOffset, string fileName)
    {
        var probeLength = (int)Math.Min(HeaderProbeLength, fileSize - archiveOffset);
        var probe = new byte[probeLength];
        stream.Seek(archiveOffset, SeekOrigin.Begin);
        stream.ReadExactly(probe, 0, probeLength);

        if (!BundleObfuscation.TryDeriveKey(probe, out var key))
        {
            var signature = probeLength >= 8
                ? Convert.ToHexString(probe.AsSpan(4, 4))
                : "<truncated>";
            throw new BundleFormatException(
                BundleObfuscation.IsGffContainer(probe)
                    ? $"{fileName}+{archiveOffset} is a GFF container, which is not supported yet"
                    : $"{fileName}+{archiveOffset} is not a recognised archive (signature {signature})");
        }

        // Key phase starts at 0 for each archive.
        key.Decrypt(probe, 0);

        var reader = new BigEndianReader(probe);
        if (!reader.ReadBytes(8).SequenceEqual("UnityFS\0"u8))
        {
            throw new BundleFormatException($"{fileName}+{archiveOffset} did not decrypt to a UnityFS archive");
        }

        var header = new BundleHeader(
            FormatVersion: reader.ReadInt32(),
            UnityVersion: reader.ReadCString(),
            UnityRevision: reader.ReadCString(),
            Size: reader.ReadInt64(),
            CompressedBlocksInfoSize: reader.ReadUInt32(),
            UncompressedBlocksInfoSize: reader.ReadUInt32(),
            Flags: reader.ReadUInt32());

        if (header.Size <= 0 || archiveOffset + header.Size > fileSize)
        {
            throw new BundleFormatException(
                $"{fileName}+{archiveOffset} declares a size of {header.Size}, which does not fit "
                + $"in the remaining {fileSize - archiveOffset} bytes");
        }

        // Format 7 aligns block table to 16-byte boundary.
        if (header.FormatVersion >= 7)
        {
            reader.AlignTo(16);
        }

        byte[] blocksInfo;
        long payloadOffset;
        if (header.BlocksInfoAtEnd)
        {
            // Block table is at archive end; read and decrypt using archive-relative offset.
            var relativeOffset = header.Size - header.CompressedBlocksInfoSize;
            blocksInfo = new byte[header.CompressedBlocksInfoSize];
            stream.Seek(archiveOffset + relativeOffset, SeekOrigin.Begin);
            stream.ReadExactly(blocksInfo);
            key.Decrypt(blocksInfo, relativeOffset);
            payloadOffset = reader.Position;
        }
        else
        {
            var available = probe.Length - reader.Position;
            if (available < header.CompressedBlocksInfoSize)
            {
                throw new BundleFormatException(
                    $"{fileName}+{archiveOffset} has a {header.CompressedBlocksInfoSize}-byte block "
                    + $"table that overruns the {HeaderProbeLength}-byte header probe");
            }

            blocksInfo = reader.ReadBytes((int)header.CompressedBlocksInfoSize).ToArray();
            payloadOffset = reader.Position;
        }

        blocksInfo = Decompress(
            blocksInfo,
            header.BlocksInfoCompression,
            (int)header.UncompressedBlocksInfoSize,
            $"{fileName}+{archiveOffset} block table");

        var (blocks, nodes) = ParseBlocksInfo(blocksInfo);

        return new BundleArchive(
            archiveOffset,
            key.Mask,
            key.Inverted,
            header,
            blocks,
            nodes,
            archiveOffset + payloadOffset);
    }

    private static (List<BundleBlock> Blocks, List<BundleNode> Nodes) ParseBlocksInfo(byte[] blocksInfo)
    {
        var reader = new BigEndianReader(blocksInfo);
        reader.Skip(16); // uncompressed data hash

        var blockCount = reader.ReadInt32();
        var blocks = new List<BundleBlock>(blockCount);
        for (var i = 0; i < blockCount; i++)
        {
            blocks.Add(new BundleBlock(reader.ReadUInt32(), reader.ReadUInt32(), reader.ReadUInt16()));
        }

        var nodeCount = reader.ReadInt32();
        var nodes = new List<BundleNode>(nodeCount);
        for (var i = 0; i < nodeCount; i++)
        {
            var offset = reader.ReadInt64();
            var size = reader.ReadInt64();
            var flags = reader.ReadUInt32();
            nodes.Add(new BundleNode(reader.ReadCString(), offset, size, flags));
        }

        return (blocks, nodes);
    }

    private static byte[] Decompress(byte[] source, BundleCompression compression, int uncompressedSize, string what)
    {
        switch (compression)
        {
            case BundleCompression.None:
                return source;

            case BundleCompression.Lz4:
            case BundleCompression.Lz4Hc:
                var target = new byte[uncompressedSize];
                var written = LZ4Codec.Decode(source, target);
                if (written != uncompressedSize)
                {
                    throw new BundleFormatException(
                        $"{what} decompressed to {written} bytes, expected {uncompressedSize}");
                }

                return target;

            case BundleCompression.Lzma:
                throw new BundleFormatException($"{what} uses LZMA, which is not supported yet");

            default:
                throw new BundleFormatException($"{what} uses unknown compression {(int)compression}");
        }
    }
}

// Span reader for big-endian UnityFS data.
internal ref struct BigEndianReader(ReadOnlySpan<byte> buffer)
{
    private readonly ReadOnlySpan<byte> _buffer = buffer;

    public int Position { get; private set; }

    public void Skip(int count) => Position += count;

    public void AlignTo(int alignment)
    {
        var remainder = Position % alignment;
        if (remainder != 0)
        {
            Position += alignment - remainder;
        }
    }

    public ReadOnlySpan<byte> ReadBytes(int count)
    {
        var slice = _buffer.Slice(Position, count);
        Position += count;
        return slice;
    }

    public int ReadInt32() => BinaryPrimitives.ReadInt32BigEndian(ReadBytes(4));

    public uint ReadUInt32() => BinaryPrimitives.ReadUInt32BigEndian(ReadBytes(4));

    public long ReadInt64() => BinaryPrimitives.ReadInt64BigEndian(ReadBytes(8));

    public ushort ReadUInt16() => BinaryPrimitives.ReadUInt16BigEndian(ReadBytes(2));

    public string ReadCString()
    {
        var end = _buffer[Position..].IndexOf((byte)0);
        if (end < 0)
        {
            throw new BundleFormatException("unterminated string in bundle header");
        }

        var value = Encoding.UTF8.GetString(_buffer.Slice(Position, end));
        Position += end + 1;
        return value;
    }
}
