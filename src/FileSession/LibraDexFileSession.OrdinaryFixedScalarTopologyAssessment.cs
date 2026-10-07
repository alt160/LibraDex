using System.Buffers.Binary;
using LibraDex.Layouts;
using LibraDex.Views;

namespace LibraDex;

internal sealed partial class LibraDexFileSession
{
    /// <summary>
    /// Walks one active `SS16-8` generation and returns exact reachable router, shelf, tuple, and byte counts.<br/>
    /// The persisted profile must be the supported file-backed 32 KiB profile; unsupported profile identifiers are rejected rather than estimated.<br/>
    /// </summary>
    /// <param name="slotIndex">The active fixed-directory slot whose current root generation should be assessed.<br/></param>
    /// <returns>Exact current-generation topology and tuple accounting.<br/></returns>
    internal OrdinaryFixedScalarTopologyAssessment AssessScalar16Scalar8Topology(int slotIndex)
    {
        Scalar16Scalar8Profile profile = Scalar16Scalar8Profile.Default32KiB;
        return AssessOrdinaryFixedScalarTopology(
            slotIndex,
            Scalar16Scalar8Layout.Magic,
            Scalar16Scalar8Layout.FormatVersion,
            Scalar16Scalar8Layout.HeaderSize,
            profile.MaxItemCount,
            profile.ShelfExtentSize,
            "SS16-8",
            terminalShape: TerminalIdentityRootLayout.ShapeFixedKeyScalar8Identity,
            terminalKeyLength: Scalar16Scalar8Layout.KeySize);
    }

    /// <summary>
    /// Walks one active `SS8-16` generation and returns exact reachable router, shelf, tuple, and byte counts.<br/>
    /// The persisted profile must be the supported file-backed 32 KiB profile; unsupported profile identifiers are rejected rather than estimated.<br/>
    /// </summary>
    /// <param name="slotIndex">The active fixed-directory slot whose current root generation should be assessed.<br/></param>
    /// <returns>Exact current-generation topology and tuple accounting.<br/></returns>
    internal OrdinaryFixedScalarTopologyAssessment AssessScalar8Scalar16Topology(int slotIndex)
    {
        Scalar8Scalar16Profile profile = Scalar8Scalar16Profile.Default32KiB;
        return AssessOrdinaryFixedScalarTopology(
            slotIndex,
            Scalar8Scalar16Layout.Magic,
            Scalar8Scalar16Layout.FormatVersion,
            Scalar8Scalar16Layout.HeaderSize,
            profile.MaxItemCount,
            profile.ShelfExtentSize,
            "SS8-16",
            terminalShape: TerminalIdentityRootLayout.ShapeFixedKeyScalar16Identity,
            terminalKeyLength: Scalar8Scalar16Layout.KeySize);
    }

    /// <summary>
    /// Walks one active `SS16-16` generation and returns exact reachable router, shelf, tuple, and byte counts.<br/>
    /// The persisted profile must be the supported file-backed 24 KiB profile; unsupported profile identifiers are rejected rather than estimated.<br/>
    /// </summary>
    /// <param name="slotIndex">The active fixed-directory slot whose current root generation should be assessed.<br/></param>
    /// <returns>Exact current-generation topology and tuple accounting.<br/></returns>
    internal OrdinaryFixedScalarTopologyAssessment AssessScalar16Scalar16Topology(int slotIndex)
    {
        Scalar16Scalar16Profile profile = Scalar16Scalar16Profile.Default24KiB;
        return AssessOrdinaryFixedScalarTopology(
            slotIndex,
            Scalar16Scalar16Layout.Magic,
            Scalar16Scalar16Layout.FormatVersion,
            Scalar16Scalar16Layout.HeaderSize,
            profile.MaxItemCount,
            profile.ShelfExtentSize,
            "SS16-16",
            terminalShape: TerminalIdentityRootLayout.ShapeFixedKeyScalar16Identity,
            terminalKeyLength: Scalar16Scalar16Layout.KeySize);
    }

    /// <summary>
    /// Walks one active `FS32-16` generation and returns exact reachable router, shelf, tuple, and byte counts.<br/>
    /// The persisted profile must be the supported file-backed 40 KiB profile; unsupported profile identifiers are rejected rather than estimated.<br/>
    /// </summary>
    /// <param name="slotIndex">The active fixed-directory slot whose current root generation should be assessed.<br/></param>
    /// <returns>Exact current-generation topology and tuple accounting.<br/></returns>
    internal OrdinaryFixedScalarTopologyAssessment AssessFixed32Scalar16Topology(int slotIndex)
    {
        Fixed32Scalar16Profile profile = Fixed32Scalar16Profile.Default40KiB;
        return AssessOrdinaryFixedScalarTopology(
            slotIndex,
            Fixed32Scalar16Layout.Magic,
            Fixed32Scalar16Layout.FormatVersion,
            Fixed32Scalar16Layout.HeaderSize,
            profile.MaxItemCount,
            profile.ShelfExtentSize,
            "FS32-16",
            terminalShape: TerminalIdentityRootLayout.ShapeFixedKeyScalar16Identity,
            terminalKeyLength: Fixed32Scalar16Layout.KeySize);
    }

