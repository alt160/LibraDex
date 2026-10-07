using System.Buffers.Binary;
using LibraDex.Layouts;
using LibraDex.Views;

namespace LibraDex;

internal sealed partial class LibraDexFileSession
{
    /// <summary>
    /// Walks one active `VS16` generation plus its scalar-16 null and empty key-state routes and returns exact reachable storage.<br/>
    /// Ordinary shelf extents are read from validated persisted headers so mixed allocation classes remain exact.<br/>
    /// </summary>
    /// <param name="slotIndex">The active directory slot whose current root generation should be assessed.<br/></param>
    /// <param name="maxKeyLength">The persisted maximum encoded key length.<br/></param>
    /// <param name="nullKeyRouteOffset">The scalar-16 null-key route root, or zero when absent.<br/></param>
    /// <param name="emptyKeyRouteOffset">The scalar-16 empty-key route root, or zero when absent.<br/></param>
    /// <returns>Exact current-generation router, shelf, key-state, tuple, and byte accounting.<br/></returns>
    internal VariableTopologyAssessment AssessVarKeyScalar16Topology(
        int slotIndex,
        int maxKeyLength,
        long nullKeyRouteOffset,
        long emptyKeyRouteOffset)
    {
        VariableTopologyAssessment ordinary = AssessVariableTopology(
            slotIndex,
            VariableTopologyKind.VarKeyScalar16,
            maxKeyLength,
            maxIdentityLength: KeyStateIdentityRouteLayout.Scalar16IdentitySize);
        HashSet<long> claimed = new(ordinary.ClaimedOffsets);
        VariableKeyStateTopologyAssessment nullRoute = AssessVariableScalar16KeyState(nullKeyRouteOffset, "null", claimed);
        VariableKeyStateTopologyAssessment emptyRoute = AssessVariableScalar16KeyState(emptyKeyRouteOffset, "empty", claimed);
        return ordinary with
        {
            TupleCount = checked(ordinary.TupleCount + nullRoute.TupleCount + emptyRoute.TupleCount),
            ReachableBytes = checked(ordinary.ReachableBytes + nullRoute.ReachableBytes + emptyRoute.ReachableBytes),
            TerminalRootCount = checked(ordinary.TerminalRootCount + nullRoute.TerminalRootCount + emptyRoute.TerminalRootCount),
            TerminalShelfCount = checked(ordinary.TerminalShelfCount + nullRoute.TerminalShelfCount + emptyRoute.TerminalShelfCount),
            KeyStateRootCount = checked(nullRoute.KeyStateRootCount + emptyRoute.KeyStateRootCount),
            ClaimedOffsets = claimed.ToArray()
        };
    }

    /// <summary>
    /// Walks one active `VV` generation, including terminal variable-identity roots created for exhausted duplicate keys.<br/>
    /// </summary>
    /// <param name="slotIndex">The active directory slot whose current root generation should be assessed.<br/></param>
    /// <param name="maxKeyLength">The persisted maximum encoded key length.<br/></param>
    /// <param name="maxIdentityLength">The persisted maximum identity byte length.<br/></param>
    /// <returns>Exact current-generation topology, tuple, reclaimable-payload, and byte accounting.<br/></returns>
    internal VariableTopologyAssessment AssessVarKeyVarIdentityTopology(int slotIndex, int maxKeyLength, int maxIdentityLength)
        => AssessVariableTopology(slotIndex, VariableTopologyKind.VarKeyVarIdentity, maxKeyLength, maxIdentityLength);

    /// <summary>
    /// Walks one active `SV8` generation, including overflow chains and terminal variable-identity roots.<br/>
    /// </summary>
    /// <param name="slotIndex">The active directory slot whose current root generation should be assessed.<br/></param>
    /// <param name="maxIdentityLength">The persisted maximum identity byte length.<br/></param>
    /// <returns>Exact current-generation topology, tuple, reclaimable-payload, and byte accounting.<br/></returns>
    internal VariableTopologyAssessment AssessScalar8VarIdentityTopology(int slotIndex, int maxIdentityLength)
        => AssessVariableTopology(slotIndex, VariableTopologyKind.Scalar8VarIdentity, maxKeyLength: Scalar8VarIdentityLayout.KeySize, maxIdentityLength);

