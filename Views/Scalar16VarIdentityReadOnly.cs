using LibraDex.Layouts;

namespace LibraDex.Views;

internal sealed class Scalar16VarIdentityReadOnly : IVarIdentityReadOnlyShelf
{
    private readonly ReadOnlyMemory<byte> bytes;
    private readonly byte[]? ownedBytes;
    private readonly bool descending;
    private readonly uint[] recordOffsets;
    private readonly uint[] keyPrefixes;

    public Scalar16VarIdentityReadOnly(ReadOnlyMemory<byte> bytes, Scalar16VarIdentityProfile profile)
    {
        this.bytes = bytes;
        descending = profile.Descending;
        IsValid = TryDecodeSlots(bytes.Span, profile, validateRecords: true, out recordOffsets, out keyPrefixes);
    }

    /// <summary>
    /// Creates an immutable `SV16` shelf view that retains explicit ownership of the supplied byte array.<br/>
    /// Session read caching uses this overload so its retained-byte accounting and decoded sidecars share one visible ownership unit.<br/>
    /// </summary>
    /// <param name="bytes">The complete immutable shelf byte array retained by the view.<br/></param>
    /// <param name="profile">The `SV16` shelf profile used to validate and decode slot metadata.<br/></param>
    internal Scalar16VarIdentityReadOnly(byte[] bytes, Scalar16VarIdentityProfile profile)
        : this(bytes, profile, validateRecords: true)
    {
    }

    internal Scalar16VarIdentityReadOnly(byte[] bytes, Scalar16VarIdentityProfile profile, bool validateRecords)
    {
        this.bytes = bytes;
        ownedBytes = bytes;
        descending = profile.Descending;
        IsValid = TryDecodeSlots(bytes, profile, validateRecords, out recordOffsets, out keyPrefixes);
    }

    public bool IsValid { get; }

    internal bool IsDescending => descending;

    public int ItemCount => IsValid ? recordOffsets.Length : 0;

    public int PhysicalItemCount => ItemCount;

    public int LiveItemCount => ItemCount;

    public int DeletedItemCount => 0;

    internal byte[] Bytes => ownedBytes ?? throw new InvalidOperationException("This SV16 read-only view does not own a cacheable shelf byte array.");

    internal long RetainedSidecarBytes => checked((long)recordOffsets.Length * sizeof(uint) + (long)keyPrefixes.Length * sizeof(uint));

    byte[] IVarIdentityReadOnlyShelf.Bytes => Bytes;

    long IVarIdentityReadOnlyShelf.RetainedSidecarBytes => RetainedSidecarBytes;

    internal long NextShelfOffset => Scalar16VarIdentityLayout.ReadNextShelfOffset(bytes.Span);

