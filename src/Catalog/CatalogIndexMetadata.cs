using System.Buffers.Binary;
using System.Globalization;
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
    Composite = 6,

    /// <summary>
    /// The key is a first-class signed BigInteger value stored with a capped sortable byte encoding.<br/>
    /// </summary>
    BigInt = 7
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
    DateTimeKeyEncoding DateTimeKeyEncoding,
    LibraDexProjectionDirectionSet Directions,
    LibraDexIndexSortOrder SortOrder,
    IReadOnlyList<LibraDexIndexProjectionSpec> Projections,
    IReadOnlyList<LibraDexCompositeKeyPartSpec> CompositeParts,
    int VarKeyMaxKeyLength,
    int VarIdentityMaxLength,
    int ExactReversedProjectionSlotIndex,
    int FoldedProjectionSlotIndex,
    int SortKeyProjectionSlotIndex,
    int FoldedReversedProjectionSlotIndex,
    string FoldedCulture,
    string SortKeyCulture,
    LibraDexStringComparisonPolicyKind StringComparisonPolicyKind,
    CompareOptions StringComparisonCompareOptions,
    string StringComparisonCulture,
    string StringComparisonCustomComparerTypeName,
    bool HasShapeMetadata,
    long NullKeyRouteOffset = 0,
    long EmptyKeyRouteOffset = 0,
    IdentityKeyMultiplicity IdentityKeyMultiplicity = IdentityKeyMultiplicity.MultipleKeysPerIdentity,
    LibraDexTextNormalization FoldedNormalization = LibraDexTextNormalization.None,
    int NormalizedProjectionSlotIndex = -1,
    int NormalizedReversedProjectionSlotIndex = -1,
    IReadOnlyList<LibraDexStringSortKeyProjectionMetadata>? SortKeyProfiles = null);

internal readonly record struct KeyRouteOffsets(long Null, long Empty)
{
    public bool HasNull => Null > 0;

    public bool HasEmpty => Empty > 0;
}

internal static class CatalogIndexMetadataCodec
{
    private const uint Magic = 0x4D58444C; // LDXM
    private const ushort Version1 = 1;
    private const ushort Version2 = 2;
    private const ushort Version3 = 3;
    private const ushort Version4 = 4;
    private const ushort Version5 = 5;
    private const ushort Version6 = 6;
    private const ushort Version7 = 7;
    private const ushort Version8 = 8;
    private const ushort Version9 = 9;
    private const ushort Version10 = 10;
    private const ushort Version11 = 11;
    private const ushort Version12 = 12;
    private const int Version1HeaderSize = 20;
    private const int Version2HeaderSize = 28;
    private const int Version3HeaderSize = 44;
    private const int Version4HeaderSize = 50;
    private const int Version5HeaderSize = 54;
    private const int Version6HeaderSize = 58;
    private const int Version7HeaderSize = 60;
    private const int Version8HeaderSize = 76;
    private const int Version9HeaderSize = 78;
    private const int Version10HeaderSize = 88;
    private const int Version12HeaderSize = 90;
    private const int ProjectionSize = 6;
    private const int Version6CompositePartPrefixSize = 7;
    private const int Version7CompositePartPrefixSize = 9;
    private const int Version11CompositePartPrefixSize = 11;

