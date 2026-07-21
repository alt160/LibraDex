using LibraDex.Layouts;

namespace LibraDex.Views;

internal sealed class VarKeyScalar8ReadOnly
{
    private readonly ReadOnlyMemory<byte> bytes;
    private readonly VarKeyScalar8Profile profile;
    private readonly uint[] recordOffsets;
    private readonly uint[] keyPrefixes;
    private readonly bool isDuplicateRun;
    private readonly int duplicateRunKeyLength;

    public VarKeyScalar8ReadOnly(ReadOnlyMemory<byte> bytes, VarKeyScalar8Profile profile)
    {
        this.bytes = bytes;
        this.profile = profile;
        isDuplicateRun = VarKeyScalar8Layout.HasDuplicateRunFlag(bytes.Span);
        if (isDuplicateRun)
        {
            IsValid = TryValidateDuplicateRun(bytes.Span, profile, out duplicateRunKeyLength);
            recordOffsets = [];
            keyPrefixes = [];
        }
        else
        {
            duplicateRunKeyLength = 0;
            IsValid = TryDecodeSlots(bytes.Span, profile, out recordOffsets, out keyPrefixes);
        }
    }

    public bool IsValid { get; }

    public bool IsDuplicateRun => isDuplicateRun;

    public long DuplicateRunNextOffset => IsDuplicateRun ? VarKeyScalar8Layout.ReadDuplicateRunNextOffset(bytes.Span) : 0;

    public int ItemCount => IsValid ? (IsDuplicateRun ? VarKeyScalar8Layout.ReadItemCount(bytes.Span) : recordOffsets.Length) : 0;

    public int PhysicalItemCount => ItemCount;

    public int LiveItemCount => ItemCount;

    public int DeletedItemCount => 0;

    public int LowerBoundKey(ReadOnlySpan<byte> key)
    {
        if (IsDuplicateRun)
        {
            int comparison = VarKeyScalar8Layout.ReadDuplicateRunKey(bytes.Span).SequenceCompareTo(key);
            return comparison < 0 ? ItemCount : 0;
        }

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
        if (IsDuplicateRun)
        {
            int keyComparison = VarKeyScalar8Layout.ReadDuplicateRunKey(bytes.Span).SequenceCompareTo(key);
            if (keyComparison < 0)
            {
                return ItemCount;
            }

            if (keyComparison > 0)
            {
                return 0;
            }

            int runLow = 0;
            int runHigh = ItemCount;
            while (runLow < runHigh)
            {
                int middle = runLow + ((runHigh - runLow) >> 1);
                ulong identity = VarKeyScalar8Layout.ReadDuplicateRunIdentity(bytes.Span, duplicateRunKeyLength, middle);
                if (identity < encodedIdentity)
                {
                    runLow = middle + 1;
                }
                else
                {
                    runHigh = middle;
                }
            }

            return runLow;
        }

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
        if (IsDuplicateRun)
        {
            return VarKeyScalar8Layout.ReadDuplicateRunKey(bytes.Span);
        }

        return VarKeyScalar8Layout.ReadKey(bytes.Span, checked((int)recordOffsets[slotIndex]));
    }

