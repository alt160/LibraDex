using LibraDex.Layouts;
using System.Buffers;

namespace LibraDex.Views;

/// <summary>
/// Holds one mutable, disk-shaped `VV` shelf image plus decoded slot sidecars for session-local write hot paths.<br/>
/// The byte array remains authoritative for persistence and range reads; the sidecar arrays only avoid decoding 3-byte offsets and prefixes again for every insert into the same shelf.<br/>
/// This type is intentionally shelf-local and non-persisted so the on-disk format stays identical to `VarKeyVarIdentityLayout` version 4.<br/>
/// </summary>
internal sealed class VarKeyVarIdentityMutableShelf
{
    private readonly int[] recordOffsets;
    private readonly uint[] keyPrefixes;
    private readonly bool ownsBytes;
    private readonly bool ownsSidecars;
    private int itemCount;
    private int slotStreamLength;
    private int recordArenaEnd;
    private bool released;

    private VarKeyVarIdentityMutableShelf(
        byte[] bytes,
        VarKeyVarIdentityProfile profile,
        int[] recordOffsets,
        uint[] keyPrefixes,
        bool ownsBytes,
        bool ownsSidecars,
        int itemCount,
        int slotStreamLength,
        int slotCapacityBytes,
        int recordArenaEnd)
    {
        Bytes = bytes;
        Profile = profile;
        this.recordOffsets = recordOffsets;
        this.keyPrefixes = keyPrefixes;
        this.ownsBytes = ownsBytes;
        this.ownsSidecars = ownsSidecars;
        this.itemCount = itemCount;
        this.slotStreamLength = slotStreamLength;
        SlotCapacityBytes = slotCapacityBytes;
        this.recordArenaEnd = recordArenaEnd;
    }

    /// <summary>
    /// Gets the authoritative runtime shelf bytes that will be written back to the DataKernel when the session publishes the shelf.<br/>
    /// Mutations update this array in the same layout used on disk, including the fixed-width slot array and forward-growing record arena.<br/>
    /// </summary>
    public byte[] Bytes { get; }

    /// <summary>
    /// Gets the shelf profile used to validate capacity, maximum key length, and physical extent size for this mutable shelf.<br/>
    /// The profile is derived from the shelf header when existing bytes are loaded from the file.<br/>
    /// </summary>
    public VarKeyVarIdentityProfile Profile { get; }

    /// <summary>
    /// Gets the number of sorted tuple slots currently stored in the shelf.<br/>
    /// This mirrors the persisted header value and is updated after successful insert mutations.<br/>
    /// </summary>
    public int ItemCount => itemCount;

    /// <summary>
    /// Gets the reserved persisted slot-array byte capacity for this shelf extent.<br/>
    /// Inserts may shift bytes only inside this reserved region; when it is exhausted the caller must grow or split the shelf.<br/>
    /// </summary>
    public int SlotCapacityBytes { get; }

    /// <summary>
    /// Gets whether this mutable shelf has changed since it was loaded into the session-local sidecar cache.<br/>
    /// Clean sidecars may be retained during a batch to avoid repeated decode work, but only dirty sidecars should be flushed to DataKernel staging.<br/>
    /// </summary>
    public bool IsDirty { get; private set; }

