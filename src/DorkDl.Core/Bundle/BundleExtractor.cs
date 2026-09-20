using K4os.Compression.LZ4;

namespace DorkDl.Core.Bundle;

// Decompresses payload blocks and extracts files.
// Note: ObfuscatedWindow is archive-relative, so block offsets must be converted before decrypting.
public static class BundleExtractor
{
    // Decompresses an entire archive payload into memory.
    public static byte[] ReadPayload(Stream stream, BundleArchive archive) =>
        ReadPayload(stream, archive, long.MaxValue);

    // Decompresses payload up to `needed` bytes. Useful to grab just the CAB header and skip trailing .resS data. Allocates full buffer so offsets stay valid.
    public static byte[] ReadPayload(Stream stream, BundleArchive archive, long needed)
    {
        var total = archive.UncompressedPayloadSize;
        if (total > Array.MaxLength)
        {
            throw new BundleFormatException(
                $"payload of {total} bytes is too large to materialise in one buffer");
        }

        var key = BundleKey.FromMask(archive.Mask, archive.Inverted);
        var payload = new byte[total];
        var written = 0;
        var offset = archive.PayloadOffset;

        foreach (var block in archive.Blocks)
        {
            var chunk = new byte[block.CompressedSize];
            stream.Seek(offset, SeekOrigin.Begin);
            stream.ReadExactly(chunk);

            // Window offset is archive-relative; PayloadOffset is absolute within the file.
            key.Decrypt(chunk, offset - archive.ArchiveOffset);

            switch (block.Compression)
            {
                case BundleCompression.None:
                    chunk.CopyTo(payload, written);
                    written += chunk.Length;
                    break;

                case BundleCompression.Lz4:
                case BundleCompression.Lz4Hc:
                    var produced = LZ4Codec.Decode(
                        chunk, 0, chunk.Length,
                        payload, written, (int)block.UncompressedSize);
                    if (produced != block.UncompressedSize)
                    {
                        throw new BundleFormatException(
                            $"block decompressed to {produced} bytes, expected {block.UncompressedSize}");
                    }

                    written += produced;
                    break;

                default:
                    throw new BundleFormatException($"unsupported block compression {block.Compression}");
            }

            offset += block.CompressedSize;

            if (written >= needed)
            {
                break;
            }
        }

        return payload;
    }

    // Streams out clean UnityFS data (deobfuscates the 32 KB window per archive, passes the rest through).
    public static void WritePlain(Stream source, BundleFile bundle, Stream destination)
    {
        var buffer = new byte[64 * 1024];
        var position = 0L;

        // Archives are sequential, single forward cursor is fine.
        var index = 0;
        var archives = bundle.Archives;

        int read;
        while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
        {
            var chunk = buffer.AsSpan(0, read);

            while (index < archives.Count
                   && archives[index].ArchiveOffset + BundleObfuscation.ObfuscatedWindow <= position)
            {
                index++;
            }

            for (var i = index; i < archives.Count; i++)
            {
                var archive = archives[i];
                var windowStart = archive.ArchiveOffset;
                var windowEnd = windowStart + BundleObfuscation.ObfuscatedWindow;
                if (windowStart >= position + read)
                {
                    break;
                }

                var from = (int)Math.Max(0, windowStart - position);
                var to = (int)Math.Min(read, windowEnd - position);
                if (to <= from)
                {
                    continue;
                }

                var key = BundleKey.FromMask(archive.Mask, archive.Inverted);
                key.Decrypt(chunk[from..to], position + from - windowStart);
            }

            destination.Write(chunk);
            position += read;
        }
    }

    // MD5 of the deobfuscated bundle, matching what GFF indexes record without needing decompression.
    public static byte[] ComputePlainHash(string bundlePath)
    {
        var bundle = BundleReader.Read(bundlePath);
        using var source = File.OpenRead(bundlePath);
        using var md5 = System.Security.Cryptography.MD5.Create();
        using (var sink = new System.Security.Cryptography.CryptoStream(
                   Stream.Null, md5, System.Security.Cryptography.CryptoStreamMode.Write))
        {
            WritePlain(source, bundle, sink);
        }

        return md5.Hash ?? throw new InvalidOperationException("MD5 produced no hash");
    }

    // Extracts all files from all archives in the bundle.
    public static IReadOnlyList<ExtractedFile> ExtractAll(string bundlePath, string outputDirectory)
    {
        var bundle = BundleReader.Read(bundlePath);
        var written = new List<ExtractedFile>();

        using var stream = File.OpenRead(bundlePath);
        foreach (var archive in bundle.Archives)
        {
            var payload = ReadPayload(stream, archive);
            foreach (var node in archive.Nodes)
            {
                if (node.Offset < 0 || node.Offset + node.Size > payload.Length)
                {
                    throw new BundleFormatException(
                        $"{node.Path} claims bytes {node.Offset}..{node.Offset + node.Size} "
                        + $"of a {payload.Length}-byte payload");
                }

                // Prevent directory traversal from bundle paths.
                var safeName = SafeFileName(node.Path);
                var target = Path.Combine(outputDirectory, safeName);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                using (var output = File.Create(target))
                {
                    output.Write(payload, (int)node.Offset, (int)node.Size);
                }

                written.Add(new ExtractedFile(node.Path, target, node.Size));
            }
        }

        return written;
    }

    private static string SafeFileName(string path)
    {
        var cleaned = path.Replace('\\', '/');
        var parts = cleaned.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Where(p => p != "." && p != "..")
            .Select(p => string.Concat(p.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)));
        var safe = string.Join(Path.DirectorySeparatorChar, parts);
        return string.IsNullOrEmpty(safe) ? "unnamed" : safe;
    }
}

public sealed record ExtractedFile(string Name, string Path, long Size);
