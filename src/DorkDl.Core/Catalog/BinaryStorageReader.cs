using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;

namespace DorkDl.Core.Catalog;

// Reader for Addressables little-endian BinaryStorageBuffer (ported from com.unity.addressables 1.21.18).
// Key quirk: offsets point directly to item data; the uint32 byte length sits right before at (offset - 4).
// Offsets also act as tagged IDs where the top 2 bits are flags (masked with ClearFlagsMask).
public sealed class BinaryStorageReader(byte[] buffer)
{
    // UTF-16 flag (default is ASCII).
    private const uint UnicodeStringFlag = 0x80000000;

    // Dynamic string flag (stored as segmented chain joined by separator).
    private const uint DynamicStringFlag = 0x40000000;

    private const uint ClearFlagsMask = 0x3FFFFFFF;

    // Sentinel ID for null/absent.
    public const uint None = uint.MaxValue;

    // Sanity limit to avoid allocating huge strings on corrupt offsets.
    private const uint MaxStringLength = 8192;

    // Circuit breaker for dynamic string segment chains.
    private const int MaxDynamicSegments = 128;

    public byte[] Buffer { get; } = buffer;

    public int Length => Buffer.Length;

    public uint ReadUInt32(long offset)
    {
        if (offset < 0 || offset + 4 > Buffer.Length)
        {
            throw new CatalogFormatException($"offset {offset} is outside the {Buffer.Length}-byte catalog");
        }

        return BinaryPrimitives.ReadUInt32LittleEndian(Buffer.AsSpan((int)offset, 4));
    }

    // Reads a blittable struct from little-endian bytes.
    public T ReadValue<T>(uint offset) where T : unmanaged
    {
        var size = Marshal.SizeOf<T>();
        if (offset + size > Buffer.Length)
        {
            throw new CatalogFormatException($"struct at {offset} overruns the catalog");
        }

        return MemoryMarshal.Read<T>(Buffer.AsSpan((int)offset, size));
    }

    // Reads an array of blittable structs (element count derived from byte length at id - 4).
    public T[] ReadValueArray<T>(uint id) where T : unmanaged
    {
        if (id == None)
        {
            return [];
        }

        var byteLength = ReadUInt32(id - 4);
        if (id + byteLength > Buffer.Length)
        {
            throw new CatalogFormatException($"array at {id} claims {byteLength} bytes, which overruns the catalog");
        }

        var stride = Marshal.SizeOf<T>();
        var count = (int)(byteLength / stride);
        var result = new T[count];
        MemoryMarshal.Cast<byte, T>(Buffer.AsSpan((int)id, count * stride)).CopyTo(result);
        return result;
    }

    // Reads an array of 4-byte object IDs.
    public uint[] ReadIdArray(uint id) => id == None ? [] : ReadValueArray<uint>(id);

    // Reads ASCII/UTF-16 string or resolves dynamic segment chain if separator is provided.
    public string? ReadString(uint id, char separator = '\0')
    {
        if (id == None)
        {
            return null;
        }

        if (separator != '\0' && (id & DynamicStringFlag) == DynamicStringFlag)
        {
            var parts = new List<string>();
            var next = id;
            for (var i = 0; next != None && i < MaxDynamicSegments; i++)
            {
                var segment = ReadValue<DynamicString>(next & ClearFlagsMask);
                parts.Add(ReadString(segment.StringId) ?? string.Empty);
                next = segment.NextId;
            }

            return string.Join(separator, parts);
        }

        var unicode = (id & UnicodeStringFlag) == UnicodeStringFlag;
        var offset = unicode ? id & ClearFlagsMask : id;
        var byteLength = ReadUInt32(offset - 4);
        if (byteLength > MaxStringLength || offset + byteLength > Buffer.Length)
        {
            throw new CatalogFormatException($"string at {offset} claims an implausible {byteLength} bytes");
        }

        var span = Buffer.AsSpan((int)offset, (int)byteLength);
        return unicode ? Encoding.Unicode.GetString(span) : Encoding.ASCII.GetString(span);
    }

    // Reads 16-byte Hash128 as lowercase hex.
    public string ReadHash128(uint offset)
    {
        if (offset == None || offset + 16 > Buffer.Length)
        {
            return string.Empty;
        }

        return Convert.ToHexString(Buffer.AsSpan((int)offset, 16)).ToLowerInvariant();
    }

    // Resolves type reference to class name (stored as dotted dynamic strings).
    public string? ReadTypeName(uint id)
    {
        if (id == None)
        {
            return null;
        }

        var data = ReadValue<TypeData>(id);
        return ReadString(data.ClassId, '.');
    }

    // Reads boxed object reference: (typeId, objectId) pair.
    public ObjectTypeData ReadObjectRef(uint id) => ReadValue<ObjectTypeData>(id);

    // Reads boxed System.String object (extra indirection hop to string ID).
    public string? ReadStringObject(uint id, char separator = '\0')
    {
        if (id == None)
        {
            return null;
        }

        var reference = ReadObjectRef(id);
        return reference.ObjectId == None ? null : ReadString(ReadUInt32(reference.ObjectId), separator);
    }
}

[StructLayout(LayoutKind.Sequential)]
public struct DynamicString
{
    public uint StringId;
    public uint NextId;
}

[StructLayout(LayoutKind.Sequential)]
public struct TypeData
{
    public uint AssemblyId;
    public uint ClassId;
}

[StructLayout(LayoutKind.Sequential)]
public struct ObjectTypeData
{
    public uint TypeId;
    public uint ObjectId;
}

// Thrown when the catalog format doesn't match expected binary layout.
public sealed class CatalogFormatException(string message) : Exception(message);
