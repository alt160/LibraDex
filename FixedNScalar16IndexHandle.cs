namespace LibraDex;

/// <summary>
/// Carries the runtime location and sizing profile for a `FixedNScalar16` index.<br/>
/// This first handle points at a durable single shelf; routed `FSN-16` can widen it later without changing the fixed BigInt public syntax.<br/>
/// </summary>
/// <param name="RootOffset">The durable offset of the current fixed-key root, either a router or a single bridge shelf.</param>
/// <param name="Profile">The programmable fixed-key shelf profile.</param>
/// <param name="IsRouted">Whether <paramref name="RootOffset"/> points at a root router instead of a direct shelf.</param>
internal readonly record struct FixedNScalar16IndexHandle(long RootOffset, FixedNScalar16Profile Profile, bool IsRouted)
{
    /// <summary>
    /// Validates the handle before it is used by public index facades.<br/>
    /// The shelf offset must point at a durable DataKernel extent and the profile must be able to store at least one tuple.<br/>
    /// </summary>
    public void Validate()
    {
        if (RootOffset <= 0)
        {
            throw new InvalidDataException("The FSN-16 index handle has no root offset.");
        }

        if (Profile.MaxItemCount == 0)
        {
            throw new InvalidDataException("The FSN-16 index handle has no item capacity.");
        }
    }
}
