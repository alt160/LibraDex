using LibraDex.Layouts;

namespace LibraDex.Views;

/// <summary>
/// Holds one mutable, disk-shaped `SV16` shelf image plus decoded slot sidecars for session-local write hot paths.<br/>
/// The byte array remains authoritative for persistence and range reads; sidecars avoid decoding 3-byte offsets and key prefixes for every insert into the same shelf.<br/>
/// This type is runtime-only and does not change the `Scalar16VarIdentityLayout` persisted format.<br/>
/// </summary>
internal sealed class Scalar16VarIdentityMutableShelfView
{
    private readonly int[] recordOffsets;
    private readonly uint[] keyPrefixes;
    private readonly ulong[] keyHighs;
    private readonly ulong[] keyLows;
    private readonly int[] sortedIndexes;
    private int itemCount;
    private int slotStreamLength;
    private int recordArenaEnd;

    private Scalar16VarIdentityMutableShelfView(
        byte[] bytes,
        Scalar16VarIdentityProfile profile,
        int[] recordOffsets,
        uint[] keyPrefixes,
        ulong[] keyHighs,
        ulong[] keyLows,
        int[] sortedIndexes,
        int itemCount,
        int slotStreamLength,
        int slotCapacityBytes,
        int recordArenaEnd)
    {
        Bytes = bytes;
        Profile = profile;
        this.recordOffsets = recordOffsets;
        this.keyPrefixes = keyPrefixes;
        this.keyHighs = keyHighs;
        this.keyLows = keyLows;
        this.sortedIndexes = sortedIndexes;
        this.itemCount = itemCount;
        this.slotStreamLength = slotStreamLength;
        SlotCapacityBytes = slotCapacityBytes;
        this.recordArenaEnd = recordArenaEnd;
    }

    /// <summary>
    /// Gets the authoritative runtime shelf bytes that will be copied back to DataKernel staging when the session publishes the shelf.<br/>
    /// </summary>
    public byte[] Bytes { get; }

    /// <summary>
    /// Gets the profile used to validate extent size and maximum identity length.<br/>
    /// </summary>
    public Scalar16VarIdentityProfile Profile { get; }

    /// <summary>
    /// Gets the number of sorted tuple slots currently stored in the shelf.<br/>
    /// </summary>
    public int ItemCount => itemCount;

    /// <summary>
    /// Gets the reserved persisted slot-array byte capacity for this shelf extent.<br/>
    /// </summary>
    public int SlotCapacityBytes { get; }

    /// <summary>
    /// Gets whether this mutable shelf has changed since it was loaded into the session-local sidecar cache.<br/>
    /// </summary>
    public bool IsDirty { get; private set; }

    /// <summary>
    /// Gets whether the persisted 7-byte slot stream in <see cref="Bytes"/> must be rebuilt from the decoded sidecars before disk publication or static shelf helpers read it.<br/>
    /// </summary>
    public bool SlotBytesDirty { get; private set; }

