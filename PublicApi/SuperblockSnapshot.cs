using LibraDex.Views;

namespace LibraDex;

/// <summary>
/// Caches superblock fields after file initialization or open.<br/>
/// This is an outer model snapshot and is not used as a hot byte view.<br/>
/// </summary>
internal readonly record struct SuperblockSnapshot(
    Guid FileGuid,
    long CreatedUtcTicks,
    long ReservedPrefixBytes,
    long IndexDirectoryOffset,
    int IndexDirectoryLength,
    int IndexSlotCount,
    long AllocationDirectoryOffset,
    SuperblockDeveloperMetadata DeveloperMetadata)
{
    internal static SuperblockSnapshot FromReader(SuperblockReader reader)
    {
        SuperblockDeveloperMetadata developerMetadata = new(
            reader.DevIdentity,
            reader.DevCustomText,
            reader.DevGuid,
            reader.DevDate1UtcTicks,
            reader.DevDate2UtcTicks,
            reader.DevNumberUInt64);

        return new SuperblockSnapshot(
            reader.FileGuid,
            reader.CreatedUtcTicks,
            reader.ReservedPrefixBytes,
            reader.IndexDirectoryOffset,
            reader.IndexDirectoryLength,
            reader.IndexSlotCount,
            reader.AllocationDirectoryOffset,
            developerMetadata);
    }
}
