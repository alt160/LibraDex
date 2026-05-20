using LibraDex.Layouts;

namespace LibraDex.Views;

/// <summary>
/// Provides a writable hot-path view over superblock bytes.<br/>
/// The view does not own bytes and performs no file I/O or allocation.<br/>
/// </summary>
internal ref struct SuperblockWriter
{
    private Span<byte> bytes;

    public SuperblockWriter(Span<byte> bytes)
    {
        this.bytes = bytes;
    }

    public void Initialize(long reservedPrefixBytes, long indexDirectoryOffset)
    {
        Initialize(
            reservedPrefixBytes,
            indexDirectoryOffset,
            Guid.NewGuid(),
            DateTimeOffset.UtcNow.UtcTicks,
            default);
    }

    public void Initialize(
        long reservedPrefixBytes,
        long indexDirectoryOffset,
        Guid fileGuid,
        long createdUtcTicks,
        SuperblockDeveloperMetadata developerMetadata)
    {
        bytes.Clear();
        SuperblockLayout.WriteMagic(bytes, SuperblockLayout.Magic);
        SuperblockLayout.WriteFormatVersion(bytes, SuperblockLayout.FormatVersion);
        SuperblockLayout.WriteHeaderSize(bytes, SuperblockLayout.Size);
        SuperblockLayout.WriteFlags(bytes, 0);
        SuperblockLayout.WriteReservedPrefixBytes(bytes, reservedPrefixBytes);
        SuperblockLayout.WriteIndexDirectoryOffset(bytes, indexDirectoryOffset);
        SuperblockLayout.WriteIndexDirectoryLength(bytes, IndexDirectoryLayout.Size);
        SuperblockLayout.WriteIndexSlotCount(bytes, IndexDirectoryLayout.SlotCount);
        SuperblockLayout.WriteFileGuid(bytes, fileGuid);
        SuperblockLayout.WriteCreatedUtcTicks(bytes, createdUtcTicks);
        WriteDeveloperMetadata(developerMetadata);
    }

    public void WriteDeveloperMetadata(SuperblockDeveloperMetadata developerMetadata)
    {
        SuperblockLayout.WriteDevIdentity(bytes, developerMetadata.DevIdentity);
        SuperblockLayout.WriteDevCustomText(bytes, developerMetadata.DevCustomText);
        SuperblockLayout.WriteDevGuid(bytes, developerMetadata.DevGuid);
        SuperblockLayout.WriteDevDate1UtcTicks(bytes, developerMetadata.DevDate1UtcTicks);
        SuperblockLayout.WriteDevDate2UtcTicks(bytes, developerMetadata.DevDate2UtcTicks);
        SuperblockLayout.WriteDevNumber(bytes, developerMetadata.DevNumber);
    }
}