    /// <summary>
    /// Decodes an existing `SV16` shelf into mutable sidecars while keeping the supplied byte array as the authoritative image.<br/>
    /// Sidecar arrays are sized to the reserved slot capacity so ordinary inserts shift existing entries without reallocating slot metadata.<br/>
    /// </summary>
    /// <param name="bytes">The disk-shaped shelf byte image.</param>
    /// <param name="profile">The expected `SV16` shelf profile.</param>
    /// <param name="shelf">The decoded mutable shelf when validation succeeds.</param>
    /// <returns>`true` when the byte image is valid for <paramref name="profile"/>.</returns>
    public static bool TryCreate(byte[] bytes, Scalar16VarIdentityProfile profile, out Scalar16VarIdentityMutableShelfView shelf)
    {
        shelf = null!;
        if (bytes.Length < profile.ShelfExtentSize ||
            Scalar16VarIdentityLayout.ReadMagic(bytes) != Scalar16VarIdentityLayout.Magic ||
            Scalar16VarIdentityLayout.ReadFormatVersion(bytes) != Scalar16VarIdentityLayout.FormatVersion ||
            Scalar16VarIdentityLayout.ReadHeaderSize(bytes) != Scalar16VarIdentityLayout.HeaderSize ||
            Scalar16VarIdentityLayout.ReadShelfExtentSize(bytes) != profile.ShelfExtentSize)
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

        int slotCapacity = slotCapacityBytes / Scalar16VarIdentityLayout.SlotSize;
        int[] offsets = new int[slotCapacity];
        uint[] prefixes = new uint[slotCapacity];
        ulong[] keyHighs = new ulong[slotCapacity];
        ulong[] keyLows = new ulong[slotCapacity];
        int[] indexes = new int[slotCapacity];
        int cursor = Scalar16VarIdentityLayout.HeaderSize;
        for (int i = 0; i < count; i++)
        {
            int offset = Scalar16VarIdentityLayout.ReadSlotRecordOffset(bytes, cursor);
            if (offset < recordArenaStart || offset >= recordArenaEnd)
            {
                return false;
            }

            int recordLength = Scalar16VarIdentityLayout.GetRecordLength(bytes, offset);
            if (recordLength <= 0 || offset + recordLength > recordArenaEnd)
            {
                return false;
            }

            offsets[i] = offset;
            prefixes[i] = Scalar16VarIdentityLayout.ReadSlotKeyPrefix(bytes, cursor);
            keyHighs[i] = Scalar16VarIdentityLayout.ReadKeyHigh(bytes, offset);
            keyLows[i] = Scalar16VarIdentityLayout.ReadKeyLow(bytes, offset);
            indexes[i] = i;
            cursor += Scalar16VarIdentityLayout.SlotSize;
        }

        shelf = new Scalar16VarIdentityMutableShelfView(bytes, profile, offsets, prefixes, keyHighs, keyLows, indexes, count, slotStreamLength, slotCapacityBytes, recordArenaEnd);
        return true;
    }

    /// <summary>
    /// Inserts one scalar key and raw variable identity into the owned shelf image when reserved slot and record capacity are still available.<br/>
    /// Full results are non-mutating and tell the caller to use the existing grow, split, or duplicate-run overflow path with the current authoritative bytes.<br/>
    /// </summary>
    /// <param name="encodedKey">The encoded scalar key to insert.</param>
    /// <param name="identity">The raw variable identity bytes to insert.</param>
    /// <param name="allowDuplicateKeys">Whether duplicate scalar keys with different identities are allowed.</param>
    /// <returns>The insert result.</returns>
    public Scalar16VarIdentityInsertResult Insert(ulong encodedKeyHigh, ulong encodedKeyLow, ReadOnlySpan<byte> identity, bool allowDuplicateKeys)
    {
        if (identity.Length <= 0 || identity.Length > Profile.MaxIdentityLength)
        {
            return Scalar16VarIdentityInsertResult.Invalid;
        }

        uint prefix = Scalar16VarIdentityLayout.CreateKeyPrefix(encodedKeyHigh, encodedKeyLow);
        int insertIndex = itemCount;
        bool sortedAppend = true;
        if (itemCount > 0)
        {
            int lastComparison = CompareSlotTuple(itemCount - 1, prefix, encodedKeyHigh, encodedKeyLow, identity);
            if (lastComparison == 0)
            {
                return Scalar16VarIdentityInsertResult.AlreadyPresent;
            }

            sortedAppend = lastComparison < 0;
            if (!sortedAppend)
            {
                insertIndex = LowerBoundTuple(encodedKeyHigh, encodedKeyLow, prefix, identity);
                if (insertIndex < itemCount && CompareSlotTuple(insertIndex, prefix, encodedKeyHigh, encodedKeyLow, identity) == 0)
                {
                    return Scalar16VarIdentityInsertResult.AlreadyPresent;
                }
            }
        }

        if (!allowDuplicateKeys && ContainsKey(encodedKeyHigh, encodedKeyLow, prefix, out _))
        {
            return Scalar16VarIdentityInsertResult.KeyConflict;
        }

        if (slotStreamLength + Scalar16VarIdentityLayout.SlotSize > SlotCapacityBytes)
        {
            return Scalar16VarIdentityInsertResult.Full;
        }

        int recordLength = Scalar16VarIdentityLayout.GetNewRecordLength(identity.Length);
        int recordOffset = recordArenaEnd;
        int newRecordArenaEnd = recordOffset + recordLength;
        if (newRecordArenaEnd > Profile.ShelfExtentSize)
        {
            return Scalar16VarIdentityInsertResult.Full;
        }

        Scalar16VarIdentityLayout.WriteRecord(Bytes, recordOffset, encodedKeyHigh, encodedKeyLow, identity);
        if (!sortedAppend)
        {
            Array.Copy(sortedIndexes, insertIndex, sortedIndexes, insertIndex + 1, itemCount - insertIndex);
        }

        int physicalIndex = itemCount;
        recordOffsets[physicalIndex] = recordOffset;
        keyPrefixes[physicalIndex] = prefix;
        keyHighs[physicalIndex] = encodedKeyHigh;
        keyLows[physicalIndex] = encodedKeyLow;
        sortedIndexes[insertIndex] = physicalIndex;
        itemCount++;
        slotStreamLength += Scalar16VarIdentityLayout.SlotSize;
        recordArenaEnd = newRecordArenaEnd;
        Scalar16VarIdentityLayout.WriteItemCount(Bytes, itemCount);
        Scalar16VarIdentityLayout.WriteSlotStreamLength(Bytes, slotStreamLength);
        Scalar16VarIdentityLayout.WriteRecordArenaEnd(Bytes, recordArenaEnd);
        if (sortedAppend)
        {
            int slotOffset = Scalar16VarIdentityLayout.HeaderSize + checked(insertIndex * Scalar16VarIdentityLayout.SlotSize);
            Scalar16VarIdentityLayout.WriteSlotRecordOffset(Bytes, slotOffset, recordOffset);
            Scalar16VarIdentityLayout.WriteSlotKeyPrefix(Bytes, slotOffset, prefix);
        }
        else
        {
            SlotBytesDirty = true;
        }

        IsDirty = true;
        return Scalar16VarIdentityInsertResult.Inserted;
    }

