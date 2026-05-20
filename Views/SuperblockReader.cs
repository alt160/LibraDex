using LibraDex.Layouts;

namespace LibraDex.Views;

/// <summary>
/// Provides a read-only hot-path view over superblock bytes.<br/>
/// The view does not own bytes and performs no file I/O or allocation.<br/>
/// </summary>
internal readonly ref struct SuperblockReader
{
    private readonly ReadOnlySpan<byte> bytes;

    public SuperblockReader(ReadOnlySpan<byte> bytes)
    {
        this.bytes = bytes;
    }

    public ulong Magic => SuperblockLayout.ReadMagic(bytes);

    public ushort FormatVersion => SuperblockLayout.ReadFormatVersion(bytes);

    public ushort HeaderSize => SuperblockLayout.ReadHeaderSize(bytes);

    public uint Flags => SuperblockLayout.ReadFlags(bytes);

    public long ReservedPrefixBytes => SuperblockLayout.ReadReservedPrefixBytes(bytes);

    public long IndexDirectoryOffset => SuperblockLayout.ReadIndexDirectoryOffset(bytes);

    public int IndexDirectoryLength => SuperblockLayout.ReadIndexDirectoryLength(bytes);

    public int IndexSlotCount => SuperblockLayout.ReadIndexSlotCount(bytes);

    public Guid FileGuid => SuperblockLayout.ReadFileGuid(bytes);

    public long CreatedUtcTicks => SuperblockLayout.ReadCreatedUtcTicks(bytes);

    public string DevIdentity => SuperblockLayout.ReadDevIdentity(bytes);

    public string DevCustomText => SuperblockLayout.ReadDevCustomText(bytes);

    public Guid DevGuid => SuperblockLayout.ReadDevGuid(bytes);

    public long DevDate1UtcTicks => SuperblockLayout.ReadDevDate1UtcTicks(bytes);

    public long DevDate2UtcTicks => SuperblockLayout.ReadDevDate2UtcTicks(bytes);

    public ulong DevNumberUInt64 => SuperblockLayout.ReadDevNumberUInt64(bytes);

    public long DevNumberInt64 => SuperblockLayout.ReadDevNumberInt64(bytes);

    public bool IsValid =>
        Magic == SuperblockLayout.Magic &&
        FormatVersion == SuperblockLayout.FormatVersion &&
        HeaderSize == SuperblockLayout.Size &&
        IndexDirectoryLength == IndexDirectoryLayout.Size &&
        IndexSlotCount == IndexDirectoryLayout.SlotCount;
}