    /// <summary>
    /// Decodes an existing `VV` shelf into a mutable sidecar while keeping the supplied byte array as the authoritative image.<br/>
    /// The sidecar arrays are sized to the reserved slot capacity instead of the current item count, so ordinary inserts only shift existing entries and do not reallocate slot metadata.<br/>
    /// </summary>
    /// <param name="bytes">The disk-shaped shelf byte image to own for mutation.</param>
    /// <param name="profile">The `VV` shelf profile expected by the caller.</param>
    /// <param name="ownsBytes">Whether the created mutable shelf should return <paramref name="bytes"/> to the shared byte pool when released.</param>
    /// <param name="rentSidecars">Whether decoded slot sidecars should be rented from shared pools instead of allocated as ordinary arrays.</param>
    /// <param name="shelf">The decoded mutable shelf when validation succeeds.</param>
    /// <returns>`true` when the byte image is a valid `VV` shelf for <paramref name="profile"/>.</returns>
    public static bool TryCreate(
        byte[] bytes,
        VarKeyVarIdentityProfile profile,
        bool ownsBytes,
        bool rentSidecars,
        out VarKeyVarIdentityMutableShelf shelf)
    {
        shelf = null!;
        if (bytes.Length < profile.ShelfExtentSize ||
            VarKeyVarIdentityLayout.ReadMagic(bytes) != VarKeyVarIdentityLayout.Magic ||
            VarKeyVarIdentityLayout.ReadFormatVersion(bytes) != VarKeyVarIdentityLayout.FormatVersion ||
            VarKeyVarIdentityLayout.ReadHeaderSize(bytes) != VarKeyVarIdentityLayout.HeaderSize ||
            VarKeyVarIdentityLayout.ReadShelfExtentSize(bytes) != profile.ShelfExtentSize)
        {
            if (ownsBytes)
            {
                ArrayPool<byte>.Shared.Return(bytes, clearArray: true);
            }

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
            if (ownsBytes)
            {
                ArrayPool<byte>.Shared.Return(bytes, clearArray: true);
            }

            return false;
        }

        int slotCapacity = slotCapacityBytes / VarKeyVarIdentityLayout.SlotSize;
        int[] offsets = rentSidecars ? ArrayPool<int>.Shared.Rent(slotCapacity) : new int[slotCapacity];
        uint[] prefixes = rentSidecars ? ArrayPool<uint>.Shared.Rent(slotCapacity) : new uint[slotCapacity];
        int cursor = VarKeyVarIdentityLayout.HeaderSize;
        for (int i = 0; i < count; i++)
        {
            int offset = VarKeyVarIdentityLayout.ReadSlotRecordOffset(bytes, cursor);
            if (offset < recordArenaStart || offset >= recordArenaEnd)
            {
                ReleaseFailedDecodeBuffers(bytes, offsets, prefixes, ownsBytes, rentSidecars);
                return false;
            }

            int recordLength = VarKeyVarIdentityLayout.GetRecordLength(bytes, offset);
            if (recordLength <= 0 || offset + recordLength > recordArenaEnd)
            {
                ReleaseFailedDecodeBuffers(bytes, offsets, prefixes, ownsBytes, rentSidecars);
                return false;
            }

            offsets[i] = offset;
            prefixes[i] = VarKeyVarIdentityLayout.ReadSlotKeyPrefix(bytes, cursor);
            cursor += VarKeyVarIdentityLayout.SlotSize;
        }

        shelf = new VarKeyVarIdentityMutableShelf(
            bytes,
            profile,
            offsets,
            prefixes,
            ownsBytes,
            rentSidecars,
            count,
            slotStreamLength,
            slotCapacityBytes,
            recordArenaEnd);
        return true;
    }

    /// <summary>
    /// Returns rented shelf and sidecar buffers owned by this mutable view to their shared pools.<br/>
    /// Callers must release only after the authoritative shelf bytes have either been copied into DataKernel staging or deliberately discarded by batch abort or structural replacement.<br/>
    /// The method is idempotent so structural paths and cleanup paths can safely converge without double-returning arrays.<br/>
    /// </summary>
    /// <param name="clearShelfBytes">Whether the rented shelf byte buffer should be cleared before returning it to the shared pool.</param>
    public void Release(bool clearShelfBytes)
    {
        if (released)
        {
            return;
        }

        released = true;
        if (ownsSidecars)
        {
            ArrayPool<int>.Shared.Return(recordOffsets, clearArray: false);
            ArrayPool<uint>.Shared.Return(keyPrefixes, clearArray: false);
        }

        if (ownsBytes)
        {
            ArrayPool<byte>.Shared.Return(Bytes, clearArray: clearShelfBytes);
        }
    }

