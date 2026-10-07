namespace LibraDex;

/// <summary>
/// Describes the health state of one maintained projection discovered by catalog assessment.<br/>
/// </summary>
public enum LibraDexProjectionHealth
{
    /// <summary>
    /// Persisted projection intent references no active physical companion slot.<br/>
    /// </summary>
    Missing = 0,

    /// <summary>
    /// Persisted projection intent references an active companion slot that belongs to another index group.<br/>
    /// </summary>
    Inconsistent = 1
}

/// <summary>
/// Describes one index whose physical layout has enough attributable evidence to recommend repacking.<br/>
/// </summary>
/// <param name="Group">The owning identity group.<br/></param>
/// <param name="IndexName">The logical index name.<br/></param>
/// <param name="ReclaimableBytes">The exact persisted orphaned payload bytes LibraDex can attribute to this logical index and its maintained projections.<br/></param>
/// <param name="Reason">The evidence supporting the recommendation.<br/></param>
public readonly record struct LibraDexRepackCandidate(
    string Group,
    string IndexName,
    long ReclaimableBytes,
    string Reason);

/// <summary>
/// Attributes reclaimable physical payload to one component of a logical index.<br/>
/// Exact-forward storage is the primary component; maintained companion projections remain separately inspectable even though their bytes roll into the logical index's repack recommendation.<br/>
/// </summary>
/// <param name="Group">The owning identity group.<br/></param>
/// <param name="IndexName">The logical source index name.<br/></param>
/// <param name="PhysicalSlotIndex">The physical index-directory slot that owns the persisted shelves.<br/></param>
/// <param name="Projection">The exact, folded, or sort-key projection represented by the physical component.<br/></param>
/// <param name="Direction">The forward or reversed byte direction represented by the physical component.<br/></param>
/// <param name="ReclaimableBytes">The exact orphaned variable-record bytes persisted across the component's shelves.<br/></param>
/// <param name="ShelfCount">The number of variable-record shelves visited for this component.<br/></param>
public readonly record struct LibraDexReclaimComponent(
    string Group,
    string IndexName,
    int PhysicalSlotIndex,
    LibraDexIndexProjectionKind Projection,
    LibraDexIndexByteDirection Direction,
    long ReclaimableBytes,
    int ShelfCount);

/// <summary>
/// Describes one maintained projection requiring developer attention.<br/>
/// </summary>
/// <param name="Group">The owning identity group.<br/></param>
/// <param name="IndexName">The logical source index name.<br/></param>
/// <param name="Projection">The maintained projection kind.<br/></param>
/// <param name="Direction">The projection byte direction.<br/></param>
/// <param name="Health">The detected projection health state.<br/></param>
/// <param name="RecommendedAction">The developer-facing next action.<br/></param>
/// <param name="Reason">The persisted evidence supporting the issue.<br/></param>
public readonly record struct LibraDexProjectionIssue(
    string Group,
    string IndexName,
    LibraDexIndexProjectionKind Projection,
    LibraDexIndexByteDirection Direction,
    LibraDexProjectionHealth Health,
    string RecommendedAction,
    string Reason);

/// <summary>
/// Reports exact reachable storage for one active fixed-topology index generation.<br/>
/// The current generation can be attributed from its directory root; unreachable bytes from prior generations remain catalog-wide until allocation provenance exists.<br/>
/// </summary>
/// <param name="Group">The owning logical identity group.<br/></param>
/// <param name="IndexName">The owning logical index name.<br/></param>
/// <param name="PhysicalSlotIndex">The active physical directory slot assessed.<br/></param>
/// <param name="Generation">The active directory generation whose root was walked.<br/></param>
/// <param name="TupleCount">The exact live tuple count derived from reachable shelf headers.<br/></param>
/// <param name="ReachableBytes">The exact bytes occupied by this generation's distinct reachable routers, roots, and shelves.<br/></param>
/// <param name="RouterCount">The number of distinct reachable router pages, including the stable root.<br/></param>
/// <param name="OrdinaryShelfCount">The number of distinct reachable ordinary `SS8-8` shelves, including duplicate-run shelves.<br/></param>
/// <param name="DuplicateRunShelfCount">The subset of ordinary shelves participating in legacy duplicate-run chains.<br/></param>
/// <param name="TerminalRootCount">The number of distinct exact-key terminal identity roots.<br/></param>
/// <param name="TerminalShelfCount">The number of distinct terminal identity shelves.<br/></param>
/// <param name="ShelfExtentSize">The fixed ordinary and terminal shelf extent size selected by the directory profile.<br/></param>
public readonly record struct LibraDexFixedTopologyStorageComponent(
    string Group,
    string IndexName,
    int PhysicalSlotIndex,
    long Generation,
    long TupleCount,
    long ReachableBytes,
    int RouterCount,
    int OrdinaryShelfCount,
    int DuplicateRunShelfCount,
    int TerminalRootCount,
    int TerminalShelfCount,
    int ShelfExtentSize)
{
    /// <summary>
    /// Gets exact reachable bytes per live tuple, or zero for an empty generation.<br/>
    /// </summary>
    public double BytesPerLiveTuple => TupleCount == 0 ? 0 : (double)ReachableBytes / TupleCount;

    /// <summary>
    /// Gets per-index unreachable bytes when allocation ownership is known.<br/>
    /// The current append-only format does not tag obsolete extents with a logical owner, so this value is intentionally null rather than an estimate.<br/>
    /// </summary>
    public long? UnreachableBytes => null;
}

/// <summary>
/// Reports exact reachable storage for one active string-backed variable-key/scalar-8 identity generation.<br/>
/// Variable-key shelves may use different extent classes within one topology, so the component reports their exact byte sum rather than one nominal shelf size.<br/>
/// </summary>
/// <param name="Group">The owning logical identity group.<br/></param>
/// <param name="IndexName">The owning logical index name.<br/></param>
/// <param name="PhysicalSlotIndex">The active physical directory slot assessed.<br/></param>
/// <param name="Generation">The active directory generation whose ordinary and key-state roots were walked.<br/></param>
/// <param name="TupleCount">The exact live ordinary, null-key, and empty-key tuple count.<br/></param>
/// <param name="ReachableBytes">The exact bytes occupied by distinct reachable routers, roots, shelves, and key-state roots.<br/></param>
/// <param name="ReclaimablePayloadBytes">The exact orphaned variable-record payload bytes contained inside reachable ordinary shelves.<br/></param>
/// <param name="RouterCount">The number of distinct reachable router pages, including the stable root.<br/></param>
/// <param name="OrdinaryShelfCount">The number of distinct reachable ordinary `VS8` shelves, including duplicate-run shelves.<br/></param>
/// <param name="DuplicateRunShelfCount">The subset of ordinary shelves participating in duplicate-run chains.<br/></param>
/// <param name="TerminalRootCount">The number of distinct ordinary-key or promoted key-state terminal identity roots.<br/></param>
/// <param name="TerminalShelfCount">The number of distinct terminal scalar-8 identity shelves.<br/></param>
/// <param name="KeyStateRootCount">The number of reachable null/empty identity-route roots.<br/></param>
public readonly record struct LibraDexVariableTextTopologyStorageComponent(
    string Group,
    string IndexName,
    int PhysicalSlotIndex,
    long Generation,
    long TupleCount,
    long ReachableBytes,
    long ReclaimablePayloadBytes,
    int RouterCount,
    int OrdinaryShelfCount,
    int DuplicateRunShelfCount,
    int TerminalRootCount,
    int TerminalShelfCount,
    int KeyStateRootCount)
{
    /// <summary>
    /// Gets exact reachable bytes per live tuple, or zero for an empty generation.<br/>
    /// </summary>
    public double BytesPerLiveTuple => TupleCount == 0 ? 0 : (double)ReachableBytes / TupleCount;

    /// <summary>
    /// Gets per-index unreachable bytes when allocation ownership is known.<br/>
    /// Obsolete append extents currently carry no logical-owner tags, so this remains null rather than assigning catalog-wide unreachable bytes speculatively.<br/>
    /// </summary>
    public long? UnreachableBytes => null;
}