    public ulong ReadIdentityAt(int slotIndex)
    {
        if (IsDuplicateRun)
        {
            return VarKeyScalar8Layout.ReadDuplicateRunIdentity(bytes.Span, duplicateRunKeyLength, slotIndex);
        }

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

    /// <summary>
    /// Counts tuples whose raw key is inside an inclusive range using the decoded slot index only.<br/>
    /// This is the shelf-edge companion to routed metadata counting: fully covered shelves can use item-count metadata, while boundary shelves need only key-slot comparisons and never inspect identity payloads.<br/>
    /// </summary>
    /// <param name="lowerKey">The inclusive lower raw key bound.<br/></param>
    /// <param name="upperKey">The inclusive upper raw key bound.<br/></param>
    /// <returns>The number of shelf-local tuples in the requested key range.<br/></returns>
    public int CountItemsInKeyRange(ReadOnlySpan<byte> lowerKey, ReadOnlySpan<byte> upperKey)
    {
        if (lowerKey.SequenceCompareTo(upperKey) > 0)
        {
            return 0;
        }

        if (IsDuplicateRun)
        {
            ReadOnlySpan<byte> runKey = VarKeyScalar8Layout.ReadDuplicateRunKey(bytes.Span);
            return runKey.SequenceCompareTo(lowerKey) >= 0 && runKey.SequenceCompareTo(upperKey) <= 0
                ? ItemCount
                : 0;
        }

        int lowerSlot = LowerBoundKey(lowerKey);
        int upperSlot = UpperBoundKey(upperKey);
        return Math.Max(0, upperSlot - lowerSlot);
    }

    /// <summary>
    /// Finds the first sorted slot whose key is greater than the supplied raw key.<br/>
    /// Inclusive range counts use this as the exclusive high slot so boundary shelves can count by slot indexes instead of walking every matching record.<br/>
    /// </summary>
    /// <param name="key">The inclusive high raw key bound.<br/></param>
    /// <returns>The first slot after all keys less than or equal to <paramref name="key"/>.<br/></returns>
    public int UpperBoundKey(ReadOnlySpan<byte> key)
    {
        if (IsDuplicateRun)
        {
            int comparison = VarKeyScalar8Layout.ReadDuplicateRunKey(bytes.Span).SequenceCompareTo(key);
            return comparison <= 0 ? ItemCount : 0;
        }

        uint prefix = VarKeyScalar8Layout.CreateKeyPrefix(key);
        int low = 0;
        int high = ItemCount;
        ReadOnlySpan<byte> localBytes = bytes.Span;
        while (low < high)
        {
            int middle = low + ((high - low) >> 1);
            int comparison = CompareSlotKey(localBytes, middle, prefix, key);
            if (comparison <= 0)
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

    internal uint ReadRecordOffsetAt(int slotIndex)
    {
        if (IsDuplicateRun)
        {
            return checked((uint)(VarKeyScalar8Layout.GetDuplicateRunIdentityOffset(duplicateRunKeyLength) + (slotIndex * VarKeyScalar8Layout.IdentitySize)));
        }

        return recordOffsets[slotIndex];
    }

    internal static bool TryValidateDuplicateRun(ReadOnlySpan<byte> bytes, VarKeyScalar8Profile profile, out int keyLength)
    {
        keyLength = 0;
        if (bytes.Length < profile.ShelfExtentSize ||
            VarKeyScalar8Layout.ReadMagic(bytes) != VarKeyScalar8Layout.Magic ||
            VarKeyScalar8Layout.ReadFormatVersion(bytes) != VarKeyScalar8Layout.FormatVersion ||
            VarKeyScalar8Layout.ReadHeaderSize(bytes) != VarKeyScalar8Layout.HeaderSize ||
            VarKeyScalar8Layout.ReadShelfExtentSize(bytes) != profile.ShelfExtentSize ||
            !VarKeyScalar8Layout.HasDuplicateRunFlag(bytes))
        {
            return false;
        }

        int count = VarKeyScalar8Layout.ReadItemCount(bytes);
        keyLength = VarKeyScalar8Layout.ReadDuplicateRunKeyLength(bytes);
        return count >= 0 &&
            keyLength > 0 &&
            keyLength <= profile.MaxKeyLength &&
            count <= VarKeyScalar8Layout.GetDuplicateRunCapacity(profile, keyLength);
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

    /// <summary>
    /// Gets the physical extent size of the validated shelf backing this view.<br/>
    /// Diagnostic readers use this to distinguish logical shelf bytes touched from backing reads satisfied by session caches.<br/>
    /// </summary>
    internal int ShelfExtentSize => profile.ShelfExtentSize;
}
