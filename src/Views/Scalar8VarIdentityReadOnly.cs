using LibraDex.Layouts;

namespace LibraDex.Views;

internal sealed class Scalar8VarIdentityReadOnly : IVarIdentityReadOnlyShelf
{
    private readonly ReadOnlyMemory<byte> bytes;
    private readonly byte[]? ownedBytes;
    private readonly Scalar8VarIdentityProfile profile;
    private readonly bool descending;
    private readonly uint[] recordOffsets;
    private readonly uint[] keyPrefixes;

    public Scalar8VarIdentityReadOnly(ReadOnlyMemory<byte> bytes, Scalar8VarIdentityProfile profile)
        : this(bytes, profile, validateRecords: true)
    {
    }

    /// <summary>
    /// Creates an immutable `SV8` shelf view that retains explicit ownership of the supplied byte array.<br/>
    /// Session read caching uses this overload so retained shelf bytes and decoded sidecars remain one visible ownership unit.<br/>
    /// </summary>
    /// <param name="bytes">The complete immutable shelf byte array retained by the view.<br/></param>
    /// <param name="profile">The `SV8` shelf profile used to validate and decode slot metadata.<br/></param>
    internal Scalar8VarIdentityReadOnly(byte[] bytes, Scalar8VarIdentityProfile profile)
        : this(bytes, profile, validateRecords: true)
    {
    }

    internal Scalar8VarIdentityReadOnly(byte[] bytes, Scalar8VarIdentityProfile profile, bool validateRecords)
    {
        this.bytes = bytes;
        ownedBytes = bytes;
        this.profile = profile;
        descending = bytes.Length >= Scalar8VarIdentityLayout.HeaderSize && (Scalar8VarIdentityLayout.ReadFlags(bytes) & 1U) != 0;
        IsValid = TryDecodeSlots(bytes, profile, validateRecords, out recordOffsets, out keyPrefixes);
    }

    internal Scalar8VarIdentityReadOnly(ReadOnlyMemory<byte> bytes, Scalar8VarIdentityProfile profile, bool validateRecords)
    {
        this.bytes = bytes;
        this.profile = profile;
        descending = bytes.Length >= Scalar8VarIdentityLayout.HeaderSize && (Scalar8VarIdentityLayout.ReadFlags(bytes.Span) & 1U) != 0;
        IsValid = TryDecodeSlots(bytes.Span, profile, validateRecords, out recordOffsets, out keyPrefixes);
    }

    public bool IsValid { get; }

    internal bool IsDescending => descending;

    public int ItemCount => IsValid ? recordOffsets.Length : 0;

    public int PhysicalItemCount => ItemCount;

    public int LiveItemCount => ItemCount;

    public int DeletedItemCount => 0;

    internal byte[] Bytes => ownedBytes ?? throw new InvalidOperationException("This SV8 read-only view does not own a cacheable shelf byte array.");

    internal long RetainedSidecarBytes => checked((long)recordOffsets.Length * sizeof(uint) + (long)keyPrefixes.Length * sizeof(uint));

    byte[] IVarIdentityReadOnlyShelf.Bytes => Bytes;

    long IVarIdentityReadOnlyShelf.RetainedSidecarBytes => RetainedSidecarBytes;

    internal long NextShelfOffset => Scalar8VarIdentityLayout.ReadNextShelfOffset(bytes.Span);

