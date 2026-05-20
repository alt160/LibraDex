using LibraDex.Layouts;

namespace LibraDex.Views;

internal sealed class VarKeyScalar8ReadOnly
{
    private readonly ReadOnlyMemory<byte> bytes;
    private readonly VarKeyScalar8Profile profile;
    private readonly uint[] recordOffsets;
    private readonly uint[] keyPrefixes;

    public VarKeyScalar8ReadOnly(ReadOnlyMemory<byte> bytes, VarKeyScalar8Profile profile)
    {
        this.bytes = bytes;
        this.profile = profile;
        IsValid = TryDecodeSlots(bytes.Span, profile, out recordOffsets, out keyPrefixes);
    }

    public bool IsValid { get; }

    public int ItemCount => IsValid ? recordOffsets.Length : 0;

    public int LowerBoundKey(ReadOnlySpan<byte> key)
    {
        uint prefix = VarKeyScalar8Layout.CreateKeyPrefix(key);
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

    public int LowerBound(ReadOnlySpan<byte> key, ulong encodedIdentity)
    {
        uint prefix = VarKeyScalar8Layout.CreateKeyPrefix(key);
        int low = 0;
        int high = ItemCount;
        ReadOnlySpan<byte> localBytes = bytes.Span;
        while (low < high)
        {
            int middle = low + ((high - low) >> 1);
            int comparison = CompareSlotTuple(localBytes, middle, prefix, key, encodedIdentity);
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
        return VarKeyScalar8Layout.ReadKey(bytes.Span, checked((int)recordOffsets[slotIndex]));
    }

    public ulong ReadIdentityAt(int slotIndex)
    {
        return VarKeyScalar8Layout.ReadIdentity(bytes.Span, checked((int)recordOffsets[slotIndex]), out _);
    }

    public bool Contains(ReadOnlySpan<byte> key, ulong encodedIdentity)
    {
        int slotIndex = LowerBound(key, encodedIdentity);
        return slotIndex < ItemCount && CompareSlotTuple(bytes.Span, slotIndex, VarKeyScalar8Layout.CreateKeyPrefix(key), key, encodedIdentity) == 0;
    }

    public int CopyIdentitiesInKeyRange(ReadOnlySpan<byte> lowerKey, ReadOnlySpan<byte> upperKey, Span<ulong> encodedIdentities)
    {
        if (lowerKey.SequenceCompareTo(upperKey) > 0)
        {
            throw new ArgumentException("The upper VS8 key must be greater than or equal to the lower key.", nameof(upperKey));
        }

        int copied = 0;
        int slotIndex = LowerBoundKey(lowerKey);
        ReadOnlySpan<byte> localBytes = bytes.Span;
        for (int i = slotIndex; i < ItemCount; i++)
        {
            ReadOnlySpan<byte> key = VarKeyScalar8Layout.ReadKey(localBytes, checked((int)recordOffsets[i]));
            if (key.SequenceCompareTo(upperKey) > 0)
            {
                break;
            }

            if (copied >= encodedIdentities.Length)
            {
                throw new ArgumentException("The identity output span is too small for the requested VS8 range.", nameof(encodedIdentities));
            }

            encodedIdentities[copied++] = VarKeyScalar8Layout.ReadIdentity(localBytes, checked((int)recordOffsets[i]), out _);
        }

        return copied;
    }

    internal uint ReadRecordOffsetAt(int slotIndex)
    {
        return recordOffsets[slotIndex];
    }

    internal static bool TryDecodeSlots(ReadOnlySpan<byte> bytes, VarKeyScalar8Profile profile, out uint[] recordOffsets, out uint[] keyPrefixes)
    {
        recordOffsets = [];
        keyPrefixes = [];
        if (bytes.Length < profile.ShelfExtentSize ||
            VarKeyScalar8Layout.ReadMagic(bytes) != VarKeyScalar8Layout.Magic ||
            VarKeyScalar8Layout.ReadFormatVersion(bytes) != VarKeyScalar8Layout.FormatVersion ||
            VarKeyScalar8Layout.ReadHeaderSize(bytes) != VarKeyScalar8Layout.HeaderSize ||
            VarKeyScalar8Layout.ReadShelfExtentSize(bytes) != profile.ShelfExtentSize)
        {
            return false;
        }

        int count = VarKeyScalar8Layout.ReadItemCount(bytes);
        int slotStreamLength = VarKeyScalar8Layout.ReadSlotStreamLength(bytes);
        int slotCapacityBytes = VarKeyScalar8Layout.ReadSlotCapacityBytes(bytes);
        int recordArenaEnd = VarKeyScalar8Layout.ReadRecordArenaEnd(bytes);
        int expectedSlotStreamLength = checked(count * VarKeyScalar8Layout.SlotSize);
        int expectedSlotCapacityBytes = VarKeyScalar8Layout.CalculateSlotCapacityBytes(profile.ShelfExtentSize);
        int recordArenaStart = VarKeyScalar8Layout.HeaderSize + slotCapacityBytes;
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
        int cursor = VarKeyScalar8Layout.HeaderSize;
        for (int i = 0; i < count; i++)
        {
            int offset = VarKeyScalar8Layout.ReadSlotRecordOffset(bytes, cursor);
            if (offset < recordArenaStart || offset >= recordArenaEnd)
            {
                return false;
            }

            int recordLength = VarKeyScalar8Layout.GetRecordLength(bytes, offset);
            if (recordLength <= 0 || offset + recordLength > recordArenaEnd)
            {
                return false;
            }

            offsets[i] = checked((uint)offset);
            prefixes[i] = VarKeyScalar8Layout.ReadSlotKeyPrefix(bytes, cursor);
            cursor += VarKeyScalar8Layout.SlotSize;
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

        return VarKeyScalar8Layout.CompareRecordKey(localBytes, checked((int)recordOffsets[slotIndex]), key);
    }

    private int CompareSlotTuple(ReadOnlySpan<byte> localBytes, int slotIndex, uint prefix, ReadOnlySpan<byte> key, ulong encodedIdentity)
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

        return VarKeyScalar8Layout.CompareRecordTuple(localBytes, checked((int)recordOffsets[slotIndex]), key, encodedIdentity);
    }
}
