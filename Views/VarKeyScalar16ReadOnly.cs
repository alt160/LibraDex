using LibraDex.Layouts;

namespace LibraDex.Views;

internal sealed class VarKeyScalar16ReadOnly
{
    private readonly ReadOnlyMemory<byte> bytes;
    private readonly VarKeyScalar16Profile profile;
    private readonly uint[] recordOffsets;
    private readonly uint[] keyPrefixes;

    public VarKeyScalar16ReadOnly(ReadOnlyMemory<byte> bytes, VarKeyScalar16Profile profile)
    {
        this.bytes = bytes;
        this.profile = profile;
        IsValid = TryDecodeSlots(bytes.Span, profile, out recordOffsets, out keyPrefixes);
    }

    public bool IsValid { get; }

    public int ItemCount => IsValid ? recordOffsets.Length : 0;

    public int LowerBoundKey(ReadOnlySpan<byte> key)
    {
        uint prefix = VarKeyScalar16Layout.CreateKeyPrefix(key);
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

    public int LowerBound(ReadOnlySpan<byte> key, ulong encodedIdentityHigh, ulong encodedIdentityLow)
    {
        uint prefix = VarKeyScalar16Layout.CreateKeyPrefix(key);
        int low = 0;
        int high = ItemCount;
        ReadOnlySpan<byte> localBytes = bytes.Span;
        while (low < high)
        {
            int middle = low + ((high - low) >> 1);
            int comparison = CompareSlotTuple(localBytes, middle, prefix, key, encodedIdentityHigh, encodedIdentityLow);
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
        return VarKeyScalar16Layout.ReadKey(bytes.Span, checked((int)recordOffsets[slotIndex]));
    }

    public void ReadIdentityAt(int slotIndex, out ulong encodedIdentityHigh, out ulong encodedIdentityLow)
    {
        VarKeyScalar16Layout.ReadIdentity(bytes.Span, checked((int)recordOffsets[slotIndex]), out _, out encodedIdentityHigh, out encodedIdentityLow);
    }

    public bool Contains(ReadOnlySpan<byte> key, ulong encodedIdentityHigh, ulong encodedIdentityLow)
    {
        int slotIndex = LowerBound(key, encodedIdentityHigh, encodedIdentityLow);
        return slotIndex < ItemCount && CompareSlotTuple(bytes.Span, slotIndex, VarKeyScalar16Layout.CreateKeyPrefix(key), key, encodedIdentityHigh, encodedIdentityLow) == 0;
    }

    public int CopyIdentitiesInKeyRange(ReadOnlySpan<byte> lowerKey, ReadOnlySpan<byte> upperKey, Span<ulong> encodedIdentityHighs, Span<ulong> encodedIdentityLows)
    {
        if (lowerKey.SequenceCompareTo(upperKey) > 0)
        {
            throw new ArgumentException("The upper VS16 key must be greater than or equal to the lower key.", nameof(upperKey));
        }

        int copied = 0;
        int slotIndex = LowerBoundKey(lowerKey);
        ReadOnlySpan<byte> localBytes = bytes.Span;
        for (int i = slotIndex; i < ItemCount; i++)
        {
            ReadOnlySpan<byte> key = VarKeyScalar16Layout.ReadKey(localBytes, checked((int)recordOffsets[i]));
            if (key.SequenceCompareTo(upperKey) > 0)
            {
                break;
            }

            if (copied >= encodedIdentityHighs.Length || copied >= encodedIdentityLows.Length)
            {
                throw new ArgumentException("The identity output spans are too small for the requested VS16 range.", nameof(encodedIdentityHighs));
            }

            VarKeyScalar16Layout.ReadIdentity(localBytes, checked((int)recordOffsets[i]), out _, out encodedIdentityHighs[copied], out encodedIdentityLows[copied]);
            copied++;
        }

        return copied;
    }

    internal uint ReadRecordOffsetAt(int slotIndex)
    {
        return recordOffsets[slotIndex];
    }

    internal static bool TryDecodeSlots(ReadOnlySpan<byte> bytes, VarKeyScalar16Profile profile, out uint[] recordOffsets, out uint[] keyPrefixes)
    {
        recordOffsets = [];
        keyPrefixes = [];
        if (bytes.Length < profile.ShelfExtentSize ||
            VarKeyScalar16Layout.ReadMagic(bytes) != VarKeyScalar16Layout.Magic ||
            VarKeyScalar16Layout.ReadFormatVersion(bytes) != VarKeyScalar16Layout.FormatVersion ||
            VarKeyScalar16Layout.ReadHeaderSize(bytes) != VarKeyScalar16Layout.HeaderSize ||
            VarKeyScalar16Layout.ReadShelfExtentSize(bytes) != profile.ShelfExtentSize)
        {
            return false;
        }

        int count = VarKeyScalar16Layout.ReadItemCount(bytes);
        int slotStreamLength = VarKeyScalar16Layout.ReadSlotStreamLength(bytes);
        int slotCapacityBytes = VarKeyScalar16Layout.ReadSlotCapacityBytes(bytes);
        int recordArenaEnd = VarKeyScalar16Layout.ReadRecordArenaEnd(bytes);
        int expectedSlotStreamLength = checked(count * VarKeyScalar16Layout.SlotSize);
        int expectedSlotCapacityBytes = VarKeyScalar16Layout.CalculateSlotCapacityBytes(profile.ShelfExtentSize);
        int recordArenaStart = VarKeyScalar16Layout.HeaderSize + slotCapacityBytes;
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
        int cursor = VarKeyScalar16Layout.HeaderSize;
        for (int i = 0; i < count; i++)
        {
            int offset = VarKeyScalar16Layout.ReadSlotRecordOffset(bytes, cursor);
            if (offset < recordArenaStart || offset >= recordArenaEnd)
            {
                return false;
            }

            int recordLength = VarKeyScalar16Layout.GetRecordLength(bytes, offset);
            if (recordLength <= 0 || offset + recordLength > recordArenaEnd)
            {
                return false;
            }

            offsets[i] = checked((uint)offset);
            prefixes[i] = VarKeyScalar16Layout.ReadSlotKeyPrefix(bytes, cursor);
            cursor += VarKeyScalar16Layout.SlotSize;
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

        return VarKeyScalar16Layout.CompareRecordKey(localBytes, checked((int)recordOffsets[slotIndex]), key);
    }

    private int CompareSlotTuple(ReadOnlySpan<byte> localBytes, int slotIndex, uint prefix, ReadOnlySpan<byte> key, ulong encodedIdentityHigh, ulong encodedIdentityLow)
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

        return VarKeyScalar16Layout.CompareRecordTuple(localBytes, checked((int)recordOffsets[slotIndex]), key, encodedIdentityHigh, encodedIdentityLow);
    }
}
