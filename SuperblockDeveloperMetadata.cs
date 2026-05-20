namespace LibraDex;

/// <summary>
/// Provides optional developer-owned metadata stored in the superblock.<br/>
/// LibraDex stores these values durably but does not interpret them.<br/>
/// </summary>
/// <param name="DevIdentity">A short developer/application identity string.</param>
/// <param name="DevCustomText">Additional developer/application text.</param>
/// <param name="DevGuid">A developer/application GUID.</param>
/// <param name="DevDate1UtcTicks">The first developer/application UTC tick value, or zero when unset.</param>
/// <param name="DevDate2UtcTicks">The second developer/application UTC tick value, or zero when unset.</param>
/// <param name="DevNumber">One raw 8-byte developer/application numeric value.</param>
public readonly record struct SuperblockDeveloperMetadata(
    string DevIdentity,
    string DevCustomText,
    Guid DevGuid,
    long DevDate1UtcTicks,
    long DevDate2UtcTicks,
    ulong DevNumber);
