using System.Buffers.Binary;
using LibraDex.Layouts;
using LibraDex.Views;

namespace LibraDex;

internal sealed partial class LibraDexFileSession
{
    /// <summary>
    /// Walks one active `FSN-8` generation and returns exact reachable router, shelf, tuple, and byte counts.<br/>
    /// Shelf key width is validated against catalog metadata and the supported file-backed 64 KiB profile.<br/>
    /// </summary>
    /// <param name="slotIndex">The active fixed-directory slot whose current root generation should be assessed.<br/></param>
    /// <param name="keySize">The persisted fixed encoded key width from catalog metadata.<br/></param>
    /// <returns>Exact current-generation topology and tuple accounting.<br/></returns>
    internal FixedNTopologyAssessment AssessFixedNScalar8Topology(int slotIndex, int keySize)
    {
        FixedNScalar8Profile profile = FixedNScalar8Profile.Default64KiB(keySize);
        return AssessFixedNTopology(slotIndex, profile.ShelfExtentSize, profile.MaxItemCount, keySize, FixedNTopologyKind.Scalar8);
    }

    /// <summary>
    /// Walks one active `FSN-16` generation and returns exact reachable router, shelf, tuple, and byte counts.<br/>
    /// Shelf key width is validated against catalog metadata and the supported file-backed 64 KiB profile.<br/>
    /// </summary>
    /// <param name="slotIndex">The active fixed-directory slot whose current root generation should be assessed.<br/></param>
    /// <param name="keySize">The persisted fixed encoded key width from catalog metadata.<br/></param>
    /// <returns>Exact current-generation topology and tuple accounting.<br/></returns>
    internal FixedNTopologyAssessment AssessFixedNScalar16Topology(int slotIndex, int keySize)
    {
        FixedNScalar16Profile profile = FixedNScalar16Profile.Default64KiB(keySize);
        return AssessFixedNTopology(slotIndex, profile.ShelfExtentSize, profile.MaxItemCount, keySize, FixedNTopologyKind.Scalar16);
    }

    /// <summary>
    /// Walks one active `FSN-V` generation and returns exact reachable router, shelf, tuple, and byte counts.<br/>
    /// Variable-identity shelf arena boundaries are validated against the catalog-selected fixed key width, identity cap, and supported 64 KiB extent.<br/>
    /// </summary>
    /// <param name="slotIndex">The active fixed-directory slot whose current root generation should be assessed.<br/></param>
    /// <param name="keySize">The persisted fixed encoded key width from catalog metadata.<br/></param>
    /// <param name="maxIdentityLength">The persisted maximum raw identity length from catalog metadata.<br/></param>
    /// <returns>Exact current-generation topology and tuple accounting.<br/></returns>
    internal FixedNTopologyAssessment AssessFixedNVarIdentityTopology(int slotIndex, int keySize, int maxIdentityLength)
    {
        FixedNVarIdentityProfile profile = FixedNVarIdentityProfile.Default64KiB(keySize, maxIdentityLength);
        return AssessFixedNTopology(slotIndex, profile.ShelfExtentSize, profile.MaxItemCount, keySize, FixedNTopologyKind.VarIdentity);
    }

