using LibraDex.Layouts;

namespace LibraDex;

internal enum VarKeyVarIdentityInsertResult
{
    Inserted = 0,
    AlreadyPresent = 1,
    KeyConflict = 2,
    Full = 3,
    Invalid = 4
}

internal readonly record struct VarKeyVarIdentityMutationHint(
    bool Sampled,
    int ComparedNeighborCount,
    int StartDepth,
    int MaxCommonPrefixDepth,
    int MaxCommonPrefixBytes);

internal readonly record struct VarKeyVarIdentityProfile(
    int ShelfExtentSize,
    int MaxKeyLength,
    int MaxIdentityLength)
{
    public static readonly VarKeyVarIdentityProfile Default4KiB = Create(4 * 1024, 1024);
    public static readonly VarKeyVarIdentityProfile Default8KiB = Create(8 * 1024, 1024);
    public static readonly VarKeyVarIdentityProfile Default16KiB = Create(16 * 1024, 1024);
    public static readonly VarKeyVarIdentityProfile Default32KiB = Create(32 * 1024, 1024);
    public static readonly VarKeyVarIdentityProfile Default64KiB = Create(64 * 1024, 1024);
    public static readonly VarKeyVarIdentityProfile Default128KiB = Create(128 * 1024, 1024);
    public static readonly VarKeyVarIdentityProfile DefaultInitial = Default64KiB;

    public static VarKeyVarIdentityProfile Create(int shelfExtentSize, int maxKeyLength)
    {
        if (shelfExtentSize < VarKeyVarIdentityLayout.HeaderSize + 32)
        {
            throw new ArgumentOutOfRangeException(nameof(shelfExtentSize), shelfExtentSize, "The VV shelf extent is too small for the header and at least one record.");
        }

        if (maxKeyLength <= 0 || maxKeyLength > 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(maxKeyLength), maxKeyLength, "The first VV profile supports raw byte keys from 1 to 1024 bytes.");
        }

        return new VarKeyVarIdentityProfile(shelfExtentSize, maxKeyLength, 1024);
    }

    /// <summary>
    /// Creates a `VV` shelf profile with explicit maximum key and identity lengths.<br/>
    /// The first VV lane keeps both limits at the same 1..1024 byte contract used by the other varlen families for predictable developer experience.<br/>
    /// </summary>
    /// <param name="shelfExtentSize">The physical shelf extent size.</param>
    /// <param name="maxKeyLength">The maximum raw key length in bytes.</param>
    /// <param name="maxIdentityLength">The maximum raw identity length in bytes.</param>
    /// <returns>The validated VV profile.</returns>
    public static VarKeyVarIdentityProfile Create(int shelfExtentSize, int maxKeyLength, int maxIdentityLength)
    {
        _ = Create(shelfExtentSize, maxKeyLength);
        if (maxIdentityLength <= 0 || maxIdentityLength > 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(maxIdentityLength), maxIdentityLength, "The first VV profile supports raw byte identities from 1 to 1024 bytes.");
        }

        return new VarKeyVarIdentityProfile(shelfExtentSize, maxKeyLength, maxIdentityLength);
    }

    /// <summary>
    /// Selects the initial `VV` shelf profile from developer write-intent hints.<br/>
    /// Default intent preserves the current 64 KiB compatibility profile because concentrated write groups avoid avoidable growth extents with that start size.<br/>
    /// Broad or compactness-biased intent selects 32 KiB because measured broad root-prefix fanout had materially better bytes/item and write throughput without needing growth or transforms.<br/>
    /// The method is intentionally coarse: it chooses between validated profile anchors and leaves smaller or more aggressive policies to explicit profile knobs or later optimizer/repack work.<br/>
    /// </summary>
    /// <param name="writeIntent">The caller's coarse write-pattern and priority hints.</param>
    /// <param name="maxKeyLength">The maximum raw key length in bytes.</param>
    /// <param name="maxIdentityLength">The maximum raw identity length in bytes.</param>
    /// <returns>The selected initial `VV` profile.</returns>
    public static VarKeyVarIdentityProfile SelectInitial(LibraDexWriteIntent writeIntent, int maxKeyLength = 1024, int maxIdentityLength = 1024)
    {
        if (writeIntent.Priority == LibraDexWritePriority.Compactness ||
            writeIntent.Locality == LibraDexWriteLocality.Broad)
        {
            return Create(Default32KiB.ShelfExtentSize, maxKeyLength, maxIdentityLength);
        }

        return Create(DefaultInitial.ShelfExtentSize, maxKeyLength, maxIdentityLength);
    }

    public VarKeyVarIdentityProfile NextGrowthClass()
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
        return Create(nextSize, MaxKeyLength, MaxIdentityLength);
    }
}

internal enum VarKeyVarIdentityRouteTargetKind
{
    None = 0,
    Shelf = 1,
    Router = 2,
    TerminalVarIdentityRoot = 3
}

internal readonly record struct VarKeyVarIdentityRouteTarget(
    VarKeyVarIdentityRouteTargetKind Kind,
    long Offset,
    ushort RouterDepth,
    ushort AllocationClassId);

internal readonly record struct VarKeyVarIdentityRoutePathTarget(
    VarKeyVarIdentityRouteTarget Target,
    long ParentRouterOffset,
    byte PrefixByte,
    int RouteIndex);

internal enum VarKeyVarIdentityRoutedInsertKind
{
    Invalid = 0,
    NoOp = 1,
    KeyConflict = 2,
    WalkedNoSplit = 3,
    WalkedGrow = 4,
    WalkedShelfTransformSplit = 5,
    WalkedCreatedInitialShelf = 6,
    Full = 7,
    WalkedDuplicateRunOverflow = 8
}

internal readonly record struct VarKeyVarIdentityRoutedInsertResult(
    VarKeyVarIdentityRoutedInsertKind Kind,
    VarKeyVarIdentityInsertResult InsertResult,
    long SourceShelfOffset,
    long NewOffset,
    DataKernelCommitTelemetry Commit,
    int ItemCount,
    int ShelfExtentSize,
    ushort RouterDepth = 0,
    ushort StructuralRouterDepth = 0);
