using K4os.Compression.LZ4;

namespace DorkDl.Core.Bundle;

// Decompresses payload blocks and extracts files.
// Window offset is archive-relative (restarts at each archive in multi-archive bundles).
public static class BundleExtractor
{
    // Decompresses the whole payload of one archive into memory.
    public static byte[] ReadPayload(Stream stream, BundleArchive archive) =>
        ReadPayload(stream, archive, long.MaxValue);

    // Decompresses payload up to `needed` bytes (stops after reading header files, skipping trailing .resS).
    // Buffer is sized to blocks actually decompressed so memory isn't blown on 300 MB allocations for tiny headers.
    public static byte[] ReadPayload(Stream stream, BundleArchive archive, long needed)
    {
        var total = BytesToDecompress(archive, needed);
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

    // Total payload bytes across blocks covering up to `needed` bytes.
    public static long BytesToDecompress(BundleArchive archive, long needed)
    {
        var total = 0L;
        foreach (var block in archive.Blocks)
        {
            total += block.UncompressedSize;
            if (total >= needed)
            {
                break;
            }
        }

        return total;
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

    // MD5 of each archive on its own, obfuscation undone.
    public static IReadOnlyList<byte[]> ComputeArchivePlainHashes(string bundlePath, BundleFile bundle)
    {
        var hashes = new List<byte[]>(bundle.Archives.Count);
        var buffer = new byte[64 * 1024];

        using var source = File.OpenRead(bundlePath);
        foreach (var archive in bundle.Archives)
        {
            var key = BundleKey.FromMask(archive.Mask, archive.Inverted);
            using var md5 = System.Security.Cryptography.MD5.Create();

            source.Seek(archive.ArchiveOffset, SeekOrigin.Begin);
            var remaining = archive.ArchiveSize;
            var position = 0L;
            while (remaining > 0)
            {
                var read = source.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                if (read == 0)
                {
                    throw new BundleFormatException($"{bundle.FileName} ends inside an archive");
                }

                var chunk = buffer.AsSpan(0, read);
                var window = BundleObfuscation.ObfuscatedWindow - position;
                if (window > 0)
                {
                    key.Decrypt(chunk[..(int)Math.Min(read, window)], position);
                }

                md5.TransformBlock(buffer, 0, read, null, 0);
                position += read;
                remaining -= read;
            }

            md5.TransformFinalBlock([], 0, 0);
            hashes.Add(md5.Hash ?? throw new InvalidOperationException("MD5 produced no hash"));
        }

        return hashes;
    }

    // Extracts all files from all archives in a bundle to outputDirectory.
    public static IReadOnlyList<ExtractedFile> ExtractAll(string bundlePath, string outputDirectory) =>
        ExtractAll(bundlePath, BundleReader.Read(bundlePath), outputDirectory);

    // Extracts a bundle whose header has already been read.
    public static IReadOnlyList<ExtractedFile> ExtractAll(string bundlePath, BundleFile bundle, string outputDirectory) =>
        ExtractArchives(bundlePath, bundle, outputDirectory, Enumerable.Range(0, bundle.Archives.Count));

    // Extracts only the given archives of a file.
    public static IReadOnlyList<ExtractedFile> ExtractArchives(
        string bundlePath,
        BundleFile bundle,
        string outputDirectory,
        IEnumerable<int> archiveIndices)
    {
        var written = new List<ExtractedFile>();

        using var stream = File.OpenRead(bundlePath);
        foreach (var index in archiveIndices)
        {
            if (index < 0 || index >= bundle.Archives.Count)
            {
                throw new BundleFormatException($"{bundle.FileName} has no archive {index}");
            }

            var archive = bundle.Archives[index];
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
