using System.Buffers.Binary;
using System.Text;

namespace LibraDex;

/// <summary>
/// Identifies the logical key family recorded in catalog metadata.<br/>
/// The family is public discovery metadata; physical profile IDs still select the concrete router and shelf implementation.<br/>
/// </summary>
public enum CatalogIndexKeyFamily
{
    /// <summary>
    /// The key family was not recorded or is not known to this runtime.<br/>
    /// </summary>
    Unknown = 0,

    /// <summary>
    /// The key is a fixed scalar value such as a numeric, char, or other compact sortable scalar.<br/>
    /// </summary>
    Scalar = 1,

    /// <summary>
    /// The key is a blob or byte-array value.<br/>
    /// </summary>
    Blob = 2,

    /// <summary>
    /// The key is a string value with string-specific projection options.<br/>
    /// </summary>
    String = 3,

    /// <summary>
    /// The key is a first-class GUID value.<br/>
    /// </summary>
    Guid = 4,

    /// <summary>
    /// The key is a first-class date/time value with optional structured date-part projections.<br/>
    /// </summary>
    Date = 5,

    /// <summary>
    /// The key is an ordered composite made from multiple typed parts.<br/>
    /// </summary>
    Composite = 6
}

/// <summary>
/// Identifies the logical identity family recorded in catalog metadata.<br/>
/// This lets programmatic callers validate runtime identity values without threading generic type parameters through every generated call path.<br/>
/// </summary>
public enum CatalogIndexIdentityFamily
{
    /// <summary>
    /// The identity family was not recorded or is not known to this runtime.<br/>
    /// </summary>
    Unknown = 0,

    /// <summary>
    /// The identity is a fixed scalar value such as a numeric, char, GUID, or other compact sortable scalar.<br/>
    /// </summary>
    Scalar = 1,

    /// <summary>
    /// The identity is a blob or byte-array value.<br/>
    /// </summary>
    Blob = 2,

    /// <summary>
    /// The identity is a string value.<br/>
    /// </summary>
    String = 3
}

internal readonly record struct CatalogIndexMetadata(
    string Group,
    string IndexName,
    string KeyTypeName,
    string IdentityTypeName,
    CatalogIndexKeyFamily KeyFamily,
    CatalogIndexIdentityFamily IdentityFamily,
    IndexKeys KeyContract,
    StringKeys StringKeys,
    GuidKeys GuidKeys,
    DateKeys DateKeys,
    LibraDexProjectionDirectionSet Directions,
    LibraDexIndexSortOrder SortOrder,
    IReadOnlyList<LibraDexIndexProjectionSpec> Projections,
    IReadOnlyList<LibraDexCompositeKeyPartSpec> CompositeParts,
    bool HasShapeMetadata);

internal static class CatalogIndexMetadataCodec
{
    private const uint Magic = 0x4D58444C; // LDXM
    private const ushort Version1 = 1;
    private const ushort Version2 = 2;
    private const int Version1HeaderSize = 20;
    private const int Version2HeaderSize = 28;
    private const int ProjectionSize = 6;

    internal static int GetEncodedSize(CatalogIndexMetadata metadata)
    {
        int size = Version2HeaderSize +
            GetUtf8ByteCount(metadata.Group) +
            GetUtf8ByteCount(metadata.IndexName) +
            GetUtf8ByteCount(metadata.KeyTypeName) +
            GetUtf8ByteCount(metadata.IdentityTypeName);
        size = checked(size + (metadata.Projections.Count * ProjectionSize));
        for (int i = 0; i < metadata.CompositeParts.Count; i++)
        {
            LibraDexCompositeKeyPartSpec part = metadata.CompositeParts[i];
            size = checked(size + 7 + GetUtf8ByteCount(part.Name) + GetUtf8ByteCount(GetStableTypeName(part.KeyType)));
        }

        return size;
    }