/// <summary>
/// Describes catalog physical size and the exact reachable subset understood by the current maintenance walkers.<br/>
/// Catalog-wide unreachable bytes and amplification are exposed only when every active physical slot and key-state route is understood.<br/>
/// </summary>
public sealed class LibraDexCatalogStorageAssessment
{
    internal LibraDexCatalogStorageAssessment(
        long? physicalBytes,
        long knownReachableBytes,
        long catalogOverheadReachableBytes,
        long allocatorOverheadReachableBytes,
        long reusableBytes,
        long? unreachableBytes,
        bool isComplete,
        int unsupportedIndexCount,
        IReadOnlyList<LibraDexFixedTopologyStorageComponent> fixedTopologyComponents,
        IReadOnlyList<LibraDexVariableTextTopologyStorageComponent> variableTextTopologyComponents)
    {
        PhysicalBytes = physicalBytes;
        KnownReachableBytes = knownReachableBytes;
        CatalogOverheadReachableBytes = catalogOverheadReachableBytes;
        AllocatorOverheadReachableBytes = allocatorOverheadReachableBytes;
        ReusableBytes = reusableBytes;
        UnreachableBytes = unreachableBytes;
        IsComplete = isComplete;
        UnsupportedIndexCount = unsupportedIndexCount;
        FixedTopologyComponents = fixedTopologyComponents;
        VariableTextTopologyComponents = variableTextTopologyComponents;
    }

    /// <summary>
    /// Gets the current file length, or null for a memory-backed catalog.<br/>
    /// </summary>
    public long? PhysicalBytes { get; }

    /// <summary>
    /// Gets the exact subtotal of reachable extents understood by this LibraDex build.<br/>
    /// When <see cref="IsComplete"/> is false, this is a lower bound and must not be subtracted from file length as though it covered unknown shapes.<br/>
    /// </summary>
    public long KnownReachableBytes { get; }

    /// <summary>
    /// Gets exact reachable bytes for the superblock, fixed directory, active catalog metadata, and durable allocator metadata.<br/>
    /// </summary>
    public long CatalogOverheadReachableBytes { get; }

    /// <summary>
    /// Gets the durable allocation directory and segment-header bytes included in <see cref="CatalogOverheadReachableBytes"/>.<br/>
    /// These bytes are live bounded allocation state rather than unreachable historical mutation data.<br/>
    /// </summary>
    public long AllocatorOverheadReachableBytes { get; }

    /// <summary>
    /// Gets exact materialized allocator payload bytes currently marked free and reusable by their homogeneous allocation classes.<br/>
    /// Reusable bytes remain part of physical file length but are excluded from <see cref="UnreachableBytes"/>.<br/>
    /// </summary>
    public long ReusableBytes { get; }

    /// <summary>
    /// Gets exact catalog-wide bytes that are neither active topology, live catalog/allocator metadata, nor reusable allocator capacity when assessment is complete.<br/>
    /// The value is null when any active physical topology remains unsupported.<br/>
    /// </summary>
    public long? UnreachableBytes { get; }

    /// <summary>
    /// Gets whether physical, catalog-overhead, active-index, and key-state reachability are all understood exactly.<br/>
    /// </summary>
    public bool IsComplete { get; }

    /// <summary>
    /// Gets the number of active physical index slots whose topology is not yet included in <see cref="KnownReachableBytes"/>.<br/>
    /// </summary>
    public int UnsupportedIndexCount { get; }

    /// <summary>
    /// Gets exact current-generation accounting for every supported active fixed topology.<br/>
    /// </summary>
    public IReadOnlyList<LibraDexFixedTopologyStorageComponent> FixedTopologyComponents { get; }

    /// <summary>
    /// Gets exact current-generation accounting for every supported active string-backed `VS8` topology and its scalar-8 key-state routes.<br/>
    /// </summary>
    public IReadOnlyList<LibraDexVariableTextTopologyStorageComponent> VariableTextTopologyComponents { get; }

    /// <summary>
    /// Gets physical-to-reachable storage amplification when assessment is complete, or null when either side is unavailable or reachability is incomplete.<br/>
    /// A value of one represents no unreachable physical amplification beyond the active catalog topology.<br/>
    /// </summary>
    public double? AmplificationRatio =>
        IsComplete && PhysicalBytes.HasValue && KnownReachableBytes > 0
            ? (double)PhysicalBytes.Value / KnownReachableBytes
            : null;
}

/// <summary>
/// Represents one disconnected catalog maintenance assessment.<br/>
/// Empty candidate collections mean no supported issue was found; evidence flags distinguish that from a physical assessment LibraDex cannot yet perform authoritatively.<br/>
/// </summary>
public sealed class LibraDexMaintenanceAssessment
{
    internal LibraDexMaintenanceAssessment(
        long minimumReclaimableBytes,
        IReadOnlyList<LibraDexRepackCandidate> repackCandidates,
        IReadOnlyList<LibraDexReclaimComponent> reclaimComponents,
        IReadOnlyList<LibraDexProjectionIssue> projectionIssues,
        LibraDexCatalogStorageAssessment storage,
        bool canAttributeRepackCandidates,
        long unattributedReclaimedCellCount)
    {
        MinimumReclaimableBytes = minimumReclaimableBytes;
        RepackCandidates = repackCandidates;
        ReclaimComponents = reclaimComponents;
        ProjectionIssues = projectionIssues;
        Storage = storage;
        CanAttributeRepackCandidates = canAttributeRepackCandidates;
        UnattributedReclaimedCellCount = unattributedReclaimedCellCount;
    }

    /// <summary>
    /// Gets the caller-selected per-index reclaim threshold.<br/>
    /// </summary>
    public long MinimumReclaimableBytes { get; }

    /// <summary>
    /// Gets indexes for which LibraDex has attributable physical evidence supporting repack.<br/>
    /// </summary>
    public IReadOnlyList<LibraDexRepackCandidate> RepackCandidates { get; }

    /// <summary>
    /// Gets exact physical reclaim attribution for logical indexes and their maintained companion projections.<br/>
    /// Projection component bytes are already included in the corresponding logical <see cref="RepackCandidates"/> total and must not be added a second time.<br/>
    /// </summary>
    public IReadOnlyList<LibraDexReclaimComponent> ReclaimComponents { get; }