    /// <summary>
    /// Walks one active programmable fixed-key generation and counts each distinct physical router and shelf once.<br/>
    /// The selected kind controls shape-specific shelf-header validation while routing, physical deduplication, byte accounting, and unsupported-target rejection remain centralized.<br/>
    /// </summary>
    /// <param name="slotIndex">The active fixed-directory slot whose current root generation should be assessed.<br/></param>
    /// <param name="shelfExtentSize">The complete persisted extent size of one shelf.<br/></param>
    /// <param name="maxItemCount">The maximum tuple count admitted by the selected runtime profile.<br/></param>
    /// <param name="keySize">The fixed encoded key width persisted in catalog metadata.<br/></param>
    /// <param name="kind">The programmable fixed-key shelf encoding to validate.<br/></param>
    /// <returns>Exact current-generation topology and tuple accounting.<br/></returns>
    private FixedNTopologyAssessment AssessFixedNTopology(
        int slotIndex,
        int shelfExtentSize,
        int maxItemCount,
        int keySize,
        FixedNTopologyKind kind)
    {
        string shapeName = kind switch
        {
            FixedNTopologyKind.Scalar8 => "FSN-8",
            FixedNTopologyKind.Scalar16 => "FSN-16",
            _ => "FSN-V"
        };
        if (!TryFindIndexDirectorySlot(slotIndex, out IndexDirectorySlotSnapshot slot))
            throw new InvalidDataException($"{shapeName} topology assessment slot {slotIndex} is not active.");
        if (slot.RootRouterOffset <= 0)
            throw new InvalidDataException($"{shapeName} topology assessment slot {slotIndex} has no positive root offset.");
        if (slot.KeyProfileId != 1 || slot.IdentityProfileId != 1 || slot.RouterProfileId != 1 || slot.AllocationClassId != 1)
            throw new ArgumentException($"The index-directory slot does not describe the supported file-backed {shapeName} profile.", nameof(slotIndex));

        uint shelfMagic = kind switch
        {
            FixedNTopologyKind.Scalar8 => FixedNScalar8Layout.Magic,
            FixedNTopologyKind.Scalar16 => FixedNScalar16Layout.Magic,
            _ => FixedNVarIdentityLayout.Magic
        };
        int headerSize = kind == FixedNTopologyKind.VarIdentity
            ? FixedNVarIdentityLayout.HeaderSize
            : FixedNScalar8Layout.HeaderSize;
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
                int routeCount = router.HasDirectIndex ? RouterLayout.MaxOneByteRouteCount : router.RouteCount;
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
                FixedNTerminalTopologyAssessment terminal = AssessFixedNTerminalTopology(
                    offset,
                    kind,
                    keySize,
                    shelfExtentSize,
                    visited);
                tupleCount = checked(tupleCount + terminal.TupleCount);
                reachableBytes = checked(reachableBytes + terminal.ReachableBytes);
                shelfCount = checked(shelfCount + 1 + terminal.IdentityShelfCount);
                continue;
            }

            if (magic != shelfMagic)
                throw new InvalidDataException($"{shapeName} topology assessment found unsupported target magic 0x{magic:X8} at offset {offset}.");