    /// <summary>
    /// Inserts one raw key and raw variable-length identity into the owned shelf image when reserved slot and record capacity are still available.<br/>
    /// The slot bytes and decoded sidecar arrays are shifted together, while the new record is appended at the current record arena end.<br/>
    /// Full results are non-mutating and tell the caller to use the existing grow or split path with the current authoritative bytes.<br/>
    /// </summary>
    /// <param name="key">The raw byte key to insert.</param>
    /// <param name="identity">The raw byte identity to insert.</param>
    /// <param name="allowDuplicateKeys">Whether duplicate keys with different identities are allowed.</param>
    /// <param name="hintStartDepth">The first key byte depth to compare for route-local mutation evidence.</param>
    /// <param name="maxHintBytes">The maximum number of bytes to compare against adjacent keys.</param>
    /// <param name="mutationHint">The bounded adjacent-key observation produced during insert classification.</param>
    /// <returns>The insert result.</returns>
    public VarKeyVarIdentityInsertResult InsertWithMutationHint(
        ReadOnlySpan<byte> key,
        ReadOnlySpan<byte> identity,
        bool allowDuplicateKeys,
        int hintStartDepth,
        int maxHintBytes,
        out VarKeyVarIdentityMutationHint mutationHint)
    {
        mutationHint = default;
        if (key.Length <= 0 || key.Length > Profile.MaxKeyLength || identity.Length <= 0 || identity.Length > Profile.MaxIdentityLength)
        {
            return VarKeyVarIdentityInsertResult.Invalid;
        }

        int insertIndex = LowerBound(key, identity);
        if (insertIndex < itemCount && CompareSlotTuple(insertIndex, VarKeyVarIdentityLayout.CreateKeyPrefix(key), key, identity) == 0)
        {
            return VarKeyVarIdentityInsertResult.AlreadyPresent;
        }

        if (!allowDuplicateKeys)
        {
            int keyIndex = LowerBoundKey(key);
            if (keyIndex < itemCount && ReadKeyAt(keyIndex).SequenceEqual(key))
            {
                return VarKeyVarIdentityInsertResult.KeyConflict;
            }
        }

        mutationHint = CreateMutationHint(insertIndex, key, hintStartDepth, maxHintBytes);
        if (slotStreamLength + VarKeyVarIdentityLayout.SlotSize > SlotCapacityBytes)
        {
            return VarKeyVarIdentityInsertResult.Full;
        }

        int recordLength = VarKeyVarIdentityLayout.GetNewRecordLength(key.Length, identity.Length);
        int recordOffset = recordArenaEnd;
        int newRecordArenaEnd = recordOffset + recordLength;
        if (newRecordArenaEnd > Profile.ShelfExtentSize)
        {
            return VarKeyVarIdentityInsertResult.Full;
        }

        int slotOffset = VarKeyVarIdentityLayout.HeaderSize + checked(insertIndex * VarKeyVarIdentityLayout.SlotSize);
        int slotTailLength = slotStreamLength - checked(insertIndex * VarKeyVarIdentityLayout.SlotSize);
        if (slotTailLength < 0)
        {
            return VarKeyVarIdentityInsertResult.Invalid;
        }

        if (slotTailLength > 0)
        {
            Bytes.AsSpan(slotOffset, slotTailLength).CopyTo(Bytes.AsSpan(slotOffset + VarKeyVarIdentityLayout.SlotSize, slotTailLength));
            Array.Copy(recordOffsets, insertIndex, recordOffsets, insertIndex + 1, itemCount - insertIndex);
            Array.Copy(keyPrefixes, insertIndex, keyPrefixes, insertIndex + 1, itemCount - insertIndex);
        }

        uint keyPrefix = VarKeyVarIdentityLayout.CreateKeyPrefix(key);
        VarKeyVarIdentityLayout.WriteRecord(Bytes, recordOffset, key, identity);
        VarKeyVarIdentityLayout.WriteSlotRecordOffset(Bytes, slotOffset, recordOffset);
        VarKeyVarIdentityLayout.WriteSlotKeyPrefix(Bytes, slotOffset, keyPrefix);
        recordOffsets[insertIndex] = recordOffset;
        keyPrefixes[insertIndex] = keyPrefix;
        itemCount++;
        slotStreamLength += VarKeyVarIdentityLayout.SlotSize;
        recordArenaEnd = newRecordArenaEnd;
        VarKeyVarIdentityLayout.WriteItemCount(Bytes, itemCount);
        VarKeyVarIdentityLayout.WriteSlotStreamLength(Bytes, slotStreamLength);
        VarKeyVarIdentityLayout.WriteRecordArenaEnd(Bytes, recordArenaEnd);
        IsDirty = true;
        return VarKeyVarIdentityInsertResult.Inserted;
    }