    internal static int GetEncodedSize(CatalogIndexMetadata metadata)
    {
        IReadOnlyList<LibraDexStringSortKeyProjectionMetadata> sortKeyProfiles = metadata.SortKeyProfiles ?? Array.Empty<LibraDexStringSortKeyProjectionMetadata>();
        int size = Version12HeaderSize +
            GetUtf8ByteCount(metadata.Group) +
            GetUtf8ByteCount(metadata.IndexName) +
            GetUtf8ByteCount(metadata.KeyTypeName) +
            GetUtf8ByteCount(metadata.IdentityTypeName) +
            GetUtf8ByteCount(metadata.FoldedCulture) +
            GetUtf8ByteCount(metadata.SortKeyCulture) +
            GetUtf8ByteCount(metadata.StringComparisonCulture) +
            GetUtf8ByteCount(metadata.StringComparisonCustomComparerTypeName);
        size = checked(size + (metadata.Projections.Count * ProjectionSize));
        for (int i = 0; i < metadata.CompositeParts.Count; i++)
        {
            LibraDexCompositeKeyPartSpec part = metadata.CompositeParts[i];
            size = checked(size + Version11CompositePartPrefixSize + GetUtf8ByteCount(part.Name) + GetUtf8ByteCount(GetStableTypeName(part.KeyType)));
        }

        for (int i = 0; i < sortKeyProfiles.Count; i++)
        {
            size = checked(size + LibraDexStringSortKeyProjectionMetadata.FixedEncodedSize + GetUtf8ByteCount(sortKeyProfiles[i].CultureName));
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
        IReadOnlyList<LibraDexStringSortKeyProjectionMetadata> sortKeyProfiles = metadata.SortKeyProfiles ?? Array.Empty<LibraDexStringSortKeyProjectionMetadata>();
        BinaryPrimitives.WriteUInt16LittleEndian(destination.Slice(4, 2), Version12);
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
        BinaryPrimitives.WriteInt32LittleEndian(destination.Slice(28, 4), metadata.VarKeyMaxKeyLength);
        BinaryPrimitives.WriteInt32LittleEndian(destination.Slice(32, 4), metadata.FoldedProjectionSlotIndex);
        BinaryPrimitives.WriteInt32LittleEndian(destination.Slice(36, 4), metadata.SortKeyProjectionSlotIndex);
        BinaryPrimitives.WriteInt32LittleEndian(destination.Slice(40, 4), metadata.FoldedReversedProjectionSlotIndex);
        BinaryPrimitives.WriteUInt16LittleEndian(destination.Slice(44, 2), checked((ushort)metadata.StringComparisonPolicyKind));
        BinaryPrimitives.WriteUInt32LittleEndian(destination.Slice(46, 4), checked((uint)metadata.StringComparisonCompareOptions));
        BinaryPrimitives.WriteInt32LittleEndian(destination.Slice(50, 4), metadata.ExactReversedProjectionSlotIndex);
        BinaryPrimitives.WriteInt32LittleEndian(destination.Slice(54, 4), metadata.VarIdentityMaxLength);
        BinaryPrimitives.WriteUInt16LittleEndian(destination.Slice(58, 2), checked((ushort)metadata.DateTimeKeyEncoding));
        BinaryPrimitives.WriteInt64LittleEndian(destination.Slice(60, 8), metadata.NullKeyRouteOffset);
        BinaryPrimitives.WriteInt64LittleEndian(destination.Slice(68, 8), metadata.EmptyKeyRouteOffset);
        BinaryPrimitives.WriteUInt16LittleEndian(destination.Slice(76, 2), checked((ushort)metadata.IdentityKeyMultiplicity));
        BinaryPrimitives.WriteUInt16LittleEndian(destination.Slice(78, 2), checked((ushort)metadata.FoldedNormalization));
        BinaryPrimitives.WriteInt32LittleEndian(destination.Slice(80, 4), metadata.NormalizedProjectionSlotIndex);
        BinaryPrimitives.WriteInt32LittleEndian(destination.Slice(84, 4), metadata.NormalizedReversedProjectionSlotIndex);
        BinaryPrimitives.WriteUInt16LittleEndian(destination.Slice(88, 2), checked((ushort)sortKeyProfiles.Count));

        int cursor = Version12HeaderSize;
        WriteString(destination, ref cursor, metadata.Group);
        WriteString(destination, ref cursor, metadata.IndexName);
        WriteString(destination, ref cursor, metadata.KeyTypeName);
        WriteString(destination, ref cursor, metadata.IdentityTypeName);
        WriteString(destination, ref cursor, metadata.FoldedCulture);
        WriteString(destination, ref cursor, metadata.SortKeyCulture);
        WriteString(destination, ref cursor, metadata.StringComparisonCulture);
        WriteString(destination, ref cursor, metadata.StringComparisonCustomComparerTypeName);
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
            BinaryPrimitives.WriteUInt16LittleEndian(destination.Slice(cursor, 2), checked((ushort)part.DateTimeKeyEncoding));
            cursor += 2;
            BinaryPrimitives.WriteUInt16LittleEndian(destination.Slice(cursor, 2), checked((ushort)part.SortOrder));
            cursor += 2;
            WriteString(destination, ref cursor, part.Name);
            WriteString(destination, ref cursor, GetStableTypeName(part.KeyType));
        }

        for (int i = 0; i < sortKeyProfiles.Count; i++)
        {
            LibraDexStringSortKeyProjectionMetadata profile = sortKeyProfiles[i];
            BinaryPrimitives.WriteInt32LittleEndian(destination.Slice(cursor, 4), profile.SlotIndex);
            cursor += 4;
            BinaryPrimitives.WriteUInt32LittleEndian(destination.Slice(cursor, 4), checked((uint)profile.CompareOptions));
            cursor += 4;
            BinaryPrimitives.WriteInt32LittleEndian(destination.Slice(cursor, 4), profile.SortVersionFullVersion);
            cursor += 4;
            if (!profile.SortVersionId.TryWriteBytes(destination.Slice(cursor, 16)))
            {
                throw new InvalidOperationException("The catalog metadata buffer could not accept a string sort-version identifier.");
            }
            cursor += 16;
            WriteString(destination, ref cursor, profile.CultureName);
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
        if (version != Version1 && version != Version2 && version != Version3 && version != Version4 && version != Version5 && version != Version6 && version != Version7 && version != Version8 && version != Version9 && version != Version10 && version != Version11 && version != Version12)
        {
            return false;
        }

        int headerSize = version switch
        {
            Version12 => Version12HeaderSize,
            Version11 => Version10HeaderSize,
            Version10 => Version10HeaderSize,
            Version9 => Version9HeaderSize,
            Version8 => Version8HeaderSize,
            Version7 => Version7HeaderSize,
            Version6 => Version6HeaderSize,
            Version5 => Version5HeaderSize,
            Version4 => Version4HeaderSize,
            Version3 => Version3HeaderSize,
            Version2 => Version2HeaderSize,
            _ => Version1HeaderSize
        };
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
        DateTimeKeyEncoding dateTimeKeyEncoding = DateTimeKeyEncoding.CalendarSdt;
        LibraDexProjectionDirectionSet directions = LibraDexProjectionDirectionSet.Forward;
        LibraDexIndexSortOrder sortOrder = LibraDexIndexSortOrder.Ascending;
        int projectionCount = 0;
        int compositePartCount = 0;
        int varKeyMaxKeyLength = 0;
        int varIdentityMaxLength = 0;
        int exactReversedProjectionSlotIndex = -1;
        int foldedProjectionSlotIndex = -1;
        int sortKeyProjectionSlotIndex = -1;
        int foldedReversedProjectionSlotIndex = -1;
        LibraDexStringComparisonPolicyKind stringComparisonPolicyKind = LibraDexStringComparisonPolicyKind.Invariant;
        CompareOptions stringComparisonCompareOptions = CompareOptions.None;
        long nullKeyRouteOffset = 0;
        long emptyKeyRouteOffset = 0;
        IdentityKeyMultiplicity identityKeyMultiplicity = IdentityKeyMultiplicity.MultipleKeysPerIdentity;
        LibraDexTextNormalization foldedNormalization = LibraDexTextNormalization.None;
        int normalizedProjectionSlotIndex = -1;
        int normalizedReversedProjectionSlotIndex = -1;
        int sortKeyProfileCount = 0;
        if (version >= Version2)
        {
            directions = (LibraDexProjectionDirectionSet)BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(20, 2));
            sortOrder = (LibraDexIndexSortOrder)BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(22, 2));
            projectionCount = BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(24, 2));
            compositePartCount = BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(26, 2));
        }

        if (version >= Version3)
        {
            varKeyMaxKeyLength = BinaryPrimitives.ReadInt32LittleEndian(source.Slice(28, 4));
            foldedProjectionSlotIndex = BinaryPrimitives.ReadInt32LittleEndian(source.Slice(32, 4));
            sortKeyProjectionSlotIndex = BinaryPrimitives.ReadInt32LittleEndian(source.Slice(36, 4));
            foldedReversedProjectionSlotIndex = BinaryPrimitives.ReadInt32LittleEndian(source.Slice(40, 4));
        }

        if (version >= Version4)
        {
            stringComparisonPolicyKind = (LibraDexStringComparisonPolicyKind)BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(44, 2));
            stringComparisonCompareOptions = (CompareOptions)BinaryPrimitives.ReadUInt32LittleEndian(source.Slice(46, 4));
        }

        if (version >= Version5)
        {
            exactReversedProjectionSlotIndex = BinaryPrimitives.ReadInt32LittleEndian(source.Slice(50, 4));
        }

        if (version >= Version6)
        {
            varIdentityMaxLength = BinaryPrimitives.ReadInt32LittleEndian(source.Slice(54, 4));
        }

        if (version >= Version7)
        {
            dateTimeKeyEncoding = (DateTimeKeyEncoding)BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(58, 2));
        }

        if (version >= Version8)
        {
            nullKeyRouteOffset = BinaryPrimitives.ReadInt64LittleEndian(source.Slice(60, 8));
            emptyKeyRouteOffset = BinaryPrimitives.ReadInt64LittleEndian(source.Slice(68, 8));
        }

        if (version >= Version9)
        {
            identityKeyMultiplicity = (IdentityKeyMultiplicity)BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(76, 2));
        }

        if (version >= Version10)
        {
            foldedNormalization = (LibraDexTextNormalization)BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(78, 2));
            normalizedProjectionSlotIndex = BinaryPrimitives.ReadInt32LittleEndian(source.Slice(80, 4));
            normalizedReversedProjectionSlotIndex = BinaryPrimitives.ReadInt32LittleEndian(source.Slice(84, 4));
        }

        if (version >= Version12)
        {
            sortKeyProfileCount = BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(88, 2));
        }

        int cursor = headerSize;
        if (!TryReadString(source, totalLength, ref cursor, out string group) ||
            !TryReadString(source, totalLength, ref cursor, out string indexName) ||
            !TryReadString(source, totalLength, ref cursor, out string keyTypeName) ||
            !TryReadString(source, totalLength, ref cursor, out string identityTypeName))
        {
            return false;
        }

        string foldedCulture = string.Empty;
        string sortKeyCulture = string.Empty;
        string stringComparisonCulture = string.Empty;
        string stringComparisonCustomComparerTypeName = string.Empty;
        if (version >= Version3 &&
            (!TryReadString(source, totalLength, ref cursor, out foldedCulture) ||
            !TryReadString(source, totalLength, ref cursor, out sortKeyCulture)))
        {
            return false;
        }

        if (version >= Version4 &&
            (!TryReadString(source, totalLength, ref cursor, out stringComparisonCulture) ||
            !TryReadString(source, totalLength, ref cursor, out stringComparisonCustomComparerTypeName)))
        {
            return false;
        }

        LibraDexIndexProjectionSpec[] projections = Array.Empty<LibraDexIndexProjectionSpec>();
        LibraDexCompositeKeyPartSpec[] compositeParts = Array.Empty<LibraDexCompositeKeyPartSpec>();
        if (version >= Version2)
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
                int partPrefixSize = version >= Version11
                    ? Version11CompositePartPrefixSize
                    : version >= Version7
                        ? Version7CompositePartPrefixSize
                        : Version6CompositePartPrefixSize;
                if (cursor + partPrefixSize > totalLength)
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
                DateTimeKeyEncoding partDateTimeKeyEncoding = DateTimeKeyEncoding.CalendarSdt;
                if (version >= Version7)
                {
                    partDateTimeKeyEncoding = (DateTimeKeyEncoding)BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(cursor, 2));
                    cursor += 2;
                }
                LibraDexIndexSortOrder partSortOrder = LibraDexIndexSortOrder.Ascending;
                if (version >= Version11)
                {
                    partSortOrder = (LibraDexIndexSortOrder)BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(cursor, 2));
                    cursor += 2;
                }
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
                    partDateKeys,
                    partDateTimeKeyEncoding,
                    partSortOrder);
            }
        }


        LibraDexStringSortKeyProjectionMetadata[] sortKeyProfiles = Array.Empty<LibraDexStringSortKeyProjectionMetadata>();
        if (version >= Version12)
        {
            sortKeyProfiles = new LibraDexStringSortKeyProjectionMetadata[sortKeyProfileCount];
            for (int i = 0; i < sortKeyProfiles.Length; i++)
            {
                if (cursor + LibraDexStringSortKeyProjectionMetadata.FixedEncodedSize > totalLength)
                {
                    return false;
                }

                int slotIndex = BinaryPrimitives.ReadInt32LittleEndian(source.Slice(cursor, 4));
                cursor += 4;
                CompareOptions compareOptions = (CompareOptions)BinaryPrimitives.ReadUInt32LittleEndian(source.Slice(cursor, 4));
                cursor += 4;
                int sortVersionFullVersion = BinaryPrimitives.ReadInt32LittleEndian(source.Slice(cursor, 4));
                cursor += 4;
                Guid sortVersionId = new(source.Slice(cursor, 16));
                cursor += 16;
                if (!TryReadString(source, totalLength, ref cursor, out string cultureName))
                {
                    return false;
                }

                sortKeyProfiles[i] = new LibraDexStringSortKeyProjectionMetadata(
                    slotIndex,
                    cultureName,
                    compareOptions,
                    sortVersionFullVersion,
                    sortVersionId);
            }
        }
        else if (sortKeyProjectionSlotIndex >= 0)
        {
            sortKeyProfiles = new[]
            {
                new LibraDexStringSortKeyProjectionMetadata(
                    sortKeyProjectionSlotIndex,
                    sortKeyCulture,
                    CompareOptions.IgnoreCase,
                    0,
                    Guid.Empty)
            };
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
            dateTimeKeyEncoding,
            directions,
            sortOrder,
            projections,
            compositeParts,
            varKeyMaxKeyLength,
            varIdentityMaxLength,
            exactReversedProjectionSlotIndex,
            foldedProjectionSlotIndex,
            sortKeyProjectionSlotIndex,
            foldedReversedProjectionSlotIndex,
            foldedCulture,
            sortKeyCulture,
            stringComparisonPolicyKind,
            stringComparisonCompareOptions,
            stringComparisonCulture,
            stringComparisonCustomComparerTypeName,
            version >= Version2,
            nullKeyRouteOffset,
            emptyKeyRouteOffset,
            identityKeyMultiplicity,
            foldedNormalization,
            normalizedProjectionSlotIndex,
            normalizedReversedProjectionSlotIndex,
            sortKeyProfiles);
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
        if (version != Version1 && version != Version2 && version != Version3 && version != Version4 && version != Version5 && version != Version6 && version != Version7 && version != Version8 && version != Version9 && version != Version10 && version != Version11 && version != Version12)
        {
            return false;
        }

        length = BinaryPrimitives.ReadInt32LittleEndian(header.Slice(6, 4));
        int headerSize = version switch
        {
            Version12 => Version12HeaderSize,
            Version11 => Version10HeaderSize,
            Version10 => Version10HeaderSize,
            Version9 => Version9HeaderSize,
            Version8 => Version8HeaderSize,
            Version7 => Version7HeaderSize,
            Version6 => Version6HeaderSize,
            Version5 => Version5HeaderSize,
            Version4 => Version4HeaderSize,
            Version3 => Version3HeaderSize,
            Version2 => Version2HeaderSize,
            _ => Version1HeaderSize
        };
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

internal readonly record struct LibraDexStringSortKeyProjectionMetadata(
    int SlotIndex,
    string CultureName,
    CompareOptions CompareOptions,
    int SortVersionFullVersion,
    Guid SortVersionId)
{
    internal const int FixedEncodedSize = 28;
}