    /// <summary>
    /// Rebuilds the persisted slot stream in the authoritative shelf image from the in-memory sidecars when needed.<br/>
    /// Batched write paths keep sidecars authoritative between inserts to avoid repeatedly shifting disk-shaped slot bytes in RAM.<br/>
    /// Callers must invoke this before publishing the shelf image or passing it to static helpers that only understand the persisted slot stream.<br/>
    /// </summary>
    internal void EnsureSlotBytesCurrent()
    {
        if (!SlotBytesDirty)
        {
            return;
        }

        int slotOffset = Scalar16VarIdentityLayout.HeaderSize;
        for (int i = 0; i < itemCount; i++)
        {
            int physicalIndex = sortedIndexes[i];
            Scalar16VarIdentityLayout.WriteSlotRecordOffset(Bytes, slotOffset, recordOffsets[physicalIndex]);
            Scalar16VarIdentityLayout.WriteSlotKeyPrefix(Bytes, slotOffset, keyPrefixes[physicalIndex]);
            slotOffset += Scalar16VarIdentityLayout.SlotSize;
        }

        SlotBytesDirty = false;
    }

    /// <summary>
    /// Marks the shelf image dirty after a caller performs a direct header-only mutation.<br/>
    /// </summary>
    public void MarkDirty()
    {
        IsDirty = true;
    }

    /// <summary>
    /// Reads the scalar key at a sorted slot from the decoded mutable sidecar.<br/>
    /// Duplicate-run fast paths use this to validate first/last key bounds without allocating a read-only decoder.<br/>
    /// </summary>
    /// <param name="slotIndex">The sorted slot index to read.</param>
    /// <returns>The encoded scalar key at the requested slot.</returns>
    internal ulong ReadKeyHighAt(int slotIndex)
    {
        return keyHighs[sortedIndexes[slotIndex]];
    }

    /// <summary>
    /// Reads the low half of the scalar key at a sorted slot from the decoded mutable sidecar.<br/>
    /// Split planning uses this together with <see cref="ReadKeyHighAt(int)"/> to avoid reparsing record bytes while rebuilding shelves.<br/>
    /// </summary>
    /// <param name="slotIndex">The sorted slot index to read.</param>
    /// <returns>The encoded scalar-key low half at the requested slot.</returns>
    internal ulong ReadKeyLowAt(int slotIndex)
    {
        return keyLows[sortedIndexes[slotIndex]];
    }

