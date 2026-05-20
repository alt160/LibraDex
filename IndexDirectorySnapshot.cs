using LibraDex.Layouts;
using LibraDex.Views;

namespace LibraDex;

/// <summary>
/// Caches fixed index-directory state after file initialization, open, or directory update.<br/>
/// The snapshot is an outer model and may allocate arrays because it is not a hot byte view.<br/>
/// </summary>
public sealed class IndexDirectorySnapshot
{
    private readonly IndexDirectorySlotSnapshot[] activeSlots;

    private IndexDirectorySnapshot(bool allSlotsEmpty, IndexDirectorySlotSnapshot[] activeSlots)
    {
        AllSlotsEmpty = allSlotsEmpty;
        this.activeSlots = activeSlots;
    }

    public bool AllSlotsEmpty { get; }

    public ReadOnlySpan<IndexDirectorySlotSnapshot> ActiveSlots => activeSlots;

    internal static IndexDirectorySnapshot FromBytes(ReadOnlySpan<byte> bytes)
    {
        IndexDirectoryReader reader = new(bytes);
        int slotCount = IndexDirectoryLayout.SlotCount;
        int activeCount = 0;
        for (int i = 0; i < slotCount; i++)
        {
            if (reader.GetState(i) == IndexDirectoryLayout.ActiveState)
            {
                activeCount++;
            }
        }

        IndexDirectorySlotSnapshot[] activeSlots = new IndexDirectorySlotSnapshot[activeCount];
        int activeIndex = 0;
        for (int i = 0; i < slotCount; i++)
        {
            if (reader.GetState(i) == IndexDirectoryLayout.ActiveState)
            {
                activeSlots[activeIndex] = reader.GetSlotSnapshot(i);
                activeIndex++;
            }
        }

        return new IndexDirectorySnapshot(activeCount == 0 && reader.AllSlotsEmpty, activeSlots);
    }

    public int SlotCount => IndexDirectoryLayout.SlotCount;

    public int SlotSize => IndexDirectoryLayout.SlotSize;
}