    /// <summary>
    /// Walks one active `SV16` generation, including every linked overflow shelf.<br/>
    /// </summary>
    /// <param name="slotIndex">The active directory slot whose current root generation should be assessed.<br/></param>
    /// <param name="maxIdentityLength">The persisted maximum identity byte length.<br/></param>
    /// <returns>Exact current-generation topology, tuple, reclaimable-payload, and byte accounting.<br/></returns>
    internal VariableTopologyAssessment AssessScalar16VarIdentityTopology(int slotIndex, int maxIdentityLength)
        => AssessVariableTopology(slotIndex, VariableTopologyKind.Scalar16VarIdentity, maxKeyLength: Scalar16VarIdentityLayout.KeySize, maxIdentityLength);

    /// <summary>
    /// Walks routers and family-specific variable shelves from one active root while counting each physical extent once.<br/>
    /// The family discriminator controls ordinary shelf validation, overflow-chain traversal, and accepted terminal-root shape.<br/>
    /// </summary>
    /// <param name="slotIndex">The active directory slot whose current root generation should be assessed.<br/></param>
    /// <param name="kind">The exact physical variable topology expected below the root.<br/></param>
    /// <param name="maxKeyLength">The persisted maximum encoded key length or fixed scalar key width.<br/></param>
    /// <param name="maxIdentityLength">The persisted maximum identity byte length.<br/></param>
    /// <returns>Exact current-generation topology and the distinct offsets claimed by the walk.<br/></returns>
    private VariableTopologyAssessment AssessVariableTopology(
        int slotIndex,
        VariableTopologyKind kind,
        int maxKeyLength,
        int maxIdentityLength)
    {
        if (!TryFindIndexDirectorySlot(slotIndex, out IndexDirectorySlotSnapshot slot))
            throw new InvalidDataException($"{kind} topology assessment slot {slotIndex} is not active.");
        if (slot.RootRouterOffset <= 0)
            throw new InvalidDataException($"{kind} topology assessment slot {slotIndex} has no positive root offset.");
        if (maxKeyLength <= 0 || maxIdentityLength <= 0)
            throw new InvalidDataException($"{kind} topology assessment requires positive key and identity limits.");

        Stack<long> pending = new();
        HashSet<long> visited = new();
        pending.Push(slot.RootRouterOffset);
        long tupleCount = 0;
        long reachableBytes = 0;
        long reclaimablePayloadBytes = 0;
        int routerCount = 0;
        int ordinaryShelfCount = 0;
        int overflowShelfCount = 0;
        int terminalRootCount = 0;
        int terminalShelfCount = 0;
        Span<byte> magicBytes = stackalloc byte[sizeof(uint)];
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
                    throw new InvalidDataException($"{kind} topology assessment found an invalid router at offset {offset}.");

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

            if (IsVariableOrdinaryShelfMagic(kind, magic))
            {
                VariableAssessmentShelf shelf = ReadVariableAssessmentShelf(kind, offset, maxKeyLength, maxIdentityLength);
                tupleCount = checked(tupleCount + shelf.ItemCount);
                reachableBytes = checked(reachableBytes + shelf.ExtentSize);
                reclaimablePayloadBytes = checked(reclaimablePayloadBytes + shelf.ReclaimablePayloadBytes);
                ordinaryShelfCount++;
                long nextOffset = shelf.NextShelfOffset;
                while (nextOffset > 0)
                {
                    if (!visited.Add(nextOffset))
                        throw new InvalidDataException($"{kind} topology assessment found an aliased or cyclic overflow shelf at offset {nextOffset}.");
                    VariableAssessmentShelf overflow = ReadVariableAssessmentShelf(kind, nextOffset, maxKeyLength, maxIdentityLength);
                    tupleCount = checked(tupleCount + overflow.ItemCount);
                    reachableBytes = checked(reachableBytes + overflow.ExtentSize);
                    reclaimablePayloadBytes = checked(reclaimablePayloadBytes + overflow.ReclaimablePayloadBytes);
                    ordinaryShelfCount++;
                    overflowShelfCount++;
                    nextOffset = overflow.NextShelfOffset;
                }
                continue;
            }

            if (magic == TerminalIdentityRootLayout.Magic && TryGetVariableTerminalShape(kind, out byte expectedShape))
            {
                VariableTerminalTopologyAssessment terminal = AssessVariableTerminalRoot(
                    offset,
                    expectedShape,
                    maxKeyLength,
                    visited);
                tupleCount = checked(tupleCount + terminal.TupleCount);
                reachableBytes = checked(reachableBytes + terminal.ReachableBytes);
                terminalRootCount++;
                terminalShelfCount = checked(terminalShelfCount + terminal.ShelfCount);
                continue;
            }

            throw new InvalidDataException($"{kind} topology assessment found unsupported target magic 0x{magic:X8} at offset {offset}.");
        }