    internal static void Write(Span<byte> destination, CatalogIndexMetadata metadata)
    {
        int required = GetEncodedSize(metadata);
        if (destination.Length < required)
        {
            throw new ArgumentException("The destination span is too small for catalog index metadata.", nameof(destination));
        }

        BinaryPrimitives.WriteUInt32LittleEndian(destination[..4], Magic);
        BinaryPrimitives.WriteUInt16LittleEndian(destination.Slice(4, 2), Version2);
        BinaryPrimitives.WriteInt32LittleEndian(destination.Slice(6, 4), required);
        destination[10] = checked((byte)metadata.KeyFamily);
        destination[11] = checked((byte)metadata.IdentityFamily);
        BinaryPrimitives.WriteUInt16LittleEndian(destination.Slice(12, 2), checked((ushort)metadata.KeyContract));
        BinaryPrimitives.WriteUInt16LittleEndian(destination.Slice(14, 2), checked((ushort)metadata.StringKeys));
        BinaryPrimitives.WriteUInt16LittleEndian(destination.Slice(16, 2), checked((ushort)metadata.GuidKeys));
        BinaryPrimitives.WriteUInt16LittleEndian(destination.Slice(18, 2), checked((ushort)metadata.DateKeys));
        BinaryPrimitives.WriteUInt16LittleEndian(destination.Slice(20, 2), checked((ushort)metadata.Directions));
        BinaryPrimitives.WriteUInt16LittleEndian(destination.Slice(22, 2), checked((ushort)metadata.SortOrder));
        BinaryPrimitives.WriteUInt16LittleEndian(destination.Slice(24, 2), checked((ushort)metadata.Projections.Count));
        BinaryPrimitives.WriteUInt16LittleEndian(destination.Slice(26, 2), checked((ushort)metadata.CompositeParts.Count));

        int cursor = Version2HeaderSize;
        WriteString(destination, ref cursor, metadata.Group);
        WriteString(destination, ref cursor, metadata.IndexName);
        WriteString(destination, ref cursor, metadata.KeyTypeName);
        WriteString(destination, ref cursor, metadata.IdentityTypeName);
        for (int i = 0; i < metadata.Projections.Count; i++)
        {
            LibraDexIndexProjectionSpec projection = metadata.Projections[i];
            BinaryPrimitives.WriteUInt16LittleEndian(destination.Slice(cursor, 2), checked((ushort)projection.Kind));
            cursor += 2;
            BinaryPrimitives.WriteUInt16LittleEndian(destination.Slice(cursor, 2), checked((ushort)projection.Direction));
            cursor += 2;
            BinaryPrimitives.WriteUInt16LittleEndian(destination.Slice(cursor, 2), checked((ushort)projection.SortOrder));
            cursor += 2;
        }

        for (int i = 0; i < metadata.CompositeParts.Count; i++)
        {
            LibraDexCompositeKeyPartSpec part = metadata.CompositeParts[i];
            destination[cursor++] = checked((byte)part.KeyFamily);
            BinaryPrimitives.WriteUInt16LittleEndian(destination.Slice(cursor, 2), checked((ushort)part.StringKeys));
            cursor += 2;
            BinaryPrimitives.WriteUInt16LittleEndian(destination.Slice(cursor, 2), checked((ushort)part.GuidKeys));
            cursor += 2;
            BinaryPrimitives.WriteUInt16LittleEndian(destination.Slice(cursor, 2), checked((ushort)part.DateKeys));
            cursor += 2;
            WriteString(destination, ref cursor, part.Name);
            WriteString(destination, ref cursor, GetStableTypeName(part.KeyType));
        }
    }

    internal static bool TryRead(ReadOnlySpan<byte> source, out CatalogIndexMetadata metadata)
    {
        metadata = default;
        if (source.Length < Version1HeaderSize ||
            BinaryPrimitives.ReadUInt32LittleEndian(source[..4]) != Magic)
        {
            return false;
        }

        ushort version = BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(4, 2));
        if (version != Version1 && version != Version2)
        {
            return false;
        }

        int headerSize = version == Version2 ? Version2HeaderSize : Version1HeaderSize;
        int totalLength = BinaryPrimitives.ReadInt32LittleEndian(source.Slice(6, 4));
        if (totalLength < headerSize || source.Length < totalLength)
        {
            return false;
        }

