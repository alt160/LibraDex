using LibraDex.Layouts;

namespace LibraDex;

internal readonly record struct VarKeyScalar8Profile(
    int ShelfExtentSize,
    int MaxKeyLength)
{
    public static readonly VarKeyScalar8Profile Default16KiB = Create(16 * 1024, 1024);
    public static readonly VarKeyScalar8Profile Default32KiB = Create(32 * 1024, 1024);
    public static readonly VarKeyScalar8Profile Default64KiB = Create(64 * 1024, 1024);
    public static readonly VarKeyScalar8Profile Default128KiB = Create(128 * 1024, 1024);
    public static readonly VarKeyScalar8Profile DefaultInitial = Default64KiB;

    public static VarKeyScalar8Profile Create(int shelfExtentSize, int maxKeyLength)
    {
        if (shelfExtentSize < VarKeyScalar8Layout.HeaderSize + 32)
        {
            throw new ArgumentOutOfRangeException(nameof(shelfExtentSize), shelfExtentSize, "The VS8 shelf extent is too small for the header and at least one record.");
        }

        if (maxKeyLength <= 0 || maxKeyLength > 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(maxKeyLength), maxKeyLength, "The first VS8 profile supports raw byte keys from 1 to 1024 bytes.");
        }

        return new VarKeyScalar8Profile(shelfExtentSize, maxKeyLength);
    }

    public VarKeyScalar8Profile NextGrowthClass()
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