    /// <summary>
    /// Reads the raw identity at a sorted slot from the decoded mutable sidecar.<br/>
    /// The returned span points into the authoritative shelf bytes and is valid until the shelf image is mutated again.<br/>
    /// </summary>
    /// <param name="slotIndex">The sorted slot index to read.</param>
    /// <returns>The raw identity bytes at the requested slot.</returns>
    internal ReadOnlySpan<byte> ReadIdentityAt(int slotIndex)
    {
        return Scalar16VarIdentityLayout.ReadIdentity(Bytes, recordOffsets[sortedIndexes[slotIndex]]);
    }

    private bool ContainsKey(ulong encodedKeyHigh, ulong encodedKeyLow, uint prefix, out int index)
    {
        int low = 0;
        int high = itemCount;
        while (low < high)
        {
            int middle = low + ((high - low) >> 1);
            int comparison = CompareSlotKey(middle, prefix, encodedKeyHigh, encodedKeyLow);
            if (comparison < 0)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        index = low;
        return low < itemCount && CompareSlotKey(low, prefix, encodedKeyHigh, encodedKeyLow) == 0;
    }

    /// <summary>
    /// Finds the sorted sidecar insertion point for a tuple using binary search over the integer order index.<br/>
    /// This keeps non-monotonic inserts from moving record-offset and key-prefix sidecars; only the compact integer order index is shifted.<br/>
    /// </summary>
    /// <param name="encodedKeyHigh">The encoded scalar key high half to insert.</param>
    /// <param name="encodedKeyLow">The encoded scalar key low half to insert.</param>
    /// <param name="prefix">The cached high 4-byte key prefix.</param>
    /// <param name="identity">The raw identity bytes to insert.</param>
    /// <returns>The sorted sidecar index where the tuple belongs.</returns>
    private int LowerBoundTuple(ulong encodedKeyHigh, ulong encodedKeyLow, uint prefix, ReadOnlySpan<byte> identity)
    {
        int low = 0;
        int high = itemCount;
        while (low < high)
        {
            int middle = low + ((high - low) >> 1);
            int comparison = CompareSlotTuple(middle, prefix, encodedKeyHigh, encodedKeyLow, identity);
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

    private int CompareSlotKey(int slotIndex, uint prefix, ulong encodedKeyHigh, ulong encodedKeyLow)
    {
        int physicalIndex = sortedIndexes[slotIndex];
        uint slotPrefix = keyPrefixes[physicalIndex];
        if (slotPrefix < prefix)
        {
            return -1;
        }

        if (slotPrefix > prefix)
        {
            return 1;
        }

        ulong slotHigh = keyHighs[physicalIndex];
        if (slotHigh < encodedKeyHigh)
        {
            return -1;
        }

        if (slotHigh > encodedKeyHigh)
        {
            return 1;
        }

        ulong slotLow = keyLows[physicalIndex];
        if (slotLow < encodedKeyLow)
        {
            return -1;
        }

        return slotLow > encodedKeyLow ? 1 : 0;
    }

    private int CompareSlotTuple(int slotIndex, uint prefix, ulong encodedKeyHigh, ulong encodedKeyLow, ReadOnlySpan<byte> identity)
    {
        int physicalIndex = sortedIndexes[slotIndex];
        uint slotPrefix = keyPrefixes[physicalIndex];
        if (slotPrefix < prefix)
        {
            return -1;
        }

        if (slotPrefix > prefix)
        {
            return 1;
        }

        ulong slotHigh = keyHighs[physicalIndex];
        if (slotHigh < encodedKeyHigh)
        {
            return -1;
        }

        if (slotHigh > encodedKeyHigh)
        {
            return 1;
        }

        ulong slotLow = keyLows[physicalIndex];
        if (slotLow < encodedKeyLow)
        {
            return -1;
        }

        if (slotLow > encodedKeyLow)
        {
            return 1;
        }

        return Scalar16VarIdentityLayout.CompareIdentityBytes(Scalar16VarIdentityLayout.ReadIdentity(Bytes, recordOffsets[physicalIndex]), identity);
    }
}
