namespace LibraDex;

/// <summary>
/// Carries the runtime location and sizing profile for a single-shelf `FixedNScalar8` index.<br/>
/// This is the first durable bridge for programmable fixed-key BigInt storage; routed FSN storage can widen this handle later without changing public BigInt syntax.<br/>
/// </summary>
/// <param name="RootOffset">The durable offset of the current fixed-key root, either a router or a single bridge shelf.</param>
/// <param name="Profile">The programmable fixed-key shelf profile.</param>
/// <param name="IsRouted">Whether <paramref name="RootOffset"/> points at a root router instead of a direct shelf.</param>
internal readonly record struct FixedNScalar8IndexHandle(long RootOffset, FixedNScalar8Profile Profile, bool IsRouted)
{
    /// <summary>
    /// Validates the handle before it is used by public index facades.<br/>
    /// The shelf offset must point at a durable DataKernel extent and the profile must be able to store at least one tuple.<br/>
    /// </summary>
    public void Validate()
    {
        if (RootOffset <= 0)
        {
            throw new InvalidDataException("The FSN-8 index handle has no root offset.");
        }

        if (Profile.MaxItemCount == 0)
        {
            throw new InvalidDataException("The FSN-8 shelf index handle has no item capacity.");
        }
    }
}
