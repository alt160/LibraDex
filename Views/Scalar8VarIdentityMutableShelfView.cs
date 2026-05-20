using LibraDex.Layouts;

namespace LibraDex.Views;

/// <summary>
/// Holds one mutable, disk-shaped `SV8` shelf image plus decoded slot sidecars for session-local write hot paths.<br/>
/// The byte array remains authoritative for persistence and range reads; sidecars avoid decoding 3-byte offsets and key prefixes for every insert into the same shelf.<br/>
/// This type is runtime-only and does not change the `Scalar8VarIdentityLayout` persisted format.<br/>
/// </summary>
internal sealed class Scalar8VarIdentityMutableShelfView
{
    private readonly int[] recordOffsets;
    private readonly uint[] keyPrefixes;
    private readonly ulong[] keys;
    private readonly int[] sortedIndexes;
    private int itemCount;
    private int slotStreamLength;
    private int recordArenaEnd;

    private Scalar8VarIdentityMutableShelfView(
        byte[] bytes,
        Scalar8VarIdentityProfile profile,
        int[] recordOffsets,
        uint[] keyPrefixes,
        ulong[] keys,
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
        this.keys = keys;
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
    public Scalar8VarIdentityProfile Profile { get; }

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
    /// Decodes an existing `SV8` shelf into mutable sidecars while keeping the supplied byte array as the authoritative image.<br/>
    /// Sidecar arrays are sized to the reserved slot capacity so ordinary inserts shift existing entries without reallocating slot metadata.<br/>
    /// </summary>
    /// <param name="bytes">The disk-shaped shelf byte image.</param>
    /// <param name="profile">The expected `SV8` shelf profile.</param>
    /// <param name="shelf">The decoded mutable shelf when validation succeeds.</param>
    /// <returns>`true` when the byte image is valid for <paramref name="profile"/>.</returns>
    public static bool TryCreate(byte[] bytes, Scalar8VarIdentityProfile profile, out Scalar8VarIdentityMutableShelfView shelf)
    {
        shelf = null!;
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

        int slotCapacity = slotCapacityBytes / Scalar8VarIdentityLayout.SlotSize;
        int[] offsets = new int[slotCapacity];
        uint[] prefixes = new uint[slotCapacity];
        ulong[] keys = new ulong[slotCapacity];
        int[] indexes = new int[slotCapacity];
        int cursor = Scalar8VarIdentityLayout.HeaderSize;
        for (int i = 0; i < count; i++)
        {
            int offset = Scalar8VarIdentityLayout.ReadSlotRecordOffset(bytes, cursor);
            if (offset < recordArenaStart || offset >= recordArenaEnd)
            {
                return false;
            }

            int recordLength = Scalar8VarIdentityLayout.GetRecordLength(bytes, offset);
            if (recordLength <= 0 || offset + recordLength > recordArenaEnd)
            {
                return false;
            }

            offsets[i] = offset;
            prefixes[i] = Scalar8VarIdentityLayout.ReadSlotKeyPrefix(bytes, cursor);
            keys[i] = Scalar8VarIdentityLayout.ReadKey(bytes, offset);
            indexes[i] = i;
            cursor += Scalar8VarIdentityLayout.SlotSize;
        }

        shelf = new Scalar8VarIdentityMutableShelfView(bytes, profile, offsets, prefixes, keys, indexes, count, slotStreamLength, slotCapacityBytes, recordArenaEnd);
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
    public Scalar8VarIdentityInsertResult Insert(ulong encodedKey, ReadOnlySpan<byte> identity, bool allowDuplicateKeys)
    {
        if (identity.Length <= 0 || identity.Length > Profile.MaxIdentityLength)
        {
            return Scalar8VarIdentityInsertResult.Invalid;
        }

        uint prefix = Scalar8VarIdentityLayout.CreateKeyPrefix(encodedKey);
        int insertIndex = itemCount;
        bool sortedAppend = true;
        if (itemCount > 0)
        {
            int lastComparison = CompareSlotTuple(itemCount - 1, prefix, encodedKey, identity);
            if (lastComparison == 0)
            {
                return Scalar8VarIdentityInsertResult.AlreadyPresent;
            }

            sortedAppend = lastComparison < 0;
            if (!sortedAppend)
            {
                insertIndex = LowerBoundTuple(encodedKey, prefix, identity);
                if (insertIndex < itemCount && CompareSlotTuple(insertIndex, prefix, encodedKey, identity) == 0)
                {
                    return Scalar8VarIdentityInsertResult.AlreadyPresent;
                }
            }
        }

        if (!allowDuplicateKeys && ContainsKey(encodedKey, prefix, out _))
        {
            return Scalar8VarIdentityInsertResult.KeyConflict;
        }

        if (slotStreamLength + Scalar8VarIdentityLayout.SlotSize > SlotCapacityBytes)
        {
            return Scalar8VarIdentityInsertResult.Full;
        }

        int recordLength = Scalar8VarIdentityLayout.GetNewRecordLength(identity.Length);
        int recordOffset = recordArenaEnd;
        int newRecordArenaEnd = recordOffset + recordLength;
        if (newRecordArenaEnd > Profile.ShelfExtentSize)
        {
            return Scalar8VarIdentityInsertResult.Full;
        }

        Scalar8VarIdentityLayout.WriteRecord(Bytes, recordOffset, encodedKey, identity);
        if (!sortedAppend)
        {
            Array.Copy(sortedIndexes, insertIndex, sortedIndexes, insertIndex + 1, itemCount - insertIndex);
        }

        int physicalIndex = itemCount;
        recordOffsets[physicalIndex] = recordOffset;
        keyPrefixes[physicalIndex] = prefix;
        keys[physicalIndex] = encodedKey;
        sortedIndexes[insertIndex] = physicalIndex;
        itemCount++;
        slotStreamLength += Scalar8VarIdentityLayout.SlotSize;
        recordArenaEnd = newRecordArenaEnd;
        Scalar8VarIdentityLayout.WriteItemCount(Bytes, itemCount);
        Scalar8VarIdentityLayout.WriteSlotStreamLength(Bytes, slotStreamLength);
        Scalar8VarIdentityLayout.WriteRecordArenaEnd(Bytes, recordArenaEnd);
        if (sortedAppend)
        {
            int slotOffset = Scalar8VarIdentityLayout.HeaderSize + checked(insertIndex * Scalar8VarIdentityLayout.SlotSize);
            Scalar8VarIdentityLayout.WriteSlotRecordOffset(Bytes, slotOffset, recordOffset);
            Scalar8VarIdentityLayout.WriteSlotKeyPrefix(Bytes, slotOffset, prefix);
        }
        else
        {
            SlotBytesDirty = true;
        }

        IsDirty = true;
        return Scalar8VarIdentityInsertResult.Inserted;
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

        int slotOffset = Scalar8VarIdentityLayout.HeaderSize;
        for (int i = 0; i < itemCount; i++)
        {
            int physicalIndex = sortedIndexes[i];
            Scalar8VarIdentityLayout.WriteSlotRecordOffset(Bytes, slotOffset, recordOffsets[physicalIndex]);
            Scalar8VarIdentityLayout.WriteSlotKeyPrefix(Bytes, slotOffset, keyPrefixes[physicalIndex]);
            slotOffset += Scalar8VarIdentityLayout.SlotSize;
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
    internal ulong ReadKeyAt(int slotIndex)
    {
        return keys[sortedIndexes[slotIndex]];
    }

    /// <summary>
    /// Reads the raw identity at a sorted slot from the decoded mutable sidecar.<br/>
    /// The returned span points into the authoritative shelf bytes and is valid until the shelf image is mutated again.<br/>
    /// </summary>
    /// <param name="slotIndex">The sorted slot index to read.</param>
    /// <returns>The raw identity bytes at the requested slot.</returns>
    internal ReadOnlySpan<byte> ReadIdentityAt(int slotIndex)
    {
        return Scalar8VarIdentityLayout.ReadIdentity(Bytes, recordOffsets[sortedIndexes[slotIndex]]);
    }

    private bool ContainsKey(ulong encodedKey, uint prefix, out int index)
    {
        int low = 0;
        int high = itemCount;
        while (low < high)
        {
            int middle = low + ((high - low) >> 1);
            int comparison = CompareSlotKey(middle, prefix, encodedKey);
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
        return low < itemCount && CompareSlotKey(low, prefix, encodedKey) == 0;
    }

    /// <summary>
    /// Finds the sorted sidecar insertion point for a tuple using binary search over the integer order index.<br/>
    /// This keeps non-monotonic inserts from moving record-offset and key-prefix sidecars; only the compact integer order index is shifted.<br/>
    /// </summary>
    /// <param name="encodedKey">The encoded scalar key to insert.</param>
    /// <param name="prefix">The cached high 4-byte key prefix.</param>
    /// <param name="identity">The raw identity bytes to insert.</param>
    /// <returns>The sorted sidecar index where the tuple belongs.</returns>
    private int LowerBoundTuple(ulong encodedKey, uint prefix, ReadOnlySpan<byte> identity)
    {
        int low = 0;
        int high = itemCount;
        while (low < high)
        {
            int middle = low + ((high - low) >> 1);
            int comparison = CompareSlotTuple(middle, prefix, encodedKey, identity);
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

    private int CompareSlotKey(int slotIndex, uint prefix, ulong encodedKey)
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

        ulong slotKey = keys[physicalIndex];
        if (slotKey < encodedKey)
        {
            return -1;
        }

        return slotKey > encodedKey ? 1 : 0;
    }

    private int CompareSlotTuple(int slotIndex, uint prefix, ulong encodedKey, ReadOnlySpan<byte> identity)
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

        ulong slotKey = keys[physicalIndex];
        if (slotKey < encodedKey)
        {
            return -1;
        }

        if (slotKey > encodedKey)
        {
            return 1;
        }

        return Scalar8VarIdentityLayout.CompareIdentityBytes(Scalar8VarIdentityLayout.ReadIdentity(Bytes, recordOffsets[physicalIndex]), identity);
    }
}