        return new VariableTopologyAssessment(
            tupleCount,
            reachableBytes,
            reclaimablePayloadBytes,
            routerCount,
            ordinaryShelfCount,
            overflowShelfCount,
            terminalRootCount,
            terminalShelfCount,
            KeyStateRootCount: 0,
            visited.ToArray());
    }

    /// <summary>
    /// Reads and validates one ordinary variable shelf using its exact family layout.<br/>
    /// </summary>
    /// <param name="kind">The physical variable shelf family.<br/></param>
    /// <param name="offset">The shelf extent offset.<br/></param>
    /// <param name="maxKeyLength">The persisted maximum key length.<br/></param>
    /// <param name="maxIdentityLength">The persisted maximum identity length.<br/></param>
    /// <returns>Validated narrow shelf accounting and its optional overflow link.<br/></returns>
    private VariableAssessmentShelf ReadVariableAssessmentShelf(
        VariableTopologyKind kind,
        long offset,
        int maxKeyLength,
        int maxIdentityLength)
    {
        int headerSize = kind switch
        {
            VariableTopologyKind.VarKeyScalar16 => VarKeyScalar16Layout.HeaderSize,
            VariableTopologyKind.VarKeyVarIdentity => VarKeyVarIdentityLayout.HeaderSize,
            VariableTopologyKind.Scalar8VarIdentity => Scalar8VarIdentityLayout.HeaderSize,
            VariableTopologyKind.Scalar16VarIdentity => Scalar16VarIdentityLayout.HeaderSize,
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown variable topology.")
        };
        byte[] header = new byte[headerSize];
        kernel.Read(offset, header);

        return kind switch
        {
            VariableTopologyKind.VarKeyScalar16 => ValidateVarKeyScalar16AssessmentShelf(header, offset, maxKeyLength),
            VariableTopologyKind.VarKeyVarIdentity => ValidateVarKeyVarIdentityAssessmentShelf(header, offset, maxKeyLength, maxIdentityLength),
            VariableTopologyKind.Scalar8VarIdentity => ValidateScalar8VarIdentityAssessmentShelf(header, offset, maxIdentityLength),
            VariableTopologyKind.Scalar16VarIdentity => ValidateScalar16VarIdentityAssessmentShelf(header, offset, maxIdentityLength),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown variable topology.")
        };
    }

    /// <summary>
    /// Validates one `VS16` shelf header and returns exact narrow accounting.<br/>
    /// </summary>
    private static VariableAssessmentShelf ValidateVarKeyScalar16AssessmentShelf(ReadOnlySpan<byte> header, long offset, int maxKeyLength)
    {
        if (VarKeyScalar16Layout.ReadMagic(header) != VarKeyScalar16Layout.Magic ||
            VarKeyScalar16Layout.ReadFormatVersion(header) != VarKeyScalar16Layout.FormatVersion ||
            VarKeyScalar16Layout.ReadHeaderSize(header) != VarKeyScalar16Layout.HeaderSize)
            throw new InvalidDataException($"VS16 topology assessment found an invalid shelf header at offset {offset}.");
        int extentSize = VarKeyScalar16Layout.ReadShelfExtentSize(header);
        _ = VarKeyScalar16Profile.Create(extentSize, maxKeyLength);
        ValidateVariableAssessmentShelfFields(
            "VS16", offset, extentSize, VarKeyScalar16Layout.HeaderSize, VarKeyScalar16Layout.SlotSize,
            VarKeyScalar16Layout.ReadItemCount(header), VarKeyScalar16Layout.ReadSlotStreamLength(header),
            VarKeyScalar16Layout.ReadSlotCapacityBytes(header), VarKeyScalar16Layout.CalculateSlotCapacityBytes(extentSize),
            VarKeyScalar16Layout.ReadRecordArenaEnd(header));
        return new VariableAssessmentShelf(VarKeyScalar16Layout.ReadItemCount(header), extentSize, VarKeyScalar16Layout.ReadReclaimablePayloadBytes(header), 0);
    }

    /// <summary>
    /// Validates one `VV` shelf header and returns exact narrow accounting.<br/>
    /// </summary>
    private static VariableAssessmentShelf ValidateVarKeyVarIdentityAssessmentShelf(ReadOnlySpan<byte> header, long offset, int maxKeyLength, int maxIdentityLength)
    {
        if (VarKeyVarIdentityLayout.ReadMagic(header) != VarKeyVarIdentityLayout.Magic ||
            VarKeyVarIdentityLayout.ReadFormatVersion(header) != VarKeyVarIdentityLayout.FormatVersion ||
            VarKeyVarIdentityLayout.ReadHeaderSize(header) != VarKeyVarIdentityLayout.HeaderSize)
            throw new InvalidDataException($"VV topology assessment found an invalid shelf header at offset {offset}.");
        int extentSize = VarKeyVarIdentityLayout.ReadShelfExtentSize(header);
        _ = VarKeyVarIdentityProfile.Create(extentSize, maxKeyLength, maxIdentityLength);
        ValidateVariableAssessmentShelfFields(
            "VV", offset, extentSize, VarKeyVarIdentityLayout.HeaderSize, VarKeyVarIdentityLayout.SlotSize,
            VarKeyVarIdentityLayout.ReadItemCount(header), VarKeyVarIdentityLayout.ReadSlotStreamLength(header),
            VarKeyVarIdentityLayout.ReadSlotCapacityBytes(header), VarKeyVarIdentityLayout.CalculateSlotCapacityBytes(extentSize),
            VarKeyVarIdentityLayout.ReadRecordArenaEnd(header));
        return new VariableAssessmentShelf(VarKeyVarIdentityLayout.ReadItemCount(header), extentSize, VarKeyVarIdentityLayout.ReadReclaimablePayloadBytes(header), 0);
    }

    /// <summary>
    /// Validates one `SV8` shelf header and returns exact narrow accounting plus its overflow link.<br/>
    /// </summary>
    private static VariableAssessmentShelf ValidateScalar8VarIdentityAssessmentShelf(ReadOnlySpan<byte> header, long offset, int maxIdentityLength)
    {
        if (Scalar8VarIdentityLayout.ReadMagic(header) != Scalar8VarIdentityLayout.Magic ||
            Scalar8VarIdentityLayout.ReadFormatVersion(header) != Scalar8VarIdentityLayout.FormatVersion ||
            Scalar8VarIdentityLayout.ReadHeaderSize(header) != Scalar8VarIdentityLayout.HeaderSize)
            throw new InvalidDataException($"SV8 topology assessment found an invalid shelf header at offset {offset}.");
        int extentSize = Scalar8VarIdentityLayout.ReadShelfExtentSize(header);
        _ = Scalar8VarIdentityProfile.Create(extentSize, maxIdentityLength);
        ValidateVariableAssessmentShelfFields(
            "SV8", offset, extentSize, Scalar8VarIdentityLayout.HeaderSize, Scalar8VarIdentityLayout.SlotSize,
            Scalar8VarIdentityLayout.ReadItemCount(header), Scalar8VarIdentityLayout.ReadSlotStreamLength(header),
            Scalar8VarIdentityLayout.ReadSlotCapacityBytes(header), Scalar8VarIdentityLayout.CalculateSlotCapacityBytes(extentSize),
            Scalar8VarIdentityLayout.ReadRecordArenaEnd(header));
        return new VariableAssessmentShelf(Scalar8VarIdentityLayout.ReadItemCount(header), extentSize, Scalar8VarIdentityLayout.ReadReclaimablePayloadBytes(header), Scalar8VarIdentityLayout.ReadNextShelfOffset(header));
    }

    /// <summary>
    /// Validates one `SV16` shelf header and returns exact narrow accounting plus its overflow link.<br/>
    /// </summary>
    private static VariableAssessmentShelf ValidateScalar16VarIdentityAssessmentShelf(ReadOnlySpan<byte> header, long offset, int maxIdentityLength)
    {
        if (Scalar16VarIdentityLayout.ReadMagic(header) != Scalar16VarIdentityLayout.Magic ||
            Scalar16VarIdentityLayout.ReadFormatVersion(header) != Scalar16VarIdentityLayout.FormatVersion ||
            Scalar16VarIdentityLayout.ReadHeaderSize(header) != Scalar16VarIdentityLayout.HeaderSize)
            throw new InvalidDataException($"SV16 topology assessment found an invalid shelf header at offset {offset}.");
        int extentSize = Scalar16VarIdentityLayout.ReadShelfExtentSize(header);
        _ = Scalar16VarIdentityProfile.Create(extentSize, maxIdentityLength);
        ValidateVariableAssessmentShelfFields(
            "SV16", offset, extentSize, Scalar16VarIdentityLayout.HeaderSize, Scalar16VarIdentityLayout.SlotSize,
            Scalar16VarIdentityLayout.ReadItemCount(header), Scalar16VarIdentityLayout.ReadSlotStreamLength(header),
            Scalar16VarIdentityLayout.ReadSlotCapacityBytes(header), Scalar16VarIdentityLayout.CalculateSlotCapacityBytes(extentSize),
            Scalar16VarIdentityLayout.ReadRecordArenaEnd(header));
        return new VariableAssessmentShelf(Scalar16VarIdentityLayout.ReadItemCount(header), extentSize, Scalar16VarIdentityLayout.ReadReclaimablePayloadBytes(header), Scalar16VarIdentityLayout.ReadNextShelfOffset(header));
    }

    /// <summary>
    /// Applies shared slot-stream and record-arena invariants to one ordinary variable shelf header.<br/>
    /// </summary>
    private static void ValidateVariableAssessmentShelfFields(
        string shape,
        long offset,
        int extentSize,
        int headerSize,
        int slotSize,
        int itemCount,
        int slotStreamLength,
        int slotCapacityBytes,
        int expectedSlotCapacityBytes,
        int recordArenaEnd)
    {
        if (itemCount < 0 ||
            itemCount > expectedSlotCapacityBytes / slotSize ||
            slotStreamLength != checked(itemCount * slotSize) ||
            slotCapacityBytes != expectedSlotCapacityBytes ||
            recordArenaEnd < headerSize + slotCapacityBytes ||
            recordArenaEnd > extentSize)
        {
            throw new InvalidDataException($"{shape} topology assessment found invalid shelf metadata at offset {offset}.");
        }
    }

    /// <summary>
    /// Validates and counts one terminal variable-identity root and its linked shelves.<br/>
    /// </summary>
    private VariableTerminalTopologyAssessment AssessVariableTerminalRoot(
        long rootOffset,
        byte expectedShape,
        int maxKeyLength,
        HashSet<long> visited)
    {
        byte[] root = new byte[TerminalIdentityRootLayout.HeaderSize];
        kernel.Read(rootOffset, root);
        int keyLength = TerminalIdentityRootLayout.ReadKeyLength(root);
        int shelfExtentSize = TerminalIdentityRootLayout.ReadShelfExtentSize(root);
        if (TerminalIdentityRootLayout.ReadMagic(root) != TerminalIdentityRootLayout.Magic ||
            TerminalIdentityRootLayout.ReadFormatVersion(root) != TerminalIdentityRootLayout.FormatVersion ||
            TerminalIdentityRootLayout.ReadHeaderSize(root) != TerminalIdentityRootLayout.HeaderSize ||
            TerminalIdentityRootLayout.ReadShape(root) != expectedShape ||
            keyLength <= 0 || keyLength > maxKeyLength ||
            keyLength > TerminalIdentityRootLayout.Size - TerminalIdentityRootLayout.KeyBytesOffset ||
            shelfExtentSize < TerminalVarIdentityShelfLayout.HeaderSize)
        {
            throw new InvalidDataException($"Variable topology assessment found an invalid terminal root at offset {rootOffset}.");
        }

        long tupleCount = 0;
        long reachableBytes = TerminalIdentityRootLayout.Size;
        int shelfCount = 0;
        long lastShelfOffset = 0;
        long shelfOffset = TerminalIdentityRootLayout.ReadFirstShelfOffset(root);
        while (shelfOffset != 0)
        {
            if (!visited.Add(shelfOffset))
                throw new InvalidDataException($"Variable topology assessment found an aliased or cyclic terminal shelf at offset {shelfOffset}.");
            byte[] shelf = new byte[TerminalVarIdentityShelfLayout.HeaderSize];
            kernel.Read(shelfOffset, shelf);
            if (TerminalVarIdentityShelfLayout.ReadMagic(shelf) != TerminalVarIdentityShelfLayout.Magic ||
                TerminalVarIdentityShelfLayout.ReadFormatVersion(shelf) != TerminalVarIdentityShelfLayout.FormatVersion ||
                TerminalVarIdentityShelfLayout.ReadHeaderSize(shelf) != TerminalVarIdentityShelfLayout.HeaderSize ||
                TerminalVarIdentityShelfLayout.ReadShelfExtentSize(shelf) != shelfExtentSize)
            {
                throw new InvalidDataException($"Variable topology assessment found an invalid terminal shelf at offset {shelfOffset}.");
            }
            int itemCount = TerminalVarIdentityShelfLayout.ReadItemCount(shelf);
            ValidateVariableAssessmentShelfFields(
                "terminal variable-identity", shelfOffset, shelfExtentSize, TerminalVarIdentityShelfLayout.HeaderSize,
                TerminalVarIdentityShelfLayout.SlotSize, itemCount, TerminalVarIdentityShelfLayout.ReadSlotStreamLength(shelf),
                TerminalVarIdentityShelfLayout.ReadSlotCapacityBytes(shelf), TerminalVarIdentityShelfLayout.CalculateSlotCapacityBytes(shelfExtentSize),
                TerminalVarIdentityShelfLayout.ReadRecordArenaEnd(shelf));
            if (expectedShape == TerminalIdentityRootLayout.ShapeVarKeyScalar16Identity)
            {
                byte[] fullShelf = new byte[shelfExtentSize];
                kernel.Read(shelfOffset, fullShelf);
                TerminalVarIdentityShelfLayout.Validate(fullShelf, shelfExtentSize);
                for (int identityIndex = 0; identityIndex < itemCount; identityIndex++)
                {
                    if (TerminalVarIdentityShelfLayout.ReadIdentityAt(fullShelf, identityIndex).Length != VarKeyScalar16TerminalIdentitySize)
                        throw new InvalidDataException($"VS16 topology assessment terminal shelf {shelfOffset} contains a non-16-byte identity.");
                }
            }
            tupleCount = checked(tupleCount + itemCount);
            reachableBytes = checked(reachableBytes + shelfExtentSize);
            shelfCount++;
            lastShelfOffset = shelfOffset;
            shelfOffset = TerminalVarIdentityShelfLayout.ReadNextShelfOffset(shelf);
        }

        long tailShelfOffset = TerminalIdentityRootLayout.ReadTailShelfOffset(root);
        if ((shelfCount == 0 && tailShelfOffset != 0) || (shelfCount != 0 && tailShelfOffset != lastShelfOffset))
            throw new InvalidDataException($"Variable topology assessment terminal root {rootOffset} has inconsistent tail metadata.");
        return new VariableTerminalTopologyAssessment(tupleCount, reachableBytes, shelfCount);
    }

    /// <summary>
    /// Assesses one scalar-16 null or empty key-state route and its optional promoted terminal child.<br/>
    /// </summary>
    private VariableKeyStateTopologyAssessment AssessVariableScalar16KeyState(long routeOffset, string routeName, HashSet<long> visited)
    {
        if (routeOffset <= 0)
            return default;
        if (!visited.Add(routeOffset))
            throw new InvalidDataException($"VS16 topology assessment found an aliased {routeName} key-state route at offset {routeOffset}.");
        byte[] route = new byte[KeyStateIdentityRouteLayout.HeaderSize];
        kernel.Read(routeOffset, route);
        if (KeyStateIdentityRouteLayout.ReadMagic(route) != KeyStateIdentityRouteLayout.Magic ||
            KeyStateIdentityRouteLayout.ReadFormatVersion(route) != KeyStateIdentityRouteLayout.FormatVersion ||
            KeyStateIdentityRouteLayout.ReadHeaderSize(route) != KeyStateIdentityRouteLayout.HeaderSize ||
            KeyStateIdentityRouteLayout.ReadIdentitySizeCode(route) != KeyStateIdentityRouteLayout.Scalar16IdentityCode)
        {
            throw new InvalidDataException($"VS16 topology assessment found an invalid scalar-16 {routeName} key-state root at offset {routeOffset}.");
        }

        int itemCount = KeyStateIdentityRouteLayout.ReadItemCount(route);
        ushort storageKind = KeyStateIdentityRouteLayout.ReadStorageKind(route);
        long childRootOffset = KeyStateIdentityRouteLayout.ReadChildRootOffset(route);
        if (itemCount < 0)
            throw new InvalidDataException($"VS16 topology assessment found a negative {routeName} key-state count at offset {routeOffset}.");
        if (storageKind == KeyStateIdentityRouteLayout.InlineSortedStorageKind)
        {
            if (itemCount > KeyStateIdentityRouteLayout.MaxScalar16InlineItemCount || childRootOffset != 0)
                throw new InvalidDataException($"VS16 topology assessment found invalid inline {routeName} key-state metadata at offset {routeOffset}.");
            return new VariableKeyStateTopologyAssessment(itemCount, KeyStateIdentityRouteLayout.ExtentSize, 1, 0, 0);
        }
        if (storageKind != KeyStateIdentityRouteLayout.TerminalIdentityRootStorageKind || childRootOffset <= 0 || !visited.Add(childRootOffset))
            throw new InvalidDataException($"VS16 topology assessment found unsupported or aliased {routeName} key-state storage at offset {routeOffset}.");
        VariableTerminalTopologyAssessment terminal = AssessVariableTerminalRoot(
            childRootOffset,
            TerminalIdentityRootLayout.ShapeScalar16VarIdentity,
            maxKeyLength: 4,
            visited);
        if (terminal.TupleCount != itemCount)
            throw new InvalidDataException($"VS16 topology assessment {routeName} key-state count does not match its terminal child.");
        return new VariableKeyStateTopologyAssessment(
            terminal.TupleCount,
            checked(KeyStateIdentityRouteLayout.ExtentSize + terminal.ReachableBytes),
            1,
            1,
            terminal.ShelfCount);
    }

    private static bool IsVariableOrdinaryShelfMagic(VariableTopologyKind kind, uint magic)
        => kind switch
        {
            VariableTopologyKind.VarKeyScalar16 => magic == VarKeyScalar16Layout.Magic,
            VariableTopologyKind.VarKeyVarIdentity => magic == VarKeyVarIdentityLayout.Magic,
            VariableTopologyKind.Scalar8VarIdentity => magic == Scalar8VarIdentityLayout.Magic,
            VariableTopologyKind.Scalar16VarIdentity => magic == Scalar16VarIdentityLayout.Magic,
            _ => false
        };

    private static bool TryGetVariableTerminalShape(VariableTopologyKind kind, out byte shape)
    {
        shape = kind switch
        {
            VariableTopologyKind.VarKeyScalar16 => TerminalIdentityRootLayout.ShapeVarKeyScalar16Identity,
            VariableTopologyKind.VarKeyVarIdentity => TerminalIdentityRootLayout.ShapeVarKey,
            VariableTopologyKind.Scalar8VarIdentity => TerminalIdentityRootLayout.ShapeScalar8VarIdentity,
            _ => 0
        };
        return shape != 0;
    }

    private readonly record struct VariableAssessmentShelf(int ItemCount, int ExtentSize, int ReclaimablePayloadBytes, long NextShelfOffset);
    private readonly record struct VariableTerminalTopologyAssessment(long TupleCount, long ReachableBytes, int ShelfCount);
    private readonly record struct VariableKeyStateTopologyAssessment(long TupleCount, long ReachableBytes, int KeyStateRootCount, int TerminalRootCount, int TerminalShelfCount);
}

internal enum VariableTopologyKind
{
    VarKeyScalar16 = 1,
    VarKeyVarIdentity = 2,
    Scalar8VarIdentity = 3,
    Scalar16VarIdentity = 4
}

internal readonly record struct VariableTopologyAssessment(
    long TupleCount,
    long ReachableBytes,
    long ReclaimablePayloadBytes,
    int RouterCount,
    int OrdinaryShelfCount,
    int OverflowShelfCount,
    int TerminalRootCount,
    int TerminalShelfCount,
    int KeyStateRootCount,
    long[] ClaimedOffsets);
