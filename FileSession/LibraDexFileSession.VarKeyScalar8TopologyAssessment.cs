using System.Buffers.Binary;
using LibraDex.Layouts;
using LibraDex.Views;

namespace LibraDex;

internal sealed partial class LibraDexFileSession
{
    /// <summary>
    /// Walks one active string-backed `VS8` generation plus its null/empty scalar-8 identity routes and returns exact reachable storage.<br/>
    /// Shelf extent sizes are read from validated persisted headers because variable-key shelves may grow through multiple allocation classes within one topology.<br/>
    /// Every distinct router, shelf, terminal root, terminal shelf, and key-state root is counted once; invalid or aliased key-state structures fail instead of producing an estimate.<br/>
    /// </summary>
    /// <param name="slotIndex">The active fixed-directory slot whose current root generation should be assessed.<br/></param>
    /// <param name="maxKeyLength">The persisted maximum encoded key length used to validate ordinary and terminal key metadata.<br/></param>
    /// <param name="nullKeyRouteOffset">The active null-key identity route root, or zero when absent.<br/></param>
    /// <param name="emptyKeyRouteOffset">The active empty-key identity route root, or zero when absent.<br/></param>
    /// <returns>Exact current-generation topology, key-state, tuple, and reclaimable-payload accounting.<br/></returns>
    internal VarKeyScalar8TopologyAssessment AssessVarKeyScalar8Topology(
        int slotIndex,
        int maxKeyLength,
        long nullKeyRouteOffset,
        long emptyKeyRouteOffset)
    {
        if (!TryFindIndexDirectorySlot(slotIndex, out IndexDirectorySlotSnapshot slot))
            throw new InvalidDataException($"VS8 topology assessment slot {slotIndex} is not active.");
        if (slot.RootRouterOffset <= 0)
            throw new InvalidDataException($"VS8 topology assessment slot {slotIndex} has no positive root offset.");
        if (maxKeyLength <= 0)
            throw new InvalidDataException($"VS8 topology assessment slot {slotIndex} has no positive maximum key length.");

        Stack<long> pending = new();
        HashSet<long> visited = new();
        pending.Push(slot.RootRouterOffset);
        long tupleCount = 0;
        long reachableBytes = 0;
        long reclaimablePayloadBytes = 0;
        int routerCount = 0;
        int ordinaryShelfCount = 0;
        int duplicateRunShelfCount = 0;
        int terminalRootCount = 0;
        int terminalShelfCount = 0;
        int keyStateRootCount = 0;
        Span<byte> magicBytes = stackalloc byte[sizeof(uint)];
        Span<byte> shelfHeader = stackalloc byte[VarKeyScalar8Layout.HeaderSize];
        Span<byte> terminalRootHeader = stackalloc byte[TerminalIdentityRootLayout.HeaderSize];
        Span<byte> terminalShelfHeader = stackalloc byte[TerminalIdentity8ShelfLayout.HeaderSize];
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
                    throw new InvalidDataException($"VS8 topology assessment found an invalid router at offset {offset}.");

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

            if (magic == VarKeyScalar8Layout.Magic)
            {
                kernel.Read(offset, shelfHeader);
                VarKeyScalar8AssessmentShelf shelf = ValidateVarKeyScalar8AssessmentShelf(shelfHeader, maxKeyLength, offset);
                tupleCount = checked(tupleCount + shelf.ItemCount);
                reachableBytes = checked(reachableBytes + shelf.ExtentSize);
                reclaimablePayloadBytes = checked(reclaimablePayloadBytes + shelf.ReclaimablePayloadBytes);
                ordinaryShelfCount++;
                if (shelf.IsDuplicateRun)
                {
                    duplicateRunShelfCount++;
                    if (shelf.NextShelfOffset > 0)
                        pending.Push(shelf.NextShelfOffset);
                }

                continue;
            }

            if (magic == TerminalIdentityRootLayout.Magic)
            {
                VarKeyScalar8TerminalAssessment terminal = AssessVarKeyScalar8Terminal(
                    offset,
                    maxKeyLength,
                    TerminalIdentityRootLayout.ShapeVarKey,
                    visited,
                    terminalRootHeader,
                    terminalShelfHeader);
                tupleCount = checked(tupleCount + terminal.TupleCount);
                reachableBytes = checked(reachableBytes + terminal.ReachableBytes);
                terminalRootCount++;
                terminalShelfCount = checked(terminalShelfCount + terminal.ShelfCount);
                continue;
            }

            throw new InvalidDataException(
                $"VS8 topology assessment found unsupported target magic 0x{magic:X8} at offset {offset}.");
        }

