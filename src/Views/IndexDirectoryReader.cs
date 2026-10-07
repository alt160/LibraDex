using LibraDex.Layouts;

namespace LibraDex.Views;

/// <summary>
/// Provides a read-only hot-path view over fixed index-directory bytes.<br/>
/// The reader exposes fixed-slot fields without owning bytes or allocating for numeric fields.<br/>
/// </summary>
internal readonly ref struct IndexDirectoryReader
{
    private readonly ReadOnlySpan<byte> bytes;

    public IndexDirectoryReader(ReadOnlySpan<byte> bytes)
    {
        this.bytes = bytes;
    }

    public bool AllSlotsEmpty
    {
        get
        {
            ReadOnlySpan<byte> localBytes = bytes;
            int slotCount = IndexDirectoryLayout.SlotCount;
            int slotSize = IndexDirectoryLayout.SlotSize;
            for (int i = 0; i < slotCount; i++)
            {
                if (localBytes[i * slotSize] != IndexDirectoryLayout.EmptyState)
                {
                    return false;
                }
            }

            return true;
        }
    }

    /// <summary>
    /// Gets the state byte for a fixed index-directory slot.<br/>
    /// The state byte determines whether the slot is empty, active, or reserved for future states.<br/>
    /// </summary>
    /// <param name="slotIndex">The zero-based slot index.</param>
    /// <returns>The persisted slot state byte.</returns>
    public byte GetState(int slotIndex)
    {
        return IndexDirectoryLayout.ReadState(IndexDirectoryLayout.GetSlot(bytes, slotIndex));
    }

    /// <summary>
    /// Materializes a fixed index-directory slot into a cacheable snapshot.<br/>
    /// This is intended for reopen/session caching, not per-item routing hot paths.<br/>
    /// </summary>
    /// <param name="slotIndex">The zero-based slot index.</param>
    /// <returns>A value snapshot of the requested slot.</returns>
    public IndexDirectorySlotSnapshot GetSlotSnapshot(int slotIndex)
    {
        ReadOnlySpan<byte> slot = IndexDirectoryLayout.GetSlot(bytes, slotIndex);
        return new IndexDirectorySlotSnapshot(
            SlotIndex: slotIndex,
            State: IndexDirectoryLayout.ReadState(slot),
            Flags: IndexDirectoryLayout.ReadFlags(slot),
            RootRouterOffset: IndexDirectoryLayout.ReadRootRouterOffset(slot),
            MetadataOffset: IndexDirectoryLayout.ReadMetadataOffset(slot),
            ItemCount: IndexDirectoryLayout.ReadItemCount(slot),
            Generation: IndexDirectoryLayout.ReadGeneration(slot),
            KeyProfileId: IndexDirectoryLayout.ReadKeyProfileId(slot),
            IdentityProfileId: IndexDirectoryLayout.ReadIdentityProfileId(slot),
            RouterProfileId: IndexDirectoryLayout.ReadRouterProfileId(slot),
            AllocationClassId: IndexDirectoryLayout.ReadAllocationClassId(slot),
            Name: IndexDirectoryLayout.ReadName(slot));
    }
}
