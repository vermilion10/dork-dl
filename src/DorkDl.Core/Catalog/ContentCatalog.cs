using System.Runtime.InteropServices;

namespace DorkDl.Core.Catalog;

// Addressables binary catalog (catalog_main_<n>.bin). Maps keys (dashed GUIDs and bundle names) to locations with download metadata (hash, CRC, size).
public sealed class ContentCatalog
{
    // Unity's hash of nameof(ContentCatalogData).
    private const int ExpectedMagic = 0x0DE38942;

    private const int ExpectedVersion = 1;

    private readonly BinaryStorageReader _reader;
    private readonly KeyData[] _keys;

    private ContentCatalog(BinaryStorageReader reader, Header header, KeyData[] keys)
    {
        _reader = reader;
        _keys = keys;
        LocatorId = reader.ReadString(header.IdOffset) ?? string.Empty;
        BuildResultHash = header.BuildResultHash;
    }

    // Usually "AddressablesMainContentCatalog".
    public string LocatorId { get; }

    public uint BuildResultHash { get; }

    public int KeyCount => _keys.Length;

    public static ContentCatalog Load(string path) => Load(File.ReadAllBytes(path));

    public static ContentCatalog Load(byte[] bytes)
    {
        var reader = new BinaryStorageReader(bytes);
        var header = reader.ReadValue<Header>(0);

        if (header.Magic != ExpectedMagic)
        {
            throw new CatalogFormatException(
                $"expected catalog magic 0x{ExpectedMagic:x8} but found 0x{header.Magic:x8}");
        }

        if (header.Version != ExpectedVersion)
        {
            throw new CatalogFormatException(
                $"expected catalog version {ExpectedVersion} but found {header.Version}");
        }

        return new ContentCatalog(reader, header, reader.ReadValueArray<KeyData>(header.KeysOffset));
    }

    // Yields all catalog keys lazily; locations are resolved on demand.
    public IEnumerable<CatalogKey> Keys()
    {
        foreach (var key in _keys)
        {
            var name = _reader.ReadStringObject(key.KeyNameOffset, '/');
            if (name is not null)
            {
                yield return new CatalogKey(name, key.LocationSetOffset);
            }
        }
    }

    // Resolves locations for a given key.
    public IReadOnlyList<CatalogLocation> Locations(CatalogKey key) => Locations(key.LocationSetOffset);

    public IReadOnlyList<CatalogLocation> Locations(uint locationSetOffset)
    {
        if (locationSetOffset == BinaryStorageReader.None)
        {
            return [];
        }

        var ids = _reader.ReadIdArray(locationSetOffset);
        var locations = new List<CatalogLocation>(ids.Length);
        foreach (var id in ids)
        {
            locations.Add(ReadLocation(id));
        }

        return locations;
    }

    private CatalogLocation ReadLocation(uint offset)
    {
        var data = _reader.ReadValue<LocationData>(offset);

        AssetBundleRequestOptions? options = null;
        string? extraTypeName = null;
        if (data.ExtraDataOffset != BinaryStorageReader.None)
        {
            var extra = _reader.ReadObjectRef(data.ExtraDataOffset);
            extraTypeName = _reader.ReadTypeName(extra.TypeId);
            if (extraTypeName is not null && extraTypeName.EndsWith("AssetBundleRequestOptions", StringComparison.Ordinal))
            {
                var serialized = _reader.ReadValue<BundleOptionsData>(extra.ObjectId);
                options = new AssetBundleRequestOptions(
                    Hash: _reader.ReadHash128(serialized.HashId),
                    // Bundle names are dynamic strings split on '_' so Unity can reuse shared prefixes.
                    BundleName: _reader.ReadString(serialized.BundleNameId, '_'),
                    Crc: serialized.Crc,
                    BundleSize: serialized.BundleSize);
            }
        }

        return new CatalogLocation(
            PrimaryKey: _reader.ReadString(data.PrimaryKeyOffset, '/'),
            InternalId: _reader.ReadString(data.InternalIdOffset, '/'),
            Provider: _reader.ReadString(data.ProviderOffset, '.'),
            ResourceType: _reader.ReadTypeName(data.TypeId),
            ExtraDataType: extraTypeName,
            DependencySetOffset: data.DependencySetOffset,
            BundleOptions: options);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Header
    {
        public int Magic;
        public int Version;
        public uint KeysOffset;
        public uint IdOffset;
        public uint InstanceProvider;
        public uint SceneProvider;
        public uint InitObjectsArray;
        public uint BuildResultHash;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyData
    {
        public uint KeyNameOffset;
        public uint LocationSetOffset;
    }

    // ResourceLocation.Serializer.Data. DependencyHashValue is always zero but must be modeled to avoid offset shifts.
    [StructLayout(LayoutKind.Sequential)]
    private struct LocationData
    {
        public uint PrimaryKeyOffset;
        public uint InternalIdOffset;
        public uint ProviderOffset;
        public uint DependencySetOffset;
        public int DependencyHashValue;
        public uint ExtraDataOffset;
        public uint TypeId;
    }

    // AssetBundleRequestOptions.SerializedData.
    [StructLayout(LayoutKind.Sequential)]
    private struct BundleOptionsData
    {
        public uint HashId;
        public uint BundleNameId;
        public uint Crc;
        public uint BundleSize;
        public uint CommonId;
    }
}

// Catalog key and its location set offset.
public sealed record CatalogKey(string Name, uint LocationSetOffset)
{
    // True for keys that name a downloadable .bundle file instead of an asset GUID.
    public bool IsBundleKey => Name.EndsWith(".bundle", StringComparison.OrdinalIgnoreCase);
}

// Resolved location for an asset key.
public sealed record CatalogLocation(
    string? PrimaryKey,
    string? InternalId,
    string? Provider,
    string? ResourceType,
    string? ExtraDataType,
    uint DependencySetOffset,
    AssetBundleRequestOptions? BundleOptions);

// Download metadata (hash, CRC, size) for a bundle location.
public sealed record AssetBundleRequestOptions(string Hash, string? BundleName, uint Crc, uint BundleSize);