            kernel.Read(offset, shelfHeader);
            int itemCount = kind == FixedNTopologyKind.VarIdentity
                ? ValidateFixedNVarIdentityAssessmentShelf(shelfHeader, shelfExtentSize, maxItemCount, offset)
                : ValidateFixedNScalarAssessmentShelf(shelfHeader, keySize, maxItemCount, kind, offset);
            tupleCount = checked(tupleCount + itemCount);
            reachableBytes = checked(reachableBytes + shelfExtentSize);
            shelfCount++;
        }

        return new FixedNTopologyAssessment(tupleCount, reachableBytes, routerCount, shelfCount, shelfExtentSize, visited.ToArray());
    }

    /// <summary>
    /// Validates one `FSN-8` or `FSN-16` shelf header and returns its live tuple count.<br/>
    /// </summary>
    /// <param name="header">The persisted fixed shelf header.<br/></param>
    /// <param name="keySize">The catalog-selected fixed key width.<br/></param>
    /// <param name="maxItemCount">The selected profile's maximum tuple count.<br/></param>
    /// <param name="kind">The fixed-scalar identity width being validated.<br/></param>
    /// <param name="offset">The physical shelf offset used in diagnostics.<br/></param>
    /// <returns>The validated live tuple count.<br/></returns>
    private static int ValidateFixedNScalarAssessmentShelf(ReadOnlySpan<byte> header, int keySize, int maxItemCount, FixedNTopologyKind kind, long offset)
    {
        uint expectedMagic = kind == FixedNTopologyKind.Scalar8 ? FixedNScalar8Layout.Magic : FixedNScalar16Layout.Magic;
        if (BinaryPrimitives.ReadUInt32LittleEndian(header) != expectedMagic ||
            FixedNScalar8Layout.ReadFormatVersion(header) != FixedNScalar8Layout.FormatVersion ||
            FixedNScalar8Layout.ReadHeaderSize(header) != FixedNScalar8Layout.HeaderSize ||
            FixedNScalar8Layout.ReadKeySize(header) != keySize)
        {
            throw new InvalidDataException($"FSN scalar topology assessment found an incompatible shelf header at offset {offset}.");
        }
        int itemCount = FixedNScalar8Layout.ReadItemCount(header);
        if ((uint)itemCount > (uint)maxItemCount)
            throw new InvalidDataException($"FSN scalar topology assessment shelf {offset} reports {itemCount} items above its {maxItemCount} capacity.");
        return itemCount;
    }

    /// <summary>
    /// Validates and accounts for one fixed-N exhausted-key terminal root and every linked identity-only shelf.<br/>
    /// The root shape, key width, shelf extent, chain acyclicity, tail pointer, and per-shelf counts are proved before exact reachable bytes are returned.<br/>
    /// </summary>
    /// <param name="rootOffset">The terminal root offset already claimed by the outer topology walk.<br/></param>
    /// <param name="kind">The owning fixed-N identity family.<br/></param>
    /// <param name="keySize">The required fixed encoded key width.<br/></param>
    /// <param name="shelfExtentSize">The required identity-only shelf extent.<br/></param>
    /// <param name="visited">The shared physical-offset set used to reject aliases and cycles.<br/></param>
    /// <returns>Exact tuple, byte, and terminal-shelf accounting.<br/></returns>
    private FixedNTerminalTopologyAssessment AssessFixedNTerminalTopology(
        long rootOffset,
        FixedNTopologyKind kind,
        int keySize,
        int shelfExtentSize,
        HashSet<long> visited)
    {
        byte expectedShape = kind switch
        {
            FixedNTopologyKind.Scalar8 => TerminalIdentityRootLayout.ShapeFixedKeyScalar8Identity,
            FixedNTopologyKind.Scalar16 => TerminalIdentityRootLayout.ShapeFixedKeyScalar16Identity,
            _ => TerminalIdentityRootLayout.ShapeFixedKeyVarIdentity
        };
        byte[] rootBytes = ReadTerminalIdentityRootBytes(rootOffset);
        if (TerminalIdentityRootLayout.ReadShape(rootBytes) != expectedShape ||
            TerminalIdentityRootLayout.ReadKeyLength(rootBytes) != keySize ||
            TerminalIdentityRootLayout.ReadShelfExtentSize(rootBytes) != shelfExtentSize)
        {
            throw new InvalidDataException("The fixed-N topology assessment found an incompatible terminal identity root.");
        }

        long firstShelfOffset = TerminalIdentityRootLayout.ReadFirstShelfOffset(rootBytes);
        long expectedTailOffset = TerminalIdentityRootLayout.ReadTailShelfOffset(rootBytes);
        long currentOffset = firstShelfOffset;
        long actualTailOffset = 0;
        long tupleCount = 0;
        int shelfCount = 0;
        while (currentOffset != 0)
        {
            if (!visited.Add(currentOffset))
                throw new InvalidDataException("The fixed-N topology assessment found an aliased or cyclic terminal identity shelf.");

            long nextOffset;
            int itemCount;
            if (kind == FixedNTopologyKind.Scalar8)
            {
                byte[] shelfBytes = ReadTerminalIdentity8ShelfBytesCached(currentOffset, shelfExtentSize);
                ValidateTerminalIdentity8Shelf(shelfBytes, shelfExtentSize);
                itemCount = TerminalIdentity8ShelfLayout.ReadItemCount(shelfBytes);
                nextOffset = TerminalIdentity8ShelfLayout.ReadNextShelfOffset(shelfBytes);
            }
            else
            {
                byte[] shelfBytes = ReadTerminalVarIdentityShelfBytesCached(currentOffset, shelfExtentSize);
                TerminalVarIdentityShelfLayout.Validate(shelfBytes, shelfExtentSize);
                itemCount = TerminalVarIdentityShelfLayout.ReadItemCount(shelfBytes);
                nextOffset = TerminalVarIdentityShelfLayout.ReadNextShelfOffset(shelfBytes);
            }

            tupleCount = checked(tupleCount + itemCount);
            shelfCount++;
            actualTailOffset = currentOffset;
            currentOffset = nextOffset;
        }

        if (actualTailOffset != expectedTailOffset)
            throw new InvalidDataException("The fixed-N topology assessment terminal root tail pointer does not match the reachable final identity shelf.");

        return new FixedNTerminalTopologyAssessment(
            tupleCount,
            checked(TerminalIdentityRootLayout.Size + ((long)shelfCount * shelfExtentSize)),
            shelfCount);
    }

    /// <summary>
    /// Validates one `FSN-V` shelf header and returns its live tuple count.<br/>
    /// Arena and slot bounds are checked before the complete fixed extent is counted as reachable.<br/>
    /// </summary>
    /// <param name="header">The persisted variable-identity shelf header.<br/></param>
    /// <param name="shelfExtentSize">The catalog-selected shelf extent size.<br/></param>
    /// <param name="maxItemCount">The selected profile's maximum reserved slot count.<br/></param>
    /// <param name="offset">The physical shelf offset used in diagnostics.<br/></param>
    /// <returns>The validated live tuple count.<br/></returns>
    private static int ValidateFixedNVarIdentityAssessmentShelf(ReadOnlySpan<byte> header, int shelfExtentSize, int maxItemCount, long offset)
    {
        int itemCount = FixedNVarIdentityLayout.ReadItemCount(header);
        int slotCapacityBytes = FixedNVarIdentityLayout.ReadSlotCapacityBytes(header);
        int slotStreamLength = FixedNVarIdentityLayout.ReadSlotStreamLength(header);
        int recordArenaEnd = FixedNVarIdentityLayout.ReadRecordArenaEnd(header);
        if (FixedNVarIdentityLayout.ReadMagic(header) != FixedNVarIdentityLayout.Magic ||
            FixedNVarIdentityLayout.ReadFormatVersion(header) != FixedNVarIdentityLayout.FormatVersion ||
            FixedNVarIdentityLayout.ReadHeaderSize(header) != FixedNVarIdentityLayout.HeaderSize ||
            FixedNVarIdentityLayout.ReadShelfExtentSize(header) != shelfExtentSize ||
            itemCount < 0 || itemCount > maxItemCount ||
            slotCapacityBytes < 0 || slotStreamLength < 0 || slotStreamLength > slotCapacityBytes ||
            recordArenaEnd < FixedNVarIdentityLayout.HeaderSize + slotCapacityBytes || recordArenaEnd > shelfExtentSize)
        {
            throw new InvalidDataException($"FSN-V topology assessment found an incompatible shelf header at offset {offset}.");
        }
        return itemCount;
    }

    private enum FixedNTopologyKind : byte
    {
        Scalar8,
        Scalar16,
        VarIdentity
    }
}

/// <summary>
/// Carries exact current-generation physical accounting for one programmable fixed-key topology.<br/>
/// </summary>
internal readonly record struct FixedNTopologyAssessment(
    long TupleCount,
    long ReachableBytes,
    int RouterCount,
    int ShelfCount,
    int ShelfExtentSize,
    long[] ClaimedOffsets);

/// <summary>
/// Carries exact accounting for one fixed-N exhausted-key terminal root and its linked identity shelves.<br/>
/// </summary>
/// <param name="TupleCount">The number of identities linked from the terminal root.<br/></param>
/// <param name="ReachableBytes">The terminal root page plus every reachable identity-shelf extent.<br/></param>
/// <param name="IdentityShelfCount">The number of linked identity-only shelves.<br/></param>
internal readonly record struct FixedNTerminalTopologyAssessment(long TupleCount, long ReachableBytes, int IdentityShelfCount);