    public int LowerBoundKey(ulong encodedKeyHigh, ulong encodedKeyLow)
    {
        uint prefix = Scalar16VarIdentityLayout.CreateKeyPrefix(encodedKeyHigh, encodedKeyLow);
        int low = 0;
        int high = ItemCount;
        ReadOnlySpan<byte> localBytes = bytes.Span;
        while (low < high)
        {
            int middle = low + ((high - low) >> 1);
            int comparison = CompareSlotKey(localBytes, middle, prefix, encodedKeyHigh, encodedKeyLow);
            if (descending ? comparison > 0 : comparison < 0)
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

    public int LowerBound(ulong encodedKeyHigh, ulong encodedKeyLow, ReadOnlySpan<byte> identity)
    {
        uint prefix = Scalar16VarIdentityLayout.CreateKeyPrefix(encodedKeyHigh, encodedKeyLow);
        int low = 0;
        int high = ItemCount;
        ReadOnlySpan<byte> localBytes = bytes.Span;
        while (low < high)
        {
            int middle = low + ((high - low) >> 1);
            int comparison = CompareSlotTuple(localBytes, middle, prefix, encodedKeyHigh, encodedKeyLow, identity);
            if (descending ? comparison > 0 : comparison < 0)
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

    public ulong ReadKeyHighAt(int slotIndex)
    {
        return Scalar16VarIdentityLayout.ReadKeyHigh(bytes.Span, checked((int)recordOffsets[slotIndex]));
    }

    public ulong ReadKeyLowAt(int slotIndex)
    {
        return Scalar16VarIdentityLayout.ReadKeyLow(bytes.Span, checked((int)recordOffsets[slotIndex]));
    }

    public ReadOnlySpan<byte> ReadIdentityAt(int slotIndex)
    {
        return Scalar16VarIdentityLayout.ReadIdentity(bytes.Span, checked((int)recordOffsets[slotIndex]));
    }

    public bool Contains(ulong encodedKeyHigh, ulong encodedKeyLow, ReadOnlySpan<byte> identity)
    {
        int slotIndex = LowerBound(encodedKeyHigh, encodedKeyLow, identity);
        return slotIndex < ItemCount &&
            CompareSlotTuple(bytes.Span, slotIndex, Scalar16VarIdentityLayout.CreateKeyPrefix(encodedKeyHigh, encodedKeyLow), encodedKeyHigh, encodedKeyLow, identity) == 0;
    }

    internal uint ReadRecordOffsetAt(int slotIndex)
    {
        return recordOffsets[slotIndex];
    }

    internal static bool TryDecodeSlots(ReadOnlySpan<byte> bytes, Scalar16VarIdentityProfile profile, out uint[] recordOffsets, out uint[] keyPrefixes, out ulong[] keyHighs, out ulong[] keyLows)
    {
        recordOffsets = [];
        keyPrefixes = [];
        keyHighs = [];
        keyLows = [];
        if (!TryDecodeSlots(bytes, profile, validateRecords: true, out recordOffsets, out keyPrefixes))
        {
            return false;
        }

        keyHighs = new ulong[recordOffsets.Length];
        keyLows = new ulong[recordOffsets.Length];
        for (int i = 0; i < recordOffsets.Length; i++)
        {
            int offset = checked((int)recordOffsets[i]);
            keyHighs[i] = Scalar16VarIdentityLayout.ReadKeyHigh(bytes, offset);
            keyLows[i] = Scalar16VarIdentityLayout.ReadKeyLow(bytes, offset);
        }

        return true;
    }

    internal static bool TryDecodeSlots(ReadOnlySpan<byte> bytes, Scalar16VarIdentityProfile profile, out uint[] recordOffsets, out uint[] keyPrefixes)
    {
        return TryDecodeSlots(bytes, profile, validateRecords: true, out recordOffsets, out keyPrefixes);
    }

    internal static bool TryDecodeSlots(ReadOnlySpan<byte> bytes, Scalar16VarIdentityProfile profile, bool validateRecords, out uint[] recordOffsets, out uint[] keyPrefixes)
    {
        recordOffsets = [];
        keyPrefixes = [];
        if (bytes.Length < profile.ShelfExtentSize ||
            Scalar16VarIdentityLayout.ReadMagic(bytes) != Scalar16VarIdentityLayout.Magic ||
            Scalar16VarIdentityLayout.ReadFormatVersion(bytes) != Scalar16VarIdentityLayout.FormatVersion ||
            Scalar16VarIdentityLayout.ReadHeaderSize(bytes) != Scalar16VarIdentityLayout.HeaderSize ||
            Scalar16VarIdentityLayout.ReadShelfExtentSize(bytes) != profile.ShelfExtentSize ||
            ((Scalar16VarIdentityLayout.ReadFlags(bytes) & Scalar16VarIdentityLayout.DescendingFlag) != 0) != profile.Descending)
        {
            return false;
        }

        int count = Scalar16VarIdentityLayout.ReadItemCount(bytes);
        int slotStreamLength = Scalar16VarIdentityLayout.ReadSlotStreamLength(bytes);
        int slotCapacityBytes = Scalar16VarIdentityLayout.ReadSlotCapacityBytes(bytes);
        int recordArenaEnd = Scalar16VarIdentityLayout.ReadRecordArenaEnd(bytes);
        int expectedSlotStreamLength = checked(count * Scalar16VarIdentityLayout.SlotSize);
        int expectedSlotCapacityBytes = Scalar16VarIdentityLayout.CalculateSlotCapacityBytes(profile.ShelfExtentSize);
        int recordArenaStart = Scalar16VarIdentityLayout.HeaderSize + slotCapacityBytes;
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
        int cursor = Scalar16VarIdentityLayout.HeaderSize;
        for (int i = 0; i < count; i++)
        {
            int offset = Scalar16VarIdentityLayout.ReadSlotRecordOffset(bytes, cursor);
            if (offset < recordArenaStart || offset >= recordArenaEnd)
            {
                return false;
            }

            if (validateRecords)
            {
                int recordLength = Scalar16VarIdentityLayout.GetRecordLength(bytes, offset);
                if (recordLength <= 0 || offset + recordLength > recordArenaEnd)
                {
                    return false;
                }
            }

            offsets[i] = checked((uint)offset);
            prefixes[i] = Scalar16VarIdentityLayout.ReadSlotKeyPrefix(bytes, cursor);
            cursor += Scalar16VarIdentityLayout.SlotSize;
        }

        recordOffsets = offsets;
        keyPrefixes = prefixes;
        return true;
    }

    private int CompareSlotKey(ReadOnlySpan<byte> localBytes, int slotIndex, uint prefix, ulong encodedKeyHigh, ulong encodedKeyLow)
    {
        uint slotPrefix = keyPrefixes[slotIndex];
        if (slotPrefix < prefix)
        {
            return -1;
        }

        if (slotPrefix > prefix)
        {
            return 1;
        }

        return Scalar16VarIdentityLayout.CompareRecordKey(localBytes, checked((int)recordOffsets[slotIndex]), encodedKeyHigh, encodedKeyLow);
    }

    private int CompareSlotTuple(ReadOnlySpan<byte> localBytes, int slotIndex, uint prefix, ulong encodedKeyHigh, ulong encodedKeyLow, ReadOnlySpan<byte> identity)
    {
        uint slotPrefix = keyPrefixes[slotIndex];
        if (slotPrefix < prefix)
        {
            return -1;
        }

        if (slotPrefix > prefix)
        {
            return 1;
        }

        return Scalar16VarIdentityLayout.CompareRecordTuple(localBytes, checked((int)recordOffsets[slotIndex]), encodedKeyHigh, encodedKeyLow, identity);
    }

}

/// <summary>
/// Exposes the owned immutable shelf bytes and decoded-sidecar retention needed by the shared session cache.<br/>
/// The interface is internal so persisted shelf families retain strongly typed public/read paths without adding cache concepts to caller APIs.<br/>
/// </summary>
internal interface IVarIdentityReadOnlyShelf
{
    /// <summary>
    /// Gets the complete immutable shelf byte array owned by the decoded view.<br/>
    /// </summary>
    byte[] Bytes { get; }

    /// <summary>
    /// Gets the retained decoded-sidecar bytes owned in addition to the shelf image.<br/>
    /// </summary>
    long RetainedSidecarBytes { get; }
}