    private int LowerBoundKey(ReadOnlySpan<byte> key)
    {
        uint prefix = VarKeyVarIdentityLayout.CreateKeyPrefix(key);
        int low = 0;
        int high = itemCount;
        while (low < high)
        {
            int middle = low + ((high - low) >> 1);
            int comparison = CompareSlotKey(middle, prefix, key);
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

    private int LowerBound(ReadOnlySpan<byte> key, ReadOnlySpan<byte> identity)
    {
        uint prefix = VarKeyVarIdentityLayout.CreateKeyPrefix(key);
        int low = 0;
        int high = itemCount;
        while (low < high)
        {
            int middle = low + ((high - low) >> 1);
            int comparison = CompareSlotTuple(middle, prefix, key, identity);
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

    /// <summary>
    /// Reads the raw key bytes for an existing sorted slot without allocating or decoding a second shelf view.<br/>
    /// Structural split paths use this to consume the already-decoded mutable shelf sidecars while keeping the byte image authoritative.<br/>
    /// </summary>
    /// <param name="slotIndex">The sorted slot index to read.</param>
    /// <returns>The raw key bytes stored at <paramref name="slotIndex"/>.</returns>
    internal ReadOnlySpan<byte> ReadKeyAt(int slotIndex)
    {
        return VarKeyVarIdentityLayout.ReadKey(Bytes, recordOffsets[slotIndex]);
    }

    /// <summary>
    /// Reads the byte offset and length of a key payload for an existing sorted slot without allocating.<br/>
    /// Split builders use this once per source tuple so later boundary scans can index directly into the authoritative shelf bytes.<br/>
    /// </summary>
    /// <param name="slotIndex">The sorted slot index to inspect.</param>
    /// <param name="keyOffset">The byte offset where the raw key payload begins.</param>
    /// <param name="keyLength">The raw key length in bytes.</param>
    internal void ReadKeyLocationAt(int slotIndex, out int keyOffset, out int keyLength)
    {
        int cursor = recordOffsets[slotIndex];
        keyLength = VarKeyVarIdentityLayout.ReadVarUInt32(Bytes, ref cursor);
        keyOffset = cursor;
    }

    /// <summary>
    /// Reads the raw variable-length identity for an existing sorted slot without allocating or cloning key data.<br/>
    /// This is intended for structural transforms that need to rebuild shelves from the mutable sidecar source order.<br/>
    /// </summary>
    /// <param name="slotIndex">The sorted slot index to read.</param>
    /// <returns>The raw identity stored at <paramref name="slotIndex"/>.</returns>
    internal ReadOnlySpan<byte> ReadIdentityAt(int slotIndex)
    {
        return VarKeyVarIdentityLayout.ReadIdentity(Bytes, recordOffsets[slotIndex], out _);
    }

    /// <summary>
    /// Reads the byte offset and length of an identity payload for an existing sorted slot without allocating.<br/>
    /// Split builders use this to rebuild replacement shelves directly from the authoritative shelf bytes while preserving raw identity ordering.<br/>
    /// </summary>
    /// <param name="slotIndex">The sorted slot index to inspect.</param>
    /// <param name="identityOffset">The byte offset where the raw identity payload begins.</param>
    /// <param name="identityLength">The raw identity length in bytes.</param>
    internal void ReadIdentityLocationAt(int slotIndex, out int identityOffset, out int identityLength)
    {
        int cursor = recordOffsets[slotIndex];
        int keyLength = VarKeyVarIdentityLayout.ReadVarUInt32(Bytes, ref cursor);
        cursor += keyLength;
        identityLength = VarKeyVarIdentityLayout.ReadVarUInt32(Bytes, ref cursor);
        identityOffset = cursor;
    }

    private VarKeyVarIdentityMutationHint CreateMutationHint(
        int insertIndex,
        ReadOnlySpan<byte> key,
        int hintStartDepth,
        int maxHintBytes)
    {
        if (maxHintBytes <= 0 || hintStartDepth < 0)
        {
            return default;
        }

        int compared = 0;
        int maxCommonBytes = 0;
        if (insertIndex > 0)
        {
            compared++;
            maxCommonBytes = Math.Max(maxCommonBytes, CountCommonPrefixBytes(ReadKeyAt(insertIndex - 1), key, hintStartDepth, maxHintBytes));
        }

        if (insertIndex < itemCount)
        {
            compared++;
            maxCommonBytes = Math.Max(maxCommonBytes, CountCommonPrefixBytes(ReadKeyAt(insertIndex), key, hintStartDepth, maxHintBytes));
        }

        return compared == 0
            ? default
            : new VarKeyVarIdentityMutationHint(
                Sampled: true,
                ComparedNeighborCount: compared,
                StartDepth: hintStartDepth,
                MaxCommonPrefixDepth: hintStartDepth + maxCommonBytes,
                MaxCommonPrefixBytes: maxCommonBytes);
    }

    private static int CountCommonPrefixBytes(
        ReadOnlySpan<byte> leftKey,
        ReadOnlySpan<byte> rightKey,
        int startDepth,
        int maxBytes)
    {
        int compared = 0;
        int leftRemaining = Math.Max(0, leftKey.Length - startDepth);
        int rightRemaining = Math.Max(0, rightKey.Length - startDepth);
        int limit = Math.Min(maxBytes, Math.Min(leftRemaining, rightRemaining));
        while (compared < limit && leftKey[startDepth + compared] == rightKey[startDepth + compared])
        {
            compared++;
        }

        return compared;
    }

    private int CompareSlotKey(int slotIndex, uint prefix, ReadOnlySpan<byte> key)
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

        return VarKeyVarIdentityLayout.CompareRecordKey(Bytes, recordOffsets[slotIndex], key);
    }

    private int CompareSlotTuple(int slotIndex, uint prefix, ReadOnlySpan<byte> key, ReadOnlySpan<byte> identity)
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

        return VarKeyVarIdentityLayout.CompareRecordTuple(Bytes, recordOffsets[slotIndex], key, identity);
    }

    private static void ReleaseFailedDecodeBuffers(
        byte[] bytes,
        int[] offsets,
        uint[] prefixes,
        bool ownsBytes,
        bool ownsSidecars)
    {
        if (ownsSidecars)
        {
            ArrayPool<int>.Shared.Return(offsets, clearArray: false);
            ArrayPool<uint>.Shared.Return(prefixes, clearArray: false);
        }

        if (ownsBytes)
        {
            ArrayPool<byte>.Shared.Return(bytes, clearArray: true);
        }
    }
}