        CatalogIndexKeyFamily keyFamily = (CatalogIndexKeyFamily)source[10];
        CatalogIndexIdentityFamily identityFamily = (CatalogIndexIdentityFamily)source[11];
        IndexKeys keyContract = (IndexKeys)BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(12, 2));
        StringKeys stringKeys = (StringKeys)BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(14, 2));
        GuidKeys guidKeys = (GuidKeys)BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(16, 2));
        DateKeys dateKeys = (DateKeys)BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(18, 2));
        LibraDexProjectionDirectionSet directions = LibraDexProjectionDirectionSet.Forward;
        LibraDexIndexSortOrder sortOrder = LibraDexIndexSortOrder.Ascending;
        int projectionCount = 0;
        int compositePartCount = 0;
        if (version == Version2)
        {
            directions = (LibraDexProjectionDirectionSet)BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(20, 2));
            sortOrder = (LibraDexIndexSortOrder)BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(22, 2));
            projectionCount = BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(24, 2));
            compositePartCount = BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(26, 2));
        }

        int cursor = headerSize;
        if (!TryReadString(source, totalLength, ref cursor, out string group) ||
            !TryReadString(source, totalLength, ref cursor, out string indexName) ||
            !TryReadString(source, totalLength, ref cursor, out string keyTypeName) ||
            !TryReadString(source, totalLength, ref cursor, out string identityTypeName))
        {
            return false;
        }

        LibraDexIndexProjectionSpec[] projections = Array.Empty<LibraDexIndexProjectionSpec>();
        LibraDexCompositeKeyPartSpec[] compositeParts = Array.Empty<LibraDexCompositeKeyPartSpec>();
        if (version == Version2)
        {
            projections = new LibraDexIndexProjectionSpec[projectionCount];
            for (int i = 0; i < projections.Length; i++)
            {
                if (cursor + ProjectionSize > totalLength)
                {
                    return false;
                }

                LibraDexIndexProjectionKind kind = (LibraDexIndexProjectionKind)BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(cursor, 2));
                cursor += 2;
                LibraDexIndexByteDirection direction = (LibraDexIndexByteDirection)BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(cursor, 2));
                cursor += 2;
                LibraDexIndexSortOrder projectionSortOrder = (LibraDexIndexSortOrder)BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(cursor, 2));
                cursor += 2;
                projections[i] = new LibraDexIndexProjectionSpec(kind, direction, projectionSortOrder);
            }

            compositeParts = new LibraDexCompositeKeyPartSpec[compositePartCount];
            for (int i = 0; i < compositeParts.Length; i++)
            {
                if (cursor + 7 > totalLength)
                {
                    return false;
                }

                CatalogIndexKeyFamily partFamily = (CatalogIndexKeyFamily)source[cursor++];
                StringKeys partStringKeys = (StringKeys)BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(cursor, 2));
                cursor += 2;
                GuidKeys partGuidKeys = (GuidKeys)BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(cursor, 2));
                cursor += 2;
                DateKeys partDateKeys = (DateKeys)BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(cursor, 2));
                cursor += 2;
                if (!TryReadString(source, totalLength, ref cursor, out string partName) ||
                    !TryReadString(source, totalLength, ref cursor, out string partTypeName))
                {
                    return false;
                }

                Type partType = Type.GetType(partTypeName, throwOnError: false) ?? typeof(object);
                compositeParts[i] = new LibraDexCompositeKeyPartSpec(
                    partName,
                    partType,
                    partFamily,
                    partStringKeys,
                    partGuidKeys,
                    partDateKeys);
            }
        }

        metadata = new CatalogIndexMetadata(
            group,
            indexName,
            keyTypeName,
            identityTypeName,
            keyFamily,
            identityFamily,
            keyContract,
            stringKeys,
            guidKeys,
            dateKeys,
            directions,
            sortOrder,
            projections,
            compositeParts,
            version == Version2);
        return true;
    }

    internal static bool TryReadLength(ReadOnlySpan<byte> header, out int length)
    {
        length = 0;
        if (header.Length < Version1HeaderSize ||
            BinaryPrimitives.ReadUInt32LittleEndian(header[..4]) != Magic)
        {
            return false;
        }

        ushort version = BinaryPrimitives.ReadUInt16LittleEndian(header.Slice(4, 2));
        if (version != Version1 && version != Version2)
        {
            return false;
        }

        length = BinaryPrimitives.ReadInt32LittleEndian(header.Slice(6, 4));
        int headerSize = version == Version2 ? Version2HeaderSize : Version1HeaderSize;
        return length >= headerSize;
    }

    private static string GetStableTypeName(Type type)
    {
        return type.AssemblyQualifiedName ?? type.FullName ?? type.Name;
    }

    private static int GetUtf8ByteCount(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        int byteCount = Encoding.UTF8.GetByteCount(value);
        return checked(sizeof(ushort) + byteCount);
    }

    private static void WriteString(Span<byte> destination, ref int cursor, string value)
    {
        int byteCount = Encoding.UTF8.GetByteCount(value);
        if (byteCount > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(value), value, "Catalog index metadata strings are limited to 65535 UTF-8 bytes.");
        }

        BinaryPrimitives.WriteUInt16LittleEndian(destination.Slice(cursor, 2), checked((ushort)byteCount));
        cursor += 2;
        cursor += Encoding.UTF8.GetBytes(value, destination[cursor..]);
    }

    private static bool TryReadString(ReadOnlySpan<byte> source, int totalLength, ref int cursor, out string value)
    {
        value = string.Empty;
        if (cursor + sizeof(ushort) > totalLength)
        {
            return false;
        }

        int byteCount = BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(cursor, 2));
        cursor += 2;
        if (cursor + byteCount > totalLength)
        {
            return false;
        }

        value = Encoding.UTF8.GetString(source.Slice(cursor, byteCount));
        cursor += byteCount;
        return true;
    }
}