    /// <summary>
    /// Gets maintained projections whose persisted intent and active companion metadata disagree.<br/>
    /// </summary>
    public IReadOnlyList<LibraDexProjectionIssue> ProjectionIssues { get; }

    /// <summary>
    /// Gets physical file and active-topology reachability evidence collected by the same non-mutating assessment.<br/>
    /// </summary>
    public LibraDexCatalogStorageAssessment Storage { get; }

    /// <summary>
    /// Gets whether this LibraDex build can authoritatively attribute all reclaimable physical space to logical indexes.<br/>
    /// A false value prevents an empty <see cref="RepackCandidates"/> result from being misread as proof that repacking cannot help.<br/>
    /// </summary>
    public bool CanAttributeRepackCandidates { get; }

    /// <summary>
    /// Gets session-local reclaimed payload cells known catalog-wide but not safely attributable to a logical index.<br/>
    /// This is evidence availability, not a byte estimate or a repack recommendation.<br/>
    /// </summary>
    public long UnattributedReclaimedCellCount { get; }

    /// <summary>
    /// Gets whether the assessment found at least one actionable projection issue or attributable repack candidate.<br/>
    /// </summary>
    public bool HasRecommendations => RepackCandidates.Count != 0 || ProjectionIssues.Count != 0;
}

internal static class LibraDexMaintenanceAssessmentBuilder
{
    /// <summary>
    /// Produces one disconnected blocking maintenance assessment from persisted catalog topology and metadata.<br/>
    /// Physical projection components are attributed to their logical source index before the caller's reclaim threshold is applied.<br/>
    /// </summary>
    /// <param name="catalog">The open catalog whose active physical indexes are assessed.<br/></param>
    /// <param name="minimumReclaimableBytes">The non-negative logical-index reclaim threshold.<br/></param>
    /// <returns>The disconnected assessment, including exact component evidence and projection-health findings.<br/></returns>
    internal static LibraDexMaintenanceAssessment Assess(Catalog catalog, long minimumReclaimableBytes)
    {
        if (minimumReclaimableBytes < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(minimumReclaimableBytes),
                minimumReclaimableBytes,
                "The minimum reclaimable-byte threshold cannot be negative.");
        }

        CatalogIndexInfo[] indexes = catalog.Indexes.List();
        List<LibraDexProjectionIssue> issues = new();
        for (int i = 0; i < indexes.Length; i++)
            AddProjectionIssues(indexes[i], indexes, issues);

        Dictionary<int, LibraDexPhysicalReclaimOwner> companions = CreateCompanionOwners(indexes);
        List<LibraDexReclaimComponent> components = new();
        List<LibraDexRepackCandidate> candidates = new();
        bool complete = true;
        for (int i = 0; i < indexes.Length; i++)
        {
            CatalogIndexInfo logical = indexes[i];
            if (companions.ContainsKey(logical.SlotIndex))
                continue;

            long logicalBytes = AddReclaimComponent(
                catalog,
                logical,
                logical.SlotIndex,
                LibraDexIndexProjectionKind.Exact,
                LibraDexIndexByteDirection.Forward,
                components,
                ref complete);
            foreach (KeyValuePair<int, LibraDexPhysicalReclaimOwner> pair in companions)
            {
                LibraDexPhysicalReclaimOwner owner = pair.Value;
                if (owner.SourceSlotIndex != logical.SlotIndex)
                    continue;

                logicalBytes = checked(logicalBytes + AddReclaimComponent(
                    catalog,
                    logical,
                    pair.Key,
                    owner.Projection,
                    owner.Direction,
                    components,
                    ref complete));
            }

            if (logicalBytes > 0 && logicalBytes >= minimumReclaimableBytes)
            {
                candidates.Add(new LibraDexRepackCandidate(
                    logical.Group,
                    logical.Name,
                    logicalBytes,
                    "Persisted orphaned variable-record bytes meet the requested logical-index reclaim threshold, including maintained projection components."));
            }
        }

