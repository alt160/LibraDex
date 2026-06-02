namespace LibraDex;

/// <summary>
/// Carries the runtime location and sizing profile for a routed fixed-key / variable-identity `FV` index.<br/>
/// </summary>
/// <param name="RootOffset">The durable root offset, normally a root router.</param>
/// <param name="Profile">The programmable fixed-key / variable-identity shelf profile.</param>
/// <param name="IsRouted">Whether <paramref name="RootOffset"/> points at a router instead of a direct shelf.</param>
internal readonly record struct FixedNVarIdentityIndexHandle(long RootOffset, FixedNVarIdentityProfile Profile, bool IsRouted)
{
    /// <summary>
    /// Validates that this handle has a durable root and a usable `FV` profile.<br/>
    /// </summary>
    public void Validate()
    {
        if (RootOffset <= 0)
        {
            throw new InvalidDataException("The FV index handle has no root offset.");
        }

        if (Profile.MaxItemCount == 0)
        {
            throw new InvalidDataException("The FV index handle has no item capacity.");
        }
    }
}