    public int LowerBoundKey(ulong encodedKey)
    {
        uint prefix = Scalar8VarIdentityLayout.CreateKeyPrefix(encodedKey);
        int low = 0;
        int high = ItemCount;
        ReadOnlySpan<byte> localBytes = bytes.Span;
        while (low < high)
        {
            int middle = low + ((high - low) >> 1);
            int comparison = CompareSlotKey(localBytes, middle, prefix, encodedKey);
            if (comparison < 0)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        return low;
    }

    public int LowerBound(ulong encodedKey, ReadOnlySpan<byte> identity)
    {
        uint prefix = Scalar8VarIdentityLayout.CreateKeyPrefix(encodedKey);
        int low = 0;
        int high = ItemCount;
        ReadOnlySpan<byte> localBytes = bytes.Span;
        while (low < high)
        {
            int middle = low + ((high - low) >> 1);
            int comparison = CompareSlotTuple(localBytes, middle, prefix, encodedKey, identity);
            if (comparison < 0)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        return low;
    }

    public ulong ReadKeyAt(int slotIndex)
    {
        return Scalar8VarIdentityLayout.ReadKey(bytes.Span, checked((int)recordOffsets[slotIndex]));
    }

    public ReadOnlySpan<byte> ReadIdentityAt(int slotIndex)
    {
        return Scalar8VarIdentityLayout.ReadIdentity(bytes.Span, checked((int)recordOffsets[slotIndex]));
    }

    public bool Contains(ulong encodedKey, ReadOnlySpan<byte> identity)
    {
        int slotIndex = LowerBound(encodedKey, identity);
        return slotIndex < ItemCount &&
            CompareSlotTuple(bytes.Span, slotIndex, Scalar8VarIdentityLayout.CreateKeyPrefix(encodedKey), encodedKey, identity) == 0;
    }

    internal uint ReadRecordOffsetAt(int slotIndex)
    {
        return recordOffsets[slotIndex];
    }

    internal static bool TryDecodeSlots(ReadOnlySpan<byte> bytes, Scalar8VarIdentityProfile profile, out uint[] recordOffsets, out uint[] keyPrefixes, out ulong[] keys)
    {
        recordOffsets = [];
        keyPrefixes = [];
        keys = [];
        if (!TryDecodeSlots(bytes, profile, validateRecords: true, out recordOffsets, out keyPrefixes))
        {
            return false;
        }

        keys = new ulong[recordOffsets.Length];
        for (int i = 0; i < keys.Length; i++)
        {
            keys[i] = Scalar8VarIdentityLayout.ReadKey(bytes, checked((int)recordOffsets[i]));
        }

        return true;
    }

    internal static bool TryDecodeSlots(ReadOnlySpan<byte> bytes, Scalar8VarIdentityProfile profile, out uint[] recordOffsets, out uint[] keyPrefixes)
    {
        return TryDecodeSlots(bytes, profile, validateRecords: true, out recordOffsets, out keyPrefixes);
    }

    internal static bool TryDecodeSlots(ReadOnlySpan<byte> bytes, Scalar8VarIdentityProfile profile, bool validateRecords, out uint[] recordOffsets, out uint[] keyPrefixes)
    {
        recordOffsets = [];
        keyPrefixes = [];
        if (bytes.Length < profile.ShelfExtentSize ||
            Scalar8VarIdentityLayout.ReadMagic(bytes) != Scalar8VarIdentityLayout.Magic ||
            Scalar8VarIdentityLayout.ReadFormatVersion(bytes) != Scalar8VarIdentityLayout.FormatVersion ||
            Scalar8VarIdentityLayout.ReadHeaderSize(bytes) != Scalar8VarIdentityLayout.HeaderSize ||
            Scalar8VarIdentityLayout.ReadShelfExtentSize(bytes) != profile.ShelfExtentSize)
        {
            return false;
        }

        int count = Scalar8VarIdentityLayout.ReadItemCount(bytes);
        int slotStreamLength = Scalar8VarIdentityLayout.ReadSlotStreamLength(bytes);
        int slotCapacityBytes = Scalar8VarIdentityLayout.ReadSlotCapacityBytes(bytes);
        int recordArenaEnd = Scalar8VarIdentityLayout.ReadRecordArenaEnd(bytes);
        int expectedSlotStreamLength = checked(count * Scalar8VarIdentityLayout.SlotSize);
        int expectedSlotCapacityBytes = Scalar8VarIdentityLayout.CalculateSlotCapacityBytes(profile.ShelfExtentSize);
        int recordArenaStart = Scalar8VarIdentityLayout.HeaderSize + slotCapacityBytes;
        if (count < 0 ||
            slotStreamLength != expectedSlotStreamLength ||
            slotStreamLength > slotCapacityBytes ||
            slotCapacityBytes != expectedSlotCapacityBytes ||
            recordArenaEnd < recordArenaStart ||
            recordArenaEnd > profile.ShelfExtentSize)
        {
            return false;
        }

        uint[] offsets = new uint[count];
        uint[] prefixes = new uint[count];
        int cursor = Scalar8VarIdentityLayout.HeaderSize;
        for (int i = 0; i < count; i++)
        {
            int offset = Scalar8VarIdentityLayout.ReadSlotRecordOffset(bytes, cursor);
            if (offset < recordArenaStart || offset >= recordArenaEnd)
            {
                return false;
            }

            if (validateRecords)
            {
                int recordLength = Scalar8VarIdentityLayout.GetRecordLength(bytes, offset);
                if (recordLength <= 0 || offset + recordLength > recordArenaEnd)
                {
                    return false;
                }
            }

            offsets[i] = checked((uint)offset);
            prefixes[i] = Scalar8VarIdentityLayout.ReadSlotKeyPrefix(bytes, cursor);
            cursor += Scalar8VarIdentityLayout.SlotSize;
        }

        recordOffsets = offsets;
        keyPrefixes = prefixes;
        return true;
    }

    private int CompareSlotKey(ReadOnlySpan<byte> localBytes, int slotIndex, uint prefix, ulong encodedKey)
    {
        uint slotPrefix = keyPrefixes[slotIndex];
        if (slotPrefix < prefix)
        {
            return descending ? 1 : -1;
        }

        if (slotPrefix > prefix)
        {
            return descending ? -1 : 1;
        }

        int comparison = Scalar8VarIdentityLayout.CompareRecordKey(localBytes, checked((int)recordOffsets[slotIndex]), encodedKey);
        return descending ? -comparison : comparison;
    }

    private int CompareSlotTuple(ReadOnlySpan<byte> localBytes, int slotIndex, uint prefix, ulong encodedKey, ReadOnlySpan<byte> identity)
    {
        uint slotPrefix = keyPrefixes[slotIndex];
        if (slotPrefix < prefix)
        {
            return descending ? 1 : -1;
        }

        if (slotPrefix > prefix)
        {
            return descending ? -1 : 1;
        }

        int comparison = Scalar8VarIdentityLayout.CompareRecordTuple(localBytes, checked((int)recordOffsets[slotIndex]), encodedKey, identity);
        return descending ? -comparison : comparison;
    }
}
