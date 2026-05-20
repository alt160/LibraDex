using LibraDex.Layouts;

namespace LibraDex.Views;

/// <summary>
/// Provides a writable hot-path view over fixed index-directory bytes.<br/>
/// The writer exposes fixed-slot mutation helpers without owning bytes or performing file I/O.<br/>
/// </summary>
internal ref struct IndexDirectoryWriter
{
    private Span<byte> bytes;

    public IndexDirectoryWriter(Span<byte> bytes)
    {
        this.bytes = bytes;
    }

    public void InitializeEmpty()
    {
        bytes.Clear();
    }

    /// <summary>
    /// Writes a fixed index-directory slot from a cacheable slot snapshot.<br/>
    /// The slot is cleared before field writes so removed or shortened fixed-width strings cannot leave stale bytes.<br/>
    /// </summary>
    /// <param name="slot">The slot values to persist.</param>
    public void WriteSlot(IndexDirectorySlotSnapshot slot)
    {
        Span<byte> target = IndexDirectoryLayout.GetSlot(bytes, slot.SlotIndex);
        target.Clear();
        IndexDirectoryLayout.WriteState(target, slot.State);
        IndexDirectoryLayout.WriteFlags(target, slot.Flags);
        IndexDirectoryLayout.WriteRootRouterOffset(target, slot.RootRouterOffset);
        IndexDirectoryLayout.WriteMetadataOffset(target, slot.MetadataOffset);
        IndexDirectoryLayout.WriteItemCount(target, slot.ItemCount);
        IndexDirectoryLayout.WriteGeneration(target, slot.Generation);
        IndexDirectoryLayout.WriteKeyProfileId(target, slot.KeyProfileId);
        IndexDirectoryLayout.WriteIdentityProfileId(target, slot.IdentityProfileId);
        IndexDirectoryLayout.WriteRouterProfileId(target, slot.RouterProfileId);
        IndexDirectoryLayout.WriteAllocationClassId(target, slot.AllocationClassId);
        IndexDirectoryLayout.WriteName(target, slot.Name);
    }
}