        LibraDexReclaimedPayloadStats reclaimed = catalog.Stats.ReclaimedPayload;
        if (reclaimed.QueuedCellCount != 0)
            complete = false;
        return new LibraDexMaintenanceAssessment(
            minimumReclaimableBytes,
            candidates.ToArray(),
            components.ToArray(),
            issues.ToArray(),
            AssessStorage(catalog, indexes, companions),
            canAttributeRepackCandidates: complete,
            reclaimed.QueuedCellCount);
    }

    /// <summary>
    /// Assesses catalog-owned extents and every active `SS8-8` generation while retaining an explicit incomplete boundary for unsupported shapes and key-state routes.<br/>
    /// </summary>
    /// <param name="catalog">The open catalog whose current directory roots should be walked.<br/></param>
    /// <param name="indexes">Disconnected active index metadata snapshots.<br/></param>
    /// <param name="companions">Physical-companion ownership used to attribute component names to their logical source indexes.<br/></param>
    /// <returns>Exact known reachability and catalog-wide amplification only when every active extent is understood.<br/></returns>
    private static LibraDexCatalogStorageAssessment AssessStorage(
        Catalog catalog,
        CatalogIndexInfo[] indexes,
        Dictionary<int, LibraDexPhysicalReclaimOwner> companions)
    {
        long? physicalBytes = catalog.Path is string path
            ? new FileInfo(path).Length
            : null;
        FileAllocationStorageSnapshot allocatorStorage = catalog.Session.GetFileAllocationStorageSnapshot();
        long overheadBytes = checked(
            (long)Layouts.SuperblockLayout.Size +
            catalog.Session.Superblock.IndexDirectoryLength +
            allocatorStorage.MetadataBytes);
        long knownReachableBytes = overheadBytes;
        bool complete = physicalBytes.HasValue;
        int unsupportedIndexCount = 0;
        List<LibraDexFixedTopologyStorageComponent> fixedComponents = new();
        List<LibraDexVariableTextTopologyStorageComponent> variableTextComponents = new();
        HashSet<long> metadataOffsets = new();
        HashSet<long> rootOffsets = new();

        ReadOnlySpan<IndexDirectorySlotSnapshot> slots = catalog.Session.IndexDirectory.ActiveSlots;
        for (int i = 0; i < slots.Length; i++)
        {
            IndexDirectorySlotSnapshot slot = slots[i];
            if (!catalog.Session.TryReadCatalogIndexMetadata(slot, out CatalogIndexMetadata metadata))
            {
                complete = false;
                unsupportedIndexCount++;
                continue;
            }

            if (slot.MetadataOffset <= 0 || !metadataOffsets.Add(slot.MetadataOffset))
            {
                complete = false;
            }
            else
            {
                int metadataBytes = CatalogIndexMetadataCodec.GetEncodedSize(metadata);
                overheadBytes = checked(overheadBytes + metadataBytes);
                knownReachableBytes = checked(knownReachableBytes + metadataBytes);
            }

            int physicalIndex = Array.FindIndex(indexes, candidate => candidate.SlotIndex == slot.SlotIndex);
            if (physicalIndex < 0)
            {
                complete = false;
                unsupportedIndexCount++;
                continue;
            }
            CatalogIndexInfo physical = indexes[physicalIndex];
            CatalogIndexInfo logical = physical;
            if (companions.TryGetValue(slot.SlotIndex, out LibraDexPhysicalReclaimOwner owner))
            {
                int ownerIndex = Array.FindIndex(indexes, candidate => candidate.SlotIndex == owner.SourceSlotIndex);
                if (ownerIndex >= 0)
                    logical = indexes[ownerIndex];
            }

            long componentReachableBytes;
            if (IsScalar8Scalar8Metadata(metadata))
            {
                if (metadata.NullKeyRouteOffset > 0 || metadata.EmptyKeyRouteOffset > 0)
                    complete = false;
                Scalar8Scalar8TopologyAssessment topology = catalog.Session.AssessScalar8Scalar8Topology(slot.SlotIndex);
                fixedComponents.Add(new LibraDexFixedTopologyStorageComponent(
                    logical.Group,
                    logical.Name,
                    physical.SlotIndex,
                    physical.Generation,
                    topology.TupleCount,
                    topology.ReachableBytes,
                    topology.RouterCount,
                    topology.OrdinaryShelfCount,
                    topology.DuplicateRunShelfCount,
                    topology.TerminalRootCount,
                    topology.TerminalShelfCount,
                    topology.ShelfExtentSize));
                componentReachableBytes = topology.ReachableBytes;
            }
            else if (IsScalar16Scalar8Metadata(metadata))
            {
                if (metadata.NullKeyRouteOffset > 0 || metadata.EmptyKeyRouteOffset > 0)
                    complete = false;
                OrdinaryFixedScalarTopologyAssessment topology = catalog.Session.AssessScalar16Scalar8Topology(slot.SlotIndex);
                fixedComponents.Add(CreateOrdinaryFixedTopologyComponent(logical, physical, topology));
                componentReachableBytes = topology.ReachableBytes;
            }
            else if (IsScalar8Scalar16Metadata(metadata))
            {
                if (metadata.NullKeyRouteOffset > 0 || metadata.EmptyKeyRouteOffset > 0)
                    complete = false;
                OrdinaryFixedScalarTopologyAssessment topology = catalog.Session.AssessScalar8Scalar16Topology(slot.SlotIndex);
                fixedComponents.Add(CreateOrdinaryFixedTopologyComponent(logical, physical, topology));
                componentReachableBytes = topology.ReachableBytes;
            }
            else if (IsScalar16Scalar16Metadata(metadata))
            {
                if (metadata.NullKeyRouteOffset > 0 || metadata.EmptyKeyRouteOffset > 0)
                    complete = false;
                OrdinaryFixedScalarTopologyAssessment topology = catalog.Session.AssessScalar16Scalar16Topology(slot.SlotIndex);
                fixedComponents.Add(CreateOrdinaryFixedTopologyComponent(logical, physical, topology));
                componentReachableBytes = topology.ReachableBytes;
            }
            else if (IsStringVarKeyScalar8Metadata(metadata))
            {
                VarKeyScalar8TopologyAssessment topology = catalog.Session.AssessVarKeyScalar8Topology(
                    slot.SlotIndex,
                    metadata.VarKeyMaxKeyLength,
                    metadata.NullKeyRouteOffset,
                    metadata.EmptyKeyRouteOffset);
                variableTextComponents.Add(new LibraDexVariableTextTopologyStorageComponent(
                    logical.Group,
                    logical.Name,
                    physical.SlotIndex,
                    physical.Generation,
                    topology.TupleCount,
                    topology.ReachableBytes,
                    topology.ReclaimablePayloadBytes,
                    topology.RouterCount,
                    topology.OrdinaryShelfCount,
                    topology.DuplicateRunShelfCount,
                    topology.TerminalRootCount,
                    topology.TerminalShelfCount,
                    topology.KeyStateRootCount));
                componentReachableBytes = topology.ReachableBytes;
            }
            else if (IsFixed32Scalar8Metadata(metadata))
            {
                if (metadata.NullKeyRouteOffset > 0 || metadata.EmptyKeyRouteOffset > 0)
                    complete = false;
                Fixed32Scalar8TopologyAssessment topology = catalog.Session.AssessFixed32Scalar8Topology(slot.SlotIndex);
                fixedComponents.Add(new LibraDexFixedTopologyStorageComponent(
                    logical.Group,
                    logical.Name,
                    physical.SlotIndex,
                    physical.Generation,
                    topology.TupleCount,
                    topology.ReachableBytes,
                    topology.RouterCount,
                    topology.ShelfCount,
                    DuplicateRunShelfCount: 0,
                    TerminalRootCount: 0,
                    TerminalShelfCount: 0,
                    ShelfExtentSize: topology.ShelfExtentSize));
                componentReachableBytes = topology.ReachableBytes;
            }
            else if (IsFixed32Scalar16Metadata(metadata))
            {
                if (metadata.NullKeyRouteOffset > 0 || metadata.EmptyKeyRouteOffset > 0)
                    complete = false;
                OrdinaryFixedScalarTopologyAssessment topology = catalog.Session.AssessFixed32Scalar16Topology(slot.SlotIndex);
                fixedComponents.Add(CreateOrdinaryFixedTopologyComponent(logical, physical, topology));
                componentReachableBytes = topology.ReachableBytes;
            }
            else if (IsFixedNScalar8Metadata(metadata))
            {
                if (metadata.NullKeyRouteOffset > 0 || metadata.EmptyKeyRouteOffset > 0)
                    complete = false;
                FixedNTopologyAssessment topology = catalog.Session.AssessFixedNScalar8Topology(slot.SlotIndex, metadata.VarKeyMaxKeyLength);
                fixedComponents.Add(CreateFixedNTopologyComponent(logical, physical, topology));
                componentReachableBytes = topology.ReachableBytes;
            }
            else if (IsFixedNScalar16Metadata(metadata))
            {
                if (metadata.NullKeyRouteOffset > 0 || metadata.EmptyKeyRouteOffset > 0)
                    complete = false;
                FixedNTopologyAssessment topology = catalog.Session.AssessFixedNScalar16Topology(slot.SlotIndex, metadata.VarKeyMaxKeyLength);
                fixedComponents.Add(CreateFixedNTopologyComponent(logical, physical, topology));
                componentReachableBytes = topology.ReachableBytes;
            }
            else if (IsFixedNVarIdentityMetadata(metadata))
            {
                if (metadata.NullKeyRouteOffset > 0 || metadata.EmptyKeyRouteOffset > 0)
                    complete = false;
                FixedNTopologyAssessment topology = catalog.Session.AssessFixedNVarIdentityTopology(
                    slot.SlotIndex,
                    metadata.VarKeyMaxKeyLength,
                    metadata.VarIdentityMaxLength);
                fixedComponents.Add(CreateFixedNTopologyComponent(logical, physical, topology));
                componentReachableBytes = topology.ReachableBytes;
            }
            else
            {
                unsupportedIndexCount++;
                complete = false;
                continue;
            }

            if (!rootOffsets.Add(physical.RootRouterOffset))
            {
                complete = false;
            }
            else
            {
                knownReachableBytes = checked(knownReachableBytes + componentReachableBytes);
            }
        }

        long? unreachableBytes = null;
        if (complete &&
            physicalBytes.HasValue &&
            physicalBytes.Value >= checked(knownReachableBytes + allocatorStorage.ReusablePayloadBytes))
        {
            unreachableBytes = checked(
                physicalBytes.Value -
                knownReachableBytes -
                allocatorStorage.ReusablePayloadBytes);
        }
        else if (complete)
            complete = false;

        return new LibraDexCatalogStorageAssessment(
            physicalBytes,
            knownReachableBytes,
            overheadBytes,
            allocatorStorage.MetadataBytes,
            allocatorStorage.ReusablePayloadBytes,
            unreachableBytes,
            complete,
            unsupportedIndexCount,
            fixedComponents.ToArray(),
            variableTextComponents.ToArray());
    }

    /// <summary>
    /// Converts one exact ordinary fixed-scalar topology result into the public catalog storage component shape.<br/>
    /// Ordinary widened fixed-scalar families contain neither duplicate-run shelves nor terminal identity roots, so those public counters are structurally zero rather than unavailable estimates.<br/>
    /// </summary>
    /// <param name="logical">The logical catalog index receiving storage attribution.<br/></param>
    /// <param name="physical">The active physical slot that owns the assessed root generation.<br/></param>
    /// <param name="topology">The exact current-generation router and shelf assessment.<br/></param>
    /// <returns>The public fixed-topology storage component.<br/></returns>
    private static LibraDexFixedTopologyStorageComponent CreateOrdinaryFixedTopologyComponent(
        CatalogIndexInfo logical,
        CatalogIndexInfo physical,
        OrdinaryFixedScalarTopologyAssessment topology)
    {
        return new LibraDexFixedTopologyStorageComponent(
            logical.Group,
            logical.Name,
            physical.SlotIndex,
            physical.Generation,
            topology.TupleCount,
            topology.ReachableBytes,
            topology.RouterCount,
            topology.ShelfCount,
            DuplicateRunShelfCount: 0,
            TerminalRootCount: 0,
            TerminalShelfCount: 0,
            ShelfExtentSize: topology.ShelfExtentSize);
    }

    /// <summary>
    /// Converts one exact programmable fixed-key topology result into the public fixed storage component shape.<br/>
    /// Current FSN families contain routers and ordinary shelves only; key-state routes remain an explicit catalog-completeness boundary until separately counted.<br/>
    /// </summary>
    /// <param name="logical">The logical catalog index receiving storage attribution.<br/></param>
    /// <param name="physical">The active physical slot that owns the assessed root generation.<br/></param>
    /// <param name="topology">The exact current-generation router and shelf assessment.<br/></param>
    /// <returns>The public fixed-topology storage component.<br/></returns>
    private static LibraDexFixedTopologyStorageComponent CreateFixedNTopologyComponent(
        CatalogIndexInfo logical,
        CatalogIndexInfo physical,
        FixedNTopologyAssessment topology)
    {
        return new LibraDexFixedTopologyStorageComponent(
            logical.Group,
            logical.Name,
            physical.SlotIndex,
            physical.Generation,
            topology.TupleCount,
            topology.ReachableBytes,
            topology.RouterCount,
            topology.ShelfCount,
            DuplicateRunShelfCount: 0,
            TerminalRootCount: 0,
            TerminalShelfCount: 0,
            ShelfExtentSize: topology.ShelfExtentSize);
    }

    /// <summary>
    /// Determines whether persisted CLR and family metadata select the fixed scalar-8 key and scalar-8 identity topology.<br/>
    /// </summary>
    /// <param name="metadata">The active physical slot metadata.<br/></param>
    /// <returns><see langword="true"/> only when both runtime types resolve to the eight-byte generic scalar contract.<br/></returns>
    internal static bool IsScalar8Scalar8Metadata(CatalogIndexMetadata metadata)
    {
        if (metadata.KeyFamily is not (CatalogIndexKeyFamily.Scalar or CatalogIndexKeyFamily.Date) ||
            metadata.IdentityFamily != CatalogIndexIdentityFamily.Scalar)
        {
            return false;
        }

        Type? keyType = Type.GetType(metadata.KeyTypeName, throwOnError: false);
        Type? identityType = Type.GetType(metadata.IdentityTypeName, throwOnError: false);
        return keyType is not null && identityType is not null &&
            IsScalar8Type(keyType) && IsScalar8Type(identityType);
    }

    /// <summary>
    /// Determines whether persisted CLR and family metadata select a sixteen-byte scalar key with an eight-byte scalar identity.<br/>
    /// </summary>
    /// <param name="metadata">The active physical slot metadata.<br/></param>
    /// <returns><see langword="true"/> only for the `SS16-8` physical topology.<br/></returns>
    internal static bool IsScalar16Scalar8Metadata(CatalogIndexMetadata metadata)
    {
        if (metadata.KeyFamily is not (CatalogIndexKeyFamily.Scalar or CatalogIndexKeyFamily.Guid) ||
            metadata.IdentityFamily != CatalogIndexIdentityFamily.Scalar)
        {
            return false;
        }

        Type? keyType = Type.GetType(metadata.KeyTypeName, throwOnError: false);
        Type? identityType = Type.GetType(metadata.IdentityTypeName, throwOnError: false);
        return keyType is not null && identityType is not null &&
            IsScalar16Type(keyType) && IsScalar8Type(identityType);
    }

    /// <summary>
    /// Determines whether persisted CLR and family metadata select an eight-byte scalar key with a sixteen-byte scalar identity.<br/>
    /// </summary>
    /// <param name="metadata">The active physical slot metadata.<br/></param>
    /// <returns><see langword="true"/> only for the `SS8-16` physical topology.<br/></returns>
    internal static bool IsScalar8Scalar16Metadata(CatalogIndexMetadata metadata)
    {
        if (metadata.KeyFamily is not (CatalogIndexKeyFamily.Scalar or CatalogIndexKeyFamily.Date) ||
            metadata.IdentityFamily != CatalogIndexIdentityFamily.Scalar)
        {
            return false;
        }

        Type? keyType = Type.GetType(metadata.KeyTypeName, throwOnError: false);
        Type? identityType = Type.GetType(metadata.IdentityTypeName, throwOnError: false);
        return keyType is not null && identityType is not null &&
            IsScalar8Type(keyType) && IsScalar16Type(identityType);
    }

    /// <summary>
    /// Determines whether persisted CLR and family metadata select sixteen-byte scalar key and identity lanes.<br/>
    /// </summary>
    /// <param name="metadata">The active physical slot metadata.<br/></param>
    /// <returns><see langword="true"/> only for the `SS16-16` physical topology.<br/></returns>
    internal static bool IsScalar16Scalar16Metadata(CatalogIndexMetadata metadata)
    {
        if (metadata.KeyFamily is not (CatalogIndexKeyFamily.Scalar or CatalogIndexKeyFamily.Guid) ||
            metadata.IdentityFamily != CatalogIndexIdentityFamily.Scalar)
        {
            return false;
        }

        Type? keyType = Type.GetType(metadata.KeyTypeName, throwOnError: false);
        Type? identityType = Type.GetType(metadata.IdentityTypeName, throwOnError: false);
        return keyType is not null && identityType is not null &&
            IsScalar16Type(keyType) && IsScalar16Type(identityType);
    }

    /// <summary>
    /// Determines whether persisted metadata selects the supported string-backed variable-key/scalar-8 identity topology.<br/>
    /// Maintained string projection companions retain string family/type metadata and are assessed independently while ownership is attributed to their logical source index.<br/>
    /// </summary>
    /// <param name="metadata">The active physical slot metadata.<br/></param>
    /// <returns><see langword="true"/> for string-key, scalar-8 identity `VS8` storage with a positive maximum key length.<br/></returns>
    internal static bool IsStringVarKeyScalar8Metadata(CatalogIndexMetadata metadata)
    {
        if (metadata.KeyFamily != CatalogIndexKeyFamily.String ||
            metadata.IdentityFamily != CatalogIndexIdentityFamily.Scalar ||
            metadata.VarKeyMaxKeyLength <= 0)
        {
            return false;
        }

        Type? identityType = Type.GetType(metadata.IdentityTypeName, throwOnError: false);
        return identityType is not null && IsScalar8Type(identityType);
    }

    /// <summary>
    /// Determines whether persisted metadata selects the fixed 32-byte blob-key/scalar-8 identity topology used by Abraxas state-intent indexes.<br/>
    /// The fixed width is part of the physical shape contract; variable blob widths remain outside this walker rather than being guessed from the same logical family.<br/>
    /// </summary>
    /// <param name="metadata">The active physical slot metadata.<br/></param>
    /// <returns><see langword="true"/> only for a 32-byte blob key and eight-byte scalar identity.<br/></returns>
    internal static bool IsFixed32Scalar8Metadata(CatalogIndexMetadata metadata)
    {
        if (metadata.KeyFamily != CatalogIndexKeyFamily.Blob ||
            metadata.IdentityFamily != CatalogIndexIdentityFamily.Scalar ||
            metadata.VarKeyMaxKeyLength != Layouts.Fixed32Scalar8Layout.KeySize)
        {
            return false;
        }

        Type? identityType = Type.GetType(metadata.IdentityTypeName, throwOnError: false);
        return identityType is not null && IsScalar8Type(identityType);
    }

    /// <summary>
    /// Determines whether persisted metadata selects the fixed 32-byte blob-key/scalar-16 identity topology.<br/>
    /// The fixed width remains explicit so variable blob layouts cannot be mistaken for `FS32-16` during exact accounting.<br/>
    /// </summary>
    /// <param name="metadata">The active physical slot metadata.<br/></param>
    /// <returns><see langword="true"/> only for a 32-byte blob key and sixteen-byte scalar identity.<br/></returns>
    internal static bool IsFixed32Scalar16Metadata(CatalogIndexMetadata metadata)
    {
        if (metadata.KeyFamily != CatalogIndexKeyFamily.Blob ||
            metadata.IdentityFamily != CatalogIndexIdentityFamily.Scalar ||
            metadata.VarKeyMaxKeyLength != Layouts.Fixed32Scalar16Layout.KeySize)
        {
            return false;
        }

        Type? identityType = Type.GetType(metadata.IdentityTypeName, throwOnError: false);
        return identityType is not null && IsScalar16Type(identityType);
    }

    /// <summary>
    /// Determines whether persisted metadata selects fixed-width BigInteger keys with scalar-8 identities.<br/>
    /// </summary>
    /// <param name="metadata">The active physical slot metadata.<br/></param>
    /// <returns><see langword="true"/> only for the `FSN-8` physical topology.<br/></returns>
    internal static bool IsFixedNScalar8Metadata(CatalogIndexMetadata metadata)
    {
        if (!IsFixedNProjection(metadata, LibraDexIndexProjectionKind.BigIntFixed) ||
            metadata.IdentityFamily != CatalogIndexIdentityFamily.Scalar)
        {
            return false;
        }
        Type? identityType = Type.GetType(metadata.IdentityTypeName, throwOnError: false);
        return identityType is not null && IsScalar8Type(identityType);
    }

    /// <summary>
    /// Determines whether persisted metadata selects fixed-width BigInteger keys with scalar-16 identities.<br/>
    /// </summary>
    /// <param name="metadata">The active physical slot metadata.<br/></param>
    /// <returns><see langword="true"/> only for the `FSN-16` physical topology.<br/></returns>
    internal static bool IsFixedNScalar16Metadata(CatalogIndexMetadata metadata)
    {
        if (!IsFixedNProjection(metadata, LibraDexIndexProjectionKind.BigIntFixed) ||
            metadata.IdentityFamily != CatalogIndexIdentityFamily.Scalar)
        {
            return false;
        }
        Type? identityType = Type.GetType(metadata.IdentityTypeName, throwOnError: false);
        return identityType is not null && IsScalar16Type(identityType);
    }

    /// <summary>
    /// Determines whether persisted metadata selects fixed-width BigInteger keys with raw variable identities.<br/>
    /// </summary>
    /// <param name="metadata">The active physical slot metadata.<br/></param>
    /// <returns><see langword="true"/> only for the `FSN-V` physical topology.<br/></returns>
    internal static bool IsFixedNVarIdentityMetadata(CatalogIndexMetadata metadata)
    {
        return IsFixedNProjection(metadata, LibraDexIndexProjectionKind.BigIntFixedVarIdentity) &&
            metadata.IdentityFamily == CatalogIndexIdentityFamily.Blob &&
            metadata.VarIdentityMaxLength > 0;
    }

    /// <summary>
    /// Determines whether one catalog entry carries the requested first-class fixed-N physical projection and a positive fixed key width.<br/>
    /// </summary>
    /// <param name="metadata">The active physical slot metadata.<br/></param>
    /// <param name="projection">The exact fixed-N projection kind required by the caller.<br/></param>
    /// <returns><see langword="true"/> when family, projection, and key-width contracts all match.<br/></returns>
    private static bool IsFixedNProjection(CatalogIndexMetadata metadata, LibraDexIndexProjectionKind projection)
    {
        return metadata.KeyFamily == CatalogIndexKeyFamily.BigInt &&
            metadata.VarKeyMaxKeyLength > 0 &&
            metadata.Projections.Count == 1 &&
            metadata.Projections[0].Kind == projection;
    }

    /// <summary>
    /// Mirrors the generic scalar codec's CLR-to-eight-byte-width contract without constructing a closed generic codec at maintenance time.<br/>
    /// </summary>
    /// <param name="type">Resolved persisted CLR type.<br/></param>
    /// <returns><see langword="true"/> when the type uses one encoded eight-byte scalar lane.<br/></returns>
    internal static bool IsScalar8Type(Type type)
    {
        return type == typeof(bool) ||
            type == typeof(byte) ||
            type == typeof(sbyte) ||
            type == typeof(short) ||
            type == typeof(ushort) ||
            type == typeof(char) ||
            type == typeof(int) ||
            type == typeof(uint) ||
            type == typeof(long) ||
            type == typeof(ulong) ||
            type == typeof(float) ||
            type == typeof(double) ||
            type == typeof(DateTime) ||
            type == typeof(DateTimeOffset) ||
            type == typeof(DateOnly) ||
            type == typeof(TimeOnly) ||
            type == typeof(TimeSpan);
    }

    /// <summary>
    /// Mirrors the generic scalar codec's CLR-to-sixteen-byte-width contract without constructing a closed generic codec at maintenance time.<br/>
    /// </summary>
    /// <param name="type">Resolved persisted CLR type.<br/></param>
    /// <returns><see langword="true"/> when the type uses one encoded sixteen-byte scalar lane.<br/></returns>
    internal static bool IsScalar16Type(Type type)
    {
        return type == typeof(Guid) ||
            type == typeof(decimal) ||
            type == typeof(Int128) ||
            type == typeof(UInt128);
    }

    /// <summary>
    /// Assesses one physical slot and appends its non-zero reclaim evidence under the supplied logical owner.<br/>
    /// Unknown physical target formats propagate an incomplete evidence boundary without fabricating a byte estimate.<br/>
    /// </summary>
    /// <param name="catalog">The open catalog that owns the physical slot.<br/></param>
    /// <param name="logical">The logical source-index metadata receiving attribution.<br/></param>
    /// <param name="physicalSlotIndex">The physical source or companion slot to walk.<br/></param>
    /// <param name="projection">The component's projection kind.<br/></param>
    /// <param name="direction">The component's byte direction.<br/></param>
    /// <param name="components">The disconnected component collection under construction.<br/></param>
    /// <param name="complete">The cumulative evidence-completeness flag.<br/></param>
    /// <returns>The exact persisted orphaned bytes attributed to this physical component.<br/></returns>
    private static long AddReclaimComponent(
        Catalog catalog,
        CatalogIndexInfo logical,
        int physicalSlotIndex,
        LibraDexIndexProjectionKind projection,
        LibraDexIndexByteDirection direction,
        List<LibraDexReclaimComponent> components,
        ref bool complete)
    {
        (long bytes, int shelves, bool componentComplete) =
            catalog.Session.AssessIndexReclaimablePayload(physicalSlotIndex);
        complete &= componentComplete;
        if (bytes != 0)
        {
            components.Add(new LibraDexReclaimComponent(
                logical.Group,
                logical.Name,
                physicalSlotIndex,
                projection,
                direction,
                bytes,
                shelves));
        }

        return bytes;
    }

    /// <summary>
    /// Creates the physical-companion-to-logical-owner map from persisted source-index metadata.<br/>
    /// The map prevents companions from appearing as independent logical indexes while retaining their individual evidence.<br/>
    /// </summary>
    /// <param name="indexes">The active disconnected catalog index snapshots.<br/></param>
    /// <returns>A map keyed by physical companion slot index.<br/></returns>
    private static Dictionary<int, LibraDexPhysicalReclaimOwner> CreateCompanionOwners(
        CatalogIndexInfo[] indexes)
    {
        Dictionary<int, LibraDexPhysicalReclaimOwner> owners = new();
        for (int i = 0; i < indexes.Length; i++)
        {
            CatalogIndexInfo source = indexes[i];
            AddCompanion(
                owners,
                source.SlotIndex,
                source.ExactReversedProjectionSlotIndex,
                LibraDexIndexProjectionKind.Exact,
                LibraDexIndexByteDirection.Reversed);
            AddCompanion(
                owners,
                source.SlotIndex,
                source.FoldedProjectionSlotIndex,
                LibraDexIndexProjectionKind.FoldedText,
                LibraDexIndexByteDirection.Forward);
            AddCompanion(
                owners,
                source.SlotIndex,
                source.SortKeyProjectionSlotIndex,
                LibraDexIndexProjectionKind.SortKey,
                LibraDexIndexByteDirection.Forward);
            if (source.SortKeyProfiles is { Count: > 1 } sortKeyProfiles)
            {
                for (int j = 1; j < sortKeyProfiles.Count; j++)
                {
                    AddCompanion(
                        owners,
                        source.SlotIndex,
                        sortKeyProfiles[j].SlotIndex,
                        LibraDexIndexProjectionKind.SortKey,
                        LibraDexIndexByteDirection.Forward);
                }
            }
            AddCompanion(
                owners,
                source.SlotIndex,
                source.FoldedReversedProjectionSlotIndex,
                LibraDexIndexProjectionKind.FoldedText,
                LibraDexIndexByteDirection.Reversed);
            AddCompanion(
                owners,
                source.SlotIndex,
                source.NormalizedProjectionSlotIndex,
                LibraDexIndexProjectionKind.NormalizedText,
                LibraDexIndexByteDirection.Forward);
            AddCompanion(
                owners,
                source.SlotIndex,
                source.NormalizedReversedProjectionSlotIndex,
                LibraDexIndexProjectionKind.NormalizedText,
                LibraDexIndexByteDirection.Reversed);
        }

        return owners;
    }

    /// <summary>
    /// Adds one active persisted companion reference to the physical ownership map.<br/>
    /// Negative slot values represent projections that are not maintained and are intentionally ignored.<br/>
    /// </summary>
    /// <param name="owners">The ownership map under construction.<br/></param>
    /// <param name="sourceSlotIndex">The logical source index's physical slot.<br/></param>
    /// <param name="companionSlotIndex">The maintained companion slot, or a negative sentinel.<br/></param>
    /// <param name="projection">The companion projection kind.<br/></param>
    /// <param name="direction">The companion byte direction.<br/></param>
    private static void AddCompanion(
        Dictionary<int, LibraDexPhysicalReclaimOwner> owners,
        int sourceSlotIndex,
        int companionSlotIndex,
        LibraDexIndexProjectionKind projection,
        LibraDexIndexByteDirection direction)
    {
        if (companionSlotIndex < 0)
            return;

        owners[companionSlotIndex] = new LibraDexPhysicalReclaimOwner(
            sourceSlotIndex,
            projection,
            direction);
    }

    /// <summary>
    /// Compares one logical index's declared projections with its active physical companion slots.<br/>
    /// Healthy raw companions have no independent logical group metadata and use the deterministic owned-companion physical name.<br/>
    /// </summary>
    /// <param name="index">The logical source index to validate.<br/></param>
    /// <param name="active">All active disconnected catalog index snapshots.<br/></param>
    /// <param name="issues">The projection issue collection under construction.<br/></param>
    private static void AddProjectionIssues(
        CatalogIndexInfo index,
        CatalogIndexInfo[] active,
        List<LibraDexProjectionIssue> issues)
    {
        IReadOnlyList<LibraDexIndexProjectionSpec> projections = index.Projections;
        for (int i = 0; i < projections.Count; i++)
        {
            LibraDexIndexProjectionSpec projection = projections[i];
            int slotIndex = GetCompanionSlot(index, projection);
            if (slotIndex == int.MinValue)
                continue;

            if (slotIndex < 0)
            {
                issues.Add(new LibraDexProjectionIssue(
                    index.Group,
                    index.Name,
                    projection.Kind,
                    projection.Direction,
                    LibraDexProjectionHealth.Missing,
                    "Rebuild the maintained projection.",
                    "The logical index declares this projection, but persisted metadata contains no companion slot."));
                continue;
            }

            int found = -1;
            for (int j = 0; j < active.Length; j++)
            {
                if (active[j].SlotIndex == slotIndex)
                {
                    found = j;
                    break;
                }
            }

            if (found < 0)
            {
                issues.Add(new LibraDexProjectionIssue(
                    index.Group,
                    index.Name,
                    projection.Kind,
                    projection.Direction,
                    LibraDexProjectionHealth.Missing,
                    "Rebuild the maintained projection.",
                    $"Persisted metadata references inactive companion slot {slotIndex}."));
                continue;
            }

            CatalogIndexInfo companion = active[found];
            string expectedPhysicalName = GetCompanionPhysicalName(index.Name, projection);
            if (!string.IsNullOrEmpty(companion.Group) ||
                !string.Equals(companion.Name, expectedPhysicalName, StringComparison.Ordinal))
            {
                issues.Add(new LibraDexProjectionIssue(
                    index.Group,
                    index.Name,
                    projection.Kind,
                    projection.Direction,
                    LibraDexProjectionHealth.Inconsistent,
                    "Validate the catalog and rebuild the maintained projection.",
                    $"Companion slot {slotIndex} resolves to physical index '{companion.Name}' with logical group '{companion.Group}', not owned companion '{expectedPhysicalName}'."));
            }
        }

        AddAdditionalSortKeyProjectionIssues(index, active, issues);
    }

    /// <summary>
    /// Validates owned sort-key profile companions beyond the legacy ordinal-zero slot.<br/>
    /// Each additional profile uses a deterministic ordinal physical name while its culture and comparison identity remain in the logical owner's metadata.<br/>
    /// </summary>
    /// <param name="index">The logical string index metadata.<br/></param>
    /// <param name="active">All active disconnected catalog index snapshots.<br/></param>
    /// <param name="issues">The projection issue collection under construction.<br/></param>
    private static void AddAdditionalSortKeyProjectionIssues(
        CatalogIndexInfo index,
        CatalogIndexInfo[] active,
        List<LibraDexProjectionIssue> issues)
    {
        if (index.SortKeyProfiles is not { Count: > 1 } profiles)
        {
            return;
        }

        for (int i = 1; i < profiles.Count; i++)
        {
            LibraDexStringSortKeyProjectionInfo profile = profiles[i];
            int found = -1;
            for (int j = 0; j < active.Length; j++)
            {
                if (active[j].SlotIndex == profile.SlotIndex)
                {
                    found = j;
                    break;
                }
            }

            if (found < 0)
            {
                issues.Add(new LibraDexProjectionIssue(
                    index.Group,
                    index.Name,
                    LibraDexIndexProjectionKind.SortKey,
                    LibraDexIndexByteDirection.Forward,
                    LibraDexProjectionHealth.Missing,
                    "Rebuild the maintained projection.",
                    $"Persisted culture sort-key profile ordinal {i} references inactive companion slot {profile.SlotIndex}."));
                continue;
            }

            CatalogIndexInfo companion = active[found];
            string expectedName = $"{index.Name}#sortkey-{i}";
            if (!string.IsNullOrEmpty(companion.Group) ||
                !string.Equals(companion.Name, expectedName, StringComparison.Ordinal))
            {
                issues.Add(new LibraDexProjectionIssue(
                    index.Group,
                    index.Name,
                    LibraDexIndexProjectionKind.SortKey,
                    LibraDexIndexByteDirection.Forward,
                    LibraDexProjectionHealth.Inconsistent,
                    "Validate the catalog and rebuild the maintained projection.",
                    $"Culture sort-key profile ordinal {i} resolves to physical index '{companion.Name}' with logical group '{companion.Group}', not owned companion '{expectedName}'."));
            }
        }
    }

    /// <summary>
    /// Resolves the deterministic raw physical index name owned by one logical projection declaration.<br/>
    /// </summary>
    /// <param name="sourceName">The logical source index name.<br/></param>
    /// <param name="projection">The projection kind and byte direction.<br/></param>
    /// <returns>The expected raw companion index name.<br/></returns>
    private static string GetCompanionPhysicalName(
        string sourceName,
        LibraDexIndexProjectionSpec projection)
    {
        if (projection.Kind == LibraDexIndexProjectionKind.Exact &&
            projection.Direction == LibraDexIndexByteDirection.Reversed)
        {
            return $"{sourceName}#exact-rev";
        }
        if (projection.Kind == LibraDexIndexProjectionKind.FoldedText &&
            projection.Direction == LibraDexIndexByteDirection.Forward)
        {
            return $"{sourceName}#folded";
        }
        if (projection.Kind == LibraDexIndexProjectionKind.FoldedText &&
            projection.Direction == LibraDexIndexByteDirection.Reversed)
        {
            return $"{sourceName}#folded-rev";
        }
        if (projection.Kind == LibraDexIndexProjectionKind.SortKey &&
            projection.Direction == LibraDexIndexByteDirection.Forward)
        {
            return $"{sourceName}#sortkey";
        }
        if (projection.Kind == LibraDexIndexProjectionKind.NormalizedText &&
            projection.Direction == LibraDexIndexByteDirection.Forward)
        {
            return $"{sourceName}#normalized";
        }
        if (projection.Kind == LibraDexIndexProjectionKind.NormalizedText &&
            projection.Direction == LibraDexIndexByteDirection.Reversed)
        {
            return $"{sourceName}#normalized-rev";
        }

        return sourceName;
    }

    /// <summary>
    /// Resolves the persisted physical companion slot for one declared logical projection.<br/>
    /// Exact-forward storage returns an internal non-companion sentinel because it is the logical source slot itself.<br/>
    /// </summary>
    /// <param name="index">The logical source index metadata.<br/></param>
    /// <param name="projection">The declared projection to resolve.<br/></param>
    /// <returns>The companion slot, a negative missing-slot value, or the exact-forward sentinel.<br/></returns>
    private static int GetCompanionSlot(
        CatalogIndexInfo index,
        LibraDexIndexProjectionSpec projection)
    {
        if (projection.Kind == LibraDexIndexProjectionKind.Exact &&
            projection.Direction == LibraDexIndexByteDirection.Forward)
        {
            return int.MinValue;
        }
        if (projection.Kind == LibraDexIndexProjectionKind.Exact &&
            projection.Direction == LibraDexIndexByteDirection.Reversed)
        {
            return index.ExactReversedProjectionSlotIndex;
        }
        if (projection.Kind == LibraDexIndexProjectionKind.FoldedText &&
            projection.Direction == LibraDexIndexByteDirection.Forward)
        {
            return index.FoldedProjectionSlotIndex;
        }
        if (projection.Kind == LibraDexIndexProjectionKind.FoldedText &&
            projection.Direction == LibraDexIndexByteDirection.Reversed)
        {
            return index.FoldedReversedProjectionSlotIndex;
        }
        if (projection.Kind == LibraDexIndexProjectionKind.SortKey &&
            projection.Direction == LibraDexIndexByteDirection.Forward)
        {
            return index.SortKeyProjectionSlotIndex;
        }
        if (projection.Kind == LibraDexIndexProjectionKind.NormalizedText &&
            projection.Direction == LibraDexIndexByteDirection.Forward)
        {
            return index.NormalizedProjectionSlotIndex;
        }
        if (projection.Kind == LibraDexIndexProjectionKind.NormalizedText &&
            projection.Direction == LibraDexIndexByteDirection.Reversed)
        {
            return index.NormalizedReversedProjectionSlotIndex;
        }

        return int.MinValue;
    }

    private readonly record struct LibraDexPhysicalReclaimOwner(
        int SourceSlotIndex,
        LibraDexIndexProjectionKind Projection,
        LibraDexIndexByteDirection Direction);
}
