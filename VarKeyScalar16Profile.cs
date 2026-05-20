using LibraDex.Layouts;

namespace LibraDex;

internal readonly record struct VarKeyScalar16Profile(
    int ShelfExtentSize,
    int MaxKeyLength)
{
    public static readonly VarKeyScalar16Profile Default16KiB = Create(16 * 1024, 1024);
    public static readonly VarKeyScalar16Profile Default32KiB = Create(32 * 1024, 1024);
    public static readonly VarKeyScalar16Profile Default64KiB = Create(64 * 1024, 1024);
    public static readonly VarKeyScalar16Profile Default128KiB = Create(128 * 1024, 1024);
    public static readonly VarKeyScalar16Profile DefaultInitial = Default64KiB;

    public static VarKeyScalar16Profile Create(int shelfExtentSize, int maxKeyLength)
    {
        if (shelfExtentSize < VarKeyScalar16Layout.HeaderSize + 32)
        {
            throw new ArgumentOutOfRangeException(nameof(shelfExtentSize), shelfExtentSize, "The VS16 shelf extent is too small for the header and at least one record.");
        }

        if (maxKeyLength <= 0 || maxKeyLength > 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(maxKeyLength), maxKeyLength, "The first VS16 profile supports raw byte keys from 1 to 1024 bytes.");
        }

        return new VarKeyScalar16Profile(shelfExtentSize, maxKeyLength);
    }

    public VarKeyScalar16Profile NextGrowthClass()
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
        return Create(nextSize, MaxKeyLength);
    }
}