    /// <summary>
    /// Walks one active ordinary fixed-scalar generation and counts each distinct router and shelf extent exactly once.<br/>
    /// All supported ordinary fixed-scalar shelf layouts share the count-bearing header offsets, while the caller supplies the shape-specific magic, version, header size, capacity, and extent size.<br/>
    /// The method rejects cycles only by deduplicating already visited physical offsets, rejects unknown target kinds, and never fabricates accounting for an unsupported directory profile.<br/>
    /// </summary>
    /// <param name="slotIndex">The active fixed-directory slot whose current root generation should be assessed.<br/></param>
    /// <param name="shelfMagic">The expected shape-specific ordinary shelf magic.<br/></param>
    /// <param name="formatVersion">The expected shape-specific shelf format version.<br/></param>
    /// <param name="headerSize">The expected shape-specific fixed header size.<br/></param>
    /// <param name="maxItemCount">The maximum item count admitted by the selected shelf profile.<br/></param>
    /// <param name="shelfExtentSize">The complete persisted extent size of one ordinary shelf.<br/></param>
    /// <param name="shapeName">The short physical shape name used in validation diagnostics.<br/></param>
    /// <param name="terminalShape">The exact terminal root shape admitted by the owning fixed-key family, or zero when terminals are unsupported.<br/></param>
    /// <param name="terminalKeyLength">The exact fixed key width required when terminal roots are supported.<br/></param>
    /// <returns>Exact current-generation topology and tuple accounting.<br/></returns>
    private OrdinaryFixedScalarTopologyAssessment AssessOrdinaryFixedScalarTopology(
        int slotIndex,
        uint shelfMagic,
        ushort formatVersion,
        ushort headerSize,
        ushort maxItemCount,
        int shelfExtentSize,
        string shapeName,
        byte terminalShape,
        int terminalKeyLength)
    {
        if (!TryFindIndexDirectorySlot(slotIndex, out IndexDirectorySlotSnapshot slot))
            throw new InvalidDataException($"{shapeName} topology assessment slot {slotIndex} is not active.");
        if (slot.RootRouterOffset <= 0)
            throw new InvalidDataException($"{shapeName} topology assessment slot {slotIndex} has no positive root offset.");
        if (slot.KeyProfileId != 1 ||
            slot.IdentityProfileId != 1 ||
            slot.RouterProfileId != 1 ||
            slot.AllocationClassId != 1)
        {
            throw new ArgumentException(
                $"The index-directory slot does not describe the supported file-backed {shapeName} profile.",
                nameof(slotIndex));
        }

        Stack<long> pending = new();
        HashSet<long> visited = new();
        pending.Push(slot.RootRouterOffset);
        long tupleCount = 0;
        long reachableBytes = 0;
        int routerCount = 0;
        int shelfCount = 0;
        Span<byte> magicBytes = stackalloc byte[sizeof(uint)];
        byte[] shelfHeader = new byte[headerSize];
        byte[] routerBytes = new byte[RouterLayout.Size];

        while (pending.Count != 0)
        {
            long offset = pending.Pop();
            if (offset <= 0 || !visited.Add(offset))
                continue;

            kernel.Read(offset, magicBytes);
            uint magic = BinaryPrimitives.ReadUInt32LittleEndian(magicBytes);
            if (magic == RouterLayout.Magic)
            {
                ReadRouterPageUsingArenaCache(offset, routerBytes);
                RouterReader router = new(routerBytes);
                if (!router.IsValid)
                    throw new InvalidDataException($"{shapeName} topology assessment found an invalid router at offset {offset}.");

                routerCount++;
                reachableBytes = checked(reachableBytes + RouterLayout.Size);
                int routeCount = router.HasDirectIndex
                    ? RouterLayout.MaxOneByteRouteCount
                    : router.RouteCount;
                for (int routeIndex = 0; routeIndex < routeCount; routeIndex++)
                {
                    long target = router.GetRouteTargetAt(routeIndex);
                    if (target > 0)
                        pending.Push(target);
                }

                continue;
            }

            if (magic == TerminalIdentityRootLayout.Magic)
            {
                if (terminalShape == 0)
                    throw new InvalidDataException($"{shapeName} topology assessment found an unsupported terminal identity root at offset {offset}.");

                byte[] rootBytes = ReadTerminalIdentityRootBytes(offset);
                if (TerminalIdentityRootLayout.ReadShape(rootBytes) != terminalShape ||
                    TerminalIdentityRootLayout.ReadKeyLength(rootBytes) != terminalKeyLength ||
                    TerminalIdentityRootLayout.ReadShelfExtentSize(rootBytes) != shelfExtentSize)
                {
                    throw new InvalidDataException($"{shapeName} topology assessment found an invalid fixed-key terminal root at offset {offset}.");
                }

                reachableBytes = checked(reachableBytes + TerminalIdentityRootLayout.Size);
                long terminalShelfOffset = TerminalIdentityRootLayout.ReadFirstShelfOffset(rootBytes);
                while (terminalShelfOffset != 0)
                {
                    if (!visited.Add(terminalShelfOffset))
                        throw new InvalidDataException($"{shapeName} topology assessment found a repeated terminal identity shelf at offset {terminalShelfOffset}.");

                    int terminalItemCount;
                    long nextTerminalShelfOffset;
                    if (terminalShape == TerminalIdentityRootLayout.ShapeFixedKeyScalar8Identity)
                    {
                        byte[] terminalShelfBytes = ReadTerminalIdentity8ShelfBytesCached(terminalShelfOffset, shelfExtentSize);
                        ValidateTerminalIdentity8Shelf(terminalShelfBytes, shelfExtentSize);
                        terminalItemCount = TerminalIdentity8ShelfLayout.ReadItemCount(terminalShelfBytes);
                        nextTerminalShelfOffset = TerminalIdentity8ShelfLayout.ReadNextShelfOffset(terminalShelfBytes);
                    }
                    else
                    {
                        byte[] terminalShelfBytes = ReadTerminalVarIdentityShelfBytesCached(terminalShelfOffset, shelfExtentSize);
                        TerminalVarIdentityShelfLayout.Validate(terminalShelfBytes, shelfExtentSize);
                        terminalItemCount = TerminalVarIdentityShelfLayout.ReadItemCount(terminalShelfBytes);
                        for (int identityIndex = 0; identityIndex < terminalItemCount; identityIndex++)
                        {
                            if (TerminalVarIdentityShelfLayout.ReadIdentityAt(terminalShelfBytes, identityIndex).Length != 16)
                                throw new InvalidDataException($"{shapeName} topology assessment terminal shelf {terminalShelfOffset} contains a non-16-byte identity.");
                        }

                        nextTerminalShelfOffset = TerminalVarIdentityShelfLayout.ReadNextShelfOffset(terminalShelfBytes);
                    }

                    tupleCount = checked(tupleCount + terminalItemCount);
                    reachableBytes = checked(reachableBytes + shelfExtentSize);
                    shelfCount++;
                    terminalShelfOffset = nextTerminalShelfOffset;
                }

                continue;
            }

            if (magic != shelfMagic)
            {
                throw new InvalidDataException(
                    $"{shapeName} topology assessment found unsupported target magic 0x{magic:X8} at offset {offset}.");
            }

            kernel.Read(offset, shelfHeader);
            if (BinaryPrimitives.ReadUInt16LittleEndian(shelfHeader.AsSpan(Scalar8Scalar8Layout.FormatVersionOffset, sizeof(ushort))) != formatVersion ||
                BinaryPrimitives.ReadUInt16LittleEndian(shelfHeader.AsSpan(Scalar8Scalar8Layout.HeaderSizeOffset, sizeof(ushort))) != headerSize)
            {
                throw new InvalidDataException($"{shapeName} topology assessment found an invalid shelf header at offset {offset}.");
            }

            ushort itemCount = BinaryPrimitives.ReadUInt16LittleEndian(
                shelfHeader.AsSpan(Scalar8Scalar8Layout.ItemCountOffset, sizeof(ushort)));
            if (itemCount > maxItemCount)
            {
                throw new InvalidDataException(
                    $"{shapeName} topology assessment shelf {offset} reports {itemCount} items above its {maxItemCount} item capacity.");
            }

            tupleCount = checked(tupleCount + itemCount);
            reachableBytes = checked(reachableBytes + shelfExtentSize);
            shelfCount++;
        }

        return new OrdinaryFixedScalarTopologyAssessment(
            tupleCount,
            reachableBytes,
            routerCount,
            shelfCount,
            shelfExtentSize,
            visited.ToArray());
    }
}

/// <summary>
/// Carries exact current-generation physical accounting for one ordinary fixed-scalar topology.<br/>
/// The represented families contain only routers and ordinary fixed shelves; duplicate-run and terminal counts are therefore structurally zero.<br/>
/// </summary>
internal readonly record struct OrdinaryFixedScalarTopologyAssessment(
    long TupleCount,
    long ReachableBytes,
    int RouterCount,
    int ShelfCount,
    int ShelfExtentSize,
    long[] ClaimedOffsets);
