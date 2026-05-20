using LibraDex.Layouts;

namespace LibraDex;

internal readonly record struct Scalar8VarIdentityProfile(
    int ShelfExtentSize,
    int MaxIdentityLength)
{
    public static readonly Scalar8VarIdentityProfile Default16KiB = Create(16 * 1024, 1024);
    public static readonly Scalar8VarIdentityProfile Default32KiB = Create(32 * 1024, 1024);
    public static readonly Scalar8VarIdentityProfile Default64KiB = Create(64 * 1024, 1024);
    public static readonly Scalar8VarIdentityProfile Default128KiB = Create(128 * 1024, 1024);
    public static readonly Scalar8VarIdentityProfile DefaultInitial = Default64KiB;

    public static Scalar8VarIdentityProfile Create(int shelfExtentSize, int maxIdentityLength)
    {
        if (shelfExtentSize < Scalar8VarIdentityLayout.HeaderSize + 32)
        {
            throw new ArgumentOutOfRangeException(nameof(shelfExtentSize), shelfExtentSize, "The SV8 shelf extent is too small for the header and at least one record.");
        }

        if (maxIdentityLength <= 0 || maxIdentityLength > 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(maxIdentityLength), maxIdentityLength, "The first SV8 profile supports raw byte identities from 1 to 1024 bytes.");
        }

        return new Scalar8VarIdentityProfile(shelfExtentSize, maxIdentityLength);
    }

    public Scalar8VarIdentityProfile NextGrowthClass()
    {
        if (ShelfExtentSize >= Default128KiB.ShelfExtentSize)
        {
            return this;
        }

        int nextSize = ShelfExtentSize <= 16 * 1024
            ? 32 * 1024
            : ShelfExtentSize <= 32 * 1024
                ? 64 * 1024
                : 128 * 1024;
        return Create(nextSize, MaxIdentityLength);
    }
}