        VarKeyScalar8KeyStateAssessment nullKeyState = AssessVarKeyScalar8KeyState(
            nullKeyRouteOffset,
            "null",
            visited);
        VarKeyScalar8KeyStateAssessment emptyKeyState = AssessVarKeyScalar8KeyState(
            emptyKeyRouteOffset,
            "empty",
            visited);
        tupleCount = checked(tupleCount + nullKeyState.TupleCount + emptyKeyState.TupleCount);
        reachableBytes = checked(reachableBytes + nullKeyState.ReachableBytes + emptyKeyState.ReachableBytes);
        terminalRootCount = checked(terminalRootCount + nullKeyState.TerminalRootCount + emptyKeyState.TerminalRootCount);
        terminalShelfCount = checked(terminalShelfCount + nullKeyState.TerminalShelfCount + emptyKeyState.TerminalShelfCount);
        keyStateRootCount = checked(nullKeyState.KeyStateRootCount + emptyKeyState.KeyStateRootCount);

        return new VarKeyScalar8TopologyAssessment(
            tupleCount,
            reachableBytes,
            reclaimablePayloadBytes,
            routerCount,
            ordinaryShelfCount,
            duplicateRunShelfCount,
            terminalRootCount,
            terminalShelfCount,
            keyStateRootCount,
            visited.ToArray());
    }

    /// <summary>
    /// Assesses one scalar-8 null/empty identity route and its optional promoted terminal child.<br/>
    /// </summary>
    /// <param name="routeOffset">The key-state route root offset, or zero when absent.<br/></param>
    /// <param name="routeName">Diagnostic route label.<br/></param>
    /// <param name="visited">The owning physical component's distinct-offset set.<br/></param>
    /// <returns>Exact key-state tuple, byte, root, and terminal-shelf accounting.<br/></returns>
    private VarKeyScalar8KeyStateAssessment AssessVarKeyScalar8KeyState(
        long routeOffset,
        string routeName,
        HashSet<long> visited)
    {
        if (routeOffset <= 0)
            return default;
        if (!visited.Add(routeOffset))
            throw new InvalidDataException($"VS8 topology assessment found an aliased {routeName} key-state route at offset {routeOffset}.");

        byte[] keyStateHeader = new byte[KeyStateIdentityRouteLayout.HeaderSize];
        kernel.Read(routeOffset, keyStateHeader);
        if (KeyStateIdentityRouteLayout.ReadMagic(keyStateHeader) != KeyStateIdentityRouteLayout.Magic ||
            KeyStateIdentityRouteLayout.ReadFormatVersion(keyStateHeader) != KeyStateIdentityRouteLayout.FormatVersion ||
            KeyStateIdentityRouteLayout.ReadHeaderSize(keyStateHeader) != KeyStateIdentityRouteLayout.HeaderSize ||
            KeyStateIdentityRouteLayout.ReadIdentitySizeCode(keyStateHeader) != KeyStateIdentityRouteLayout.Scalar8IdentityCode)
        {
            throw new InvalidDataException($"VS8 topology assessment found an invalid scalar-8 {routeName} key-state root at offset {routeOffset}.");
        }

        int itemCount = KeyStateIdentityRouteLayout.ReadItemCount(keyStateHeader);
        ushort storageKind = KeyStateIdentityRouteLayout.ReadStorageKind(keyStateHeader);
        long childRootOffset = KeyStateIdentityRouteLayout.ReadChildRootOffset(keyStateHeader);
        if (itemCount < 0)
            throw new InvalidDataException($"VS8 topology assessment found a negative {routeName} key-state count at offset {routeOffset}.");

        if (storageKind == KeyStateIdentityRouteLayout.InlineSortedStorageKind)
        {
            if (itemCount > KeyStateIdentityRouteLayout.MaxScalar8InlineItemCount || childRootOffset != 0)
                throw new InvalidDataException($"VS8 topology assessment found invalid inline {routeName} key-state metadata at offset {routeOffset}.");
            return new VarKeyScalar8KeyStateAssessment(
                itemCount,
                KeyStateIdentityRouteLayout.ExtentSize,
                KeyStateRootCount: 1,
                TerminalRootCount: 0,
                TerminalShelfCount: 0);
        }

        if (storageKind != KeyStateIdentityRouteLayout.TerminalIdentityRootStorageKind || childRootOffset <= 0)
            throw new InvalidDataException($"VS8 topology assessment found unsupported {routeName} key-state storage at offset {routeOffset}.");
        if (visited.Contains(childRootOffset))
        {
            throw new InvalidDataException(
                $"VS8 topology assessment found an aliased {routeName} key-state terminal root at offset {childRootOffset}.");
        }

        byte[] terminalRootHeader = new byte[TerminalIdentityRootLayout.HeaderSize];
        byte[] terminalShelfHeader = new byte[TerminalIdentity8ShelfLayout.HeaderSize];
        VarKeyScalar8TerminalAssessment terminal = AssessVarKeyScalar8Terminal(
            childRootOffset,
            maxKeyLength: 1,
            TerminalIdentityRootLayout.ShapeScalar8,
            visited,
            terminalRootHeader,
            terminalShelfHeader);
        if (terminal.TupleCount != itemCount)
        {
            throw new InvalidDataException(
                $"VS8 topology assessment {routeName} key-state root reports {itemCount} identities but its terminal child contains {terminal.TupleCount}.");
        }

        return new VarKeyScalar8KeyStateAssessment(
            terminal.TupleCount,
            checked(KeyStateIdentityRouteLayout.ExtentSize + terminal.ReachableBytes),
            KeyStateRootCount: 1,
            TerminalRootCount: 1,
            TerminalShelfCount: terminal.ShelfCount);
    }

    /// <summary>
    /// Validates one `VS8` shelf header and returns its exact extent, count, chain, and reclaimable-payload fields.<br/>
    /// </summary>
    /// <param name="header">The persisted shelf header bytes.<br/></param>
    /// <param name="maxKeyLength">The persisted index maximum key length.<br/></param>
    /// <param name="offset">The physical shelf offset used in diagnostic exceptions.<br/></param>
    /// <returns>Validated narrow shelf accounting.<br/></returns>
    private static VarKeyScalar8AssessmentShelf ValidateVarKeyScalar8AssessmentShelf(
        ReadOnlySpan<byte> header,
        int maxKeyLength,
        long offset)
    {
        if (VarKeyScalar8Layout.ReadMagic(header) != VarKeyScalar8Layout.Magic ||
            VarKeyScalar8Layout.ReadFormatVersion(header) != VarKeyScalar8Layout.FormatVersion ||
            VarKeyScalar8Layout.ReadHeaderSize(header) != VarKeyScalar8Layout.HeaderSize)
        {
            throw new InvalidDataException($"VS8 topology assessment found an invalid shelf header at offset {offset}.");
        }

        int extentSize = VarKeyScalar8Layout.ReadShelfExtentSize(header);
        VarKeyScalar8Profile profile = VarKeyScalar8Profile.Create(extentSize, maxKeyLength) with { Descending = (VarKeyScalar8Layout.ReadFlags(header) & VarKeyScalar8Layout.DescendingFlag) != 0 };
        int itemCount = VarKeyScalar8Layout.ReadItemCount(header);
        if (itemCount < 0)
            throw new InvalidDataException($"VS8 topology assessment found a negative shelf count at offset {offset}.");

        bool duplicateRun = VarKeyScalar8Layout.HasDuplicateRunFlag(header);
        long nextOffset = 0;
        if (duplicateRun)
        {
            int keyLength = VarKeyScalar8Layout.ReadDuplicateRunKeyLength(header);
            if (keyLength <= 0 || keyLength > maxKeyLength || itemCount > VarKeyScalar8Layout.GetDuplicateRunCapacity(profile, keyLength))
                throw new InvalidDataException($"VS8 topology assessment found invalid duplicate-run metadata at offset {offset}.");
            nextOffset = VarKeyScalar8Layout.ReadDuplicateRunNextOffset(header);
        }
        else
        {
            int slotStreamLength = VarKeyScalar8Layout.ReadSlotStreamLength(header);
            int slotCapacityBytes = VarKeyScalar8Layout.ReadSlotCapacityBytes(header);
            int recordArenaEnd = VarKeyScalar8Layout.ReadRecordArenaEnd(header);
            int expectedSlotCapacityBytes = VarKeyScalar8Layout.CalculateSlotCapacityBytes(extentSize);
            if (itemCount > expectedSlotCapacityBytes / VarKeyScalar8Layout.SlotSize ||
                slotStreamLength != checked(itemCount * VarKeyScalar8Layout.SlotSize) ||
                slotCapacityBytes != expectedSlotCapacityBytes ||
                recordArenaEnd < VarKeyScalar8Layout.HeaderSize + slotCapacityBytes ||
                recordArenaEnd > extentSize)
            {
                throw new InvalidDataException($"VS8 topology assessment found invalid ordinary shelf metadata at offset {offset}.");
            }
        }

        return new VarKeyScalar8AssessmentShelf(
            itemCount,
            extentSize,
            VarKeyScalar8Layout.ReadReclaimablePayloadBytes(header),
            duplicateRun,
            nextOffset);
    }

    /// <summary>
    /// Assesses one variable-key or key-state terminal identity root and all linked scalar-8 identity shelves.<br/>
    /// </summary>
    /// <param name="rootOffset">The terminal root offset.<br/></param>
    /// <param name="maxKeyLength">Maximum accepted key bytes for root validation.<br/></param>
    /// <param name="expectedShape">Expected terminal root shape code.<br/></param>
    /// <param name="visited">Catalog-component offsets already counted by the caller.<br/></param>
    /// <param name="rootHeader">Reusable terminal-root header scratch.<br/></param>
    /// <param name="shelfHeader">Reusable terminal-shelf header scratch.<br/></param>
    /// <returns>Exact terminal tuple, byte, and shelf counts.<br/></returns>
    private VarKeyScalar8TerminalAssessment AssessVarKeyScalar8Terminal(
        long rootOffset,
        int maxKeyLength,
        byte expectedShape,
        HashSet<long> visited,
        Span<byte> rootHeader,
        Span<byte> shelfHeader)
    {
        if (!visited.Contains(rootOffset) && !visited.Add(rootOffset))
            throw new InvalidDataException($"VS8 topology assessment could not claim terminal root {rootOffset}.");

        kernel.Read(rootOffset, rootHeader);
        int keyLength = TerminalIdentityRootLayout.ReadKeyLength(rootHeader);
        int shelfExtentSize = TerminalIdentityRootLayout.ReadShelfExtentSize(rootHeader);
        if (TerminalIdentityRootLayout.ReadMagic(rootHeader) != TerminalIdentityRootLayout.Magic ||
            TerminalIdentityRootLayout.ReadFormatVersion(rootHeader) != TerminalIdentityRootLayout.FormatVersion ||
            TerminalIdentityRootLayout.ReadHeaderSize(rootHeader) != TerminalIdentityRootLayout.HeaderSize ||
            TerminalIdentityRootLayout.ReadShape(rootHeader) != expectedShape ||
            keyLength <= 0 || keyLength > maxKeyLength ||
            keyLength > TerminalIdentityRootLayout.Size - TerminalIdentityRootLayout.KeyBytesOffset ||
            shelfExtentSize < TerminalIdentity8ShelfLayout.HeaderSize)
        {
            throw new InvalidDataException($"VS8 topology assessment found an incompatible terminal root at offset {rootOffset}.");
        }

        long tupleCount = 0;
        long reachableBytes = TerminalIdentityRootLayout.Size;
        int shelfCount = 0;
        long shelfOffset = TerminalIdentityRootLayout.ReadFirstShelfOffset(rootHeader);
        long tailShelfOffset = TerminalIdentityRootLayout.ReadTailShelfOffset(rootHeader);
        if (shelfOffset == 0 && tailShelfOffset != 0)
        {
            throw new InvalidDataException(
                $"VS8 topology assessment found empty terminal root {rootOffset} with nonzero tail {tailShelfOffset}.");
        }

        long lastShelfOffset = 0;
        while (shelfOffset != 0)
        {
            if (!visited.Add(shelfOffset))
                throw new InvalidDataException($"VS8 topology assessment found an aliased or cyclic terminal shelf at offset {shelfOffset}.");

            kernel.Read(shelfOffset, shelfHeader);
            if (TerminalIdentity8ShelfLayout.ReadMagic(shelfHeader) != TerminalIdentity8ShelfLayout.Magic ||
                TerminalIdentity8ShelfLayout.ReadFormatVersion(shelfHeader) != TerminalIdentity8ShelfLayout.FormatVersion ||
                TerminalIdentity8ShelfLayout.ReadHeaderSize(shelfHeader) != TerminalIdentity8ShelfLayout.HeaderSize)
            {
                throw new InvalidDataException($"VS8 topology assessment found an invalid terminal shelf at offset {shelfOffset}.");
            }

            int itemCount = TerminalIdentity8ShelfLayout.ReadItemCount(shelfHeader);
            if ((uint)itemCount > (uint)TerminalIdentity8ShelfLayout.GetCapacity(shelfExtentSize))
                throw new InvalidDataException($"VS8 topology assessment terminal shelf {shelfOffset} exceeds its recorded capacity.");

            tupleCount = checked(tupleCount + itemCount);
            reachableBytes = checked(reachableBytes + shelfExtentSize);
            shelfCount++;
            lastShelfOffset = shelfOffset;
            shelfOffset = TerminalIdentity8ShelfLayout.ReadNextShelfOffset(shelfHeader);
        }

        if (tailShelfOffset != 0 && tailShelfOffset != lastShelfOffset)
        {
            throw new InvalidDataException(
                $"VS8 topology assessment terminal root {rootOffset} records tail {tailShelfOffset}, but its reachable final shelf is {lastShelfOffset}.");
        }

        return new VarKeyScalar8TerminalAssessment(tupleCount, reachableBytes, shelfCount);
    }

    private readonly record struct VarKeyScalar8AssessmentShelf(
        int ItemCount,
        int ExtentSize,
        int ReclaimablePayloadBytes,
        bool IsDuplicateRun,
        long NextShelfOffset);

    private readonly record struct VarKeyScalar8TerminalAssessment(
        long TupleCount,
        long ReachableBytes,
        int ShelfCount);

    private readonly record struct VarKeyScalar8KeyStateAssessment(
        long TupleCount,
        long ReachableBytes,
        int KeyStateRootCount,
        int TerminalRootCount,
        int TerminalShelfCount);
}

/// <summary>
/// Carries exact current-generation physical accounting for one active string-backed `VS8` topology and its scalar-8 key-state routes.<br/>
/// </summary>
internal readonly record struct VarKeyScalar8TopologyAssessment(
    long TupleCount,
    long ReachableBytes,
    long ReclaimablePayloadBytes,
    int RouterCount,
    int OrdinaryShelfCount,
    int DuplicateRunShelfCount,
    int TerminalRootCount,
    int TerminalShelfCount,
    int KeyStateRootCount,
    long[] ClaimedOffsets);
