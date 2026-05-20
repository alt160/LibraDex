using LibraDex.Layouts;

namespace LibraDex.Views;

internal sealed class VarKeyVarIdentityReadOnly
{
    private readonly ReadOnlyMemory<byte> bytes;
    private readonly VarKeyVarIdentityProfile profile;
    private readonly uint[] recordOffsets;
    private readonly uint[] keyPrefixes;

    public VarKeyVarIdentityReadOnly(ReadOnlyMemory<byte> bytes, VarKeyVarIdentityProfile profile)
    {
        this.bytes = bytes;
        this.profile = profile;
        IsValid = TryDecodeSlots(bytes.Span, profile, out recordOffsets, out keyPrefixes);
    }

    public bool IsValid { get; }

    public int ItemCount => IsValid ? recordOffsets.Length : 0;

    public int LowerBoundKey(ReadOnlySpan<byte> key)
    {
        uint prefix = VarKeyVarIdentityLayout.CreateKeyPrefix(key);
        int low = 0;
        int high = ItemCount;
        ReadOnlySpan<byte> localBytes = bytes.Span;
        while (low < high)
        {
            int middle = low + ((high - low) >> 1);
            int comparison = CompareSlotKey(localBytes, middle, prefix, key);
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

    public int LowerBound(ReadOnlySpan<byte> key, ReadOnlySpan<byte> identity)
    {
        uint prefix = VarKeyVarIdentityLayout.CreateKeyPrefix(key);
        int low = 0;
        int high = ItemCount;
        ReadOnlySpan<byte> localBytes = bytes.Span;
        while (low < high)
        {
            int middle = low + ((high - low) >> 1);
            int comparison = CompareSlotTuple(localBytes, middle, prefix, key, identity);
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

    public ReadOnlySpan<byte> ReadKeyAt(int slotIndex)
    {
        return VarKeyVarIdentityLayout.ReadKey(bytes.Span, checked((int)recordOffsets[slotIndex]));
    }

    public ReadOnlySpan<byte> ReadIdentityAt(int slotIndex)
    {
        return VarKeyVarIdentityLayout.ReadIdentity(bytes.Span, checked((int)recordOffsets[slotIndex]), out _);
    }

    public bool Contains(ReadOnlySpan<byte> key, ReadOnlySpan<byte> identity)
    {
        int slotIndex = LowerBound(key, identity);
        return slotIndex < ItemCount && CompareSlotTuple(bytes.Span, slotIndex, VarKeyVarIdentityLayout.CreateKeyPrefix(key), key, identity) == 0;
    }

    internal uint ReadRecordOffsetAt(int slotIndex)
    {
        return recordOffsets[slotIndex];
    }

    internal static bool TryDecodeSlots(ReadOnlySpan<byte> bytes, VarKeyVarIdentityProfile profile, out uint[] recordOffsets, out uint[] keyPrefixes)
    {
        recordOffsets = [];
        keyPrefixes = [];
        if (bytes.Length < profile.ShelfExtentSize ||
            VarKeyVarIdentityLayout.ReadMagic(bytes) != VarKeyVarIdentityLayout.Magic ||
            VarKeyVarIdentityLayout.ReadFormatVersion(bytes) != VarKeyVarIdentityLayout.FormatVersion ||
            VarKeyVarIdentityLayout.ReadHeaderSize(bytes) != VarKeyVarIdentityLayout.HeaderSize ||
            VarKeyVarIdentityLayout.ReadShelfExtentSize(bytes) != profile.ShelfExtentSize)
        {
            return false;
        }

        int count = VarKeyVarIdentityLayout.ReadItemCount(bytes);
        int slotStreamLength = VarKeyVarIdentityLayout.ReadSlotStreamLength(bytes);
        int slotCapacityBytes = VarKeyVarIdentityLayout.ReadSlotCapacityBytes(bytes);
        int recordArenaEnd = VarKeyVarIdentityLayout.ReadRecordArenaEnd(bytes);
        int expectedSlotStreamLength = checked(count * VarKeyVarIdentityLayout.SlotSize);
        int expectedSlotCapacityBytes = VarKeyVarIdentityLayout.CalculateSlotCapacityBytes(profile.ShelfExtentSize);
        int recordArenaStart = VarKeyVarIdentityLayout.HeaderSize + slotCapacityBytes;
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
        int cursor = VarKeyVarIdentityLayout.HeaderSize;
        for (int i = 0; i < count; i++)
        {
            int offset = VarKeyVarIdentityLayout.ReadSlotRecordOffset(bytes, cursor);
            if (offset < recordArenaStart || offset >= recordArenaEnd)
            {
                return false;
            }

            int recordLength = VarKeyVarIdentityLayout.GetRecordLength(bytes, offset);
            if (recordLength <= 0 || offset + recordLength > recordArenaEnd)
            {
                return false;
            }

            offsets[i] = checked((uint)offset);
            prefixes[i] = VarKeyVarIdentityLayout.ReadSlotKeyPrefix(bytes, cursor);
            cursor += VarKeyVarIdentityLayout.SlotSize;
        }

        recordOffsets = offsets;
        keyPrefixes = prefixes;
        return true;
    }

    private int CompareSlotKey(ReadOnlySpan<byte> localBytes, int slotIndex, uint prefix, ReadOnlySpan<byte> key)
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

        return VarKeyVarIdentityLayout.CompareRecordKey(localBytes, checked((int)recordOffsets[slotIndex]), key);
    }

    private int CompareSlotTuple(ReadOnlySpan<byte> localBytes, int slotIndex, uint prefix, ReadOnlySpan<byte> key, ReadOnlySpan<byte> identity)
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

        return VarKeyVarIdentityLayout.CompareRecordTuple(localBytes, checked((int)recordOffsets[slotIndex]), key, identity);
    }
}
