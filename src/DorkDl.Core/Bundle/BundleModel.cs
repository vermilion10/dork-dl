namespace DorkDl.Core.Bundle;

// Compression type for blocks and block tables (low 6 bits of flags).
public enum BundleCompression
{
    None = 0,
    Lzma = 1,
    Lz4 = 2,
    Lz4Hc = 3,
}

// Decrypted UnityFS header.
public sealed record BundleHeader(
    int FormatVersion,
    string UnityVersion,
    string UnityRevision,
    long Size,
    uint CompressedBlocksInfoSize,
    uint UncompressedBlocksInfoSize,
    uint Flags)
{
    public BundleCompression BlocksInfoCompression => (BundleCompression)(Flags & 0x3F);

    // Block table stored at archive end rather than right after header.
    public bool BlocksInfoAtEnd => (Flags & 0x80) != 0;
}

// Single compressed payload chunk.
public sealed record BundleBlock(uint UncompressedSize, uint CompressedSize, ushort Flags)
{
    public BundleCompression Compression => (BundleCompression)(Flags & 0x3F);
}

// File inside payload: usually CAB-<hash> serialized asset and an optional matching .resS.
public sealed record BundleNode(string Path, long Offset, long Size, uint Flags);

// Single UnityFS archive. Bundles can chain multiple archives, each tracking its own start offset and key phase relative to ArchiveOffset.
public sealed record BundleArchive(
    long ArchiveOffset,
    uint Mask,
    bool Inverted,
    BundleHeader Header,
    IReadOnlyList<BundleBlock> Blocks,
    IReadOnlyList<BundleNode> Nodes,
    long PayloadOffset)
{
    // Size reported by this archive's header.
    public long ArchiveSize => Header.Size;

    // Total uncompressed payload across all blocks.
    public long UncompressedPayloadSize => Blocks.Sum(b => (long)b.UncompressedSize);
}

// Entire .bundle file containing one or more concatenated archives.
public sealed record BundleFile(
    string FileName,
    long FileSize,
    IReadOnlyList<BundleArchive> Archives,
    long BytesAccountedFor,
    string? TrailingDataReason)
{
    public bool IsMultiArchive => Archives.Count > 1;

    // All files across all archives in this bundle.
    public IEnumerable<BundleNode> Nodes => Archives.SelectMany(a => a.Nodes);

    public long UncompressedPayloadSize => Archives.Sum(a => a.UncompressedPayloadSize);

    // Quick check: did the archive chain cleanly span the exact file size?
    public bool FullyParsed => BytesAccountedFor == FileSize;
}

// Thrown when an obfuscated bundle can't be parsed.
public sealed class BundleFormatException(string message) : Exception(message);
