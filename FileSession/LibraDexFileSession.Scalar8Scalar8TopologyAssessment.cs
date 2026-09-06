using System.Buffers.Binary;
using LibraDex.Layouts;
using LibraDex.Views;

namespace LibraDex;

internal sealed partial class LibraDexFileSession
{
    /// <summary>
    /// Walks one active `SS8-8` generation and returns exact reachable topology bytes and live tuple counts.<br/>
    /// The walk follows router targets, ordinary duplicate-run chains, and terminal identity chains while counting every distinct physical extent once.<br/>
    /// This method performs no mutation and throws when a reachable target has an invalid header, incompatible shelf extent, or unsupported shape.<br/>
    /// </summary>
    /// <param name="slotIndex">The active fixed-directory slot whose current root generation should be assessed.<br/></param>
    /// <returns>Exact current-generation topology and tuple accounting.<br/></returns>
    internal Scalar8Scalar8TopologyAssessment AssessScalar8Scalar8Topology(int slotIndex)
    {
        if (!TryFindIndexDirectorySlot(slotIndex, out IndexDirectorySlotSnapshot slot))
            throw new InvalidDataException($"SS8-8 topology assessment slot {slotIndex} is not active.");

        Scalar8Scalar8Profile profile = GetScalar8Scalar8ProfileForSlot(slot);
        if (slot.RootRouterOffset <= 0)
            throw new InvalidDataException($"SS8-8 topology assessment slot {slotIndex} has no positive root offset.");

        Stack<long> pending = new();
        HashSet<long> visited = new();
        pending.Push(slot.RootRouterOffset);

        long tupleCount = 0;
        long reachableBytes = 0;
        int routerCount = 0;
        int ordinaryShelfCount = 0;
        int duplicateRunShelfCount = 0;
        int terminalRootCount = 0;
        int terminalShelfCount = 0;
        Span<byte> magicBytes = stackalloc byte[sizeof(uint)];
        Span<byte> ordinaryShelfHeader = stackalloc byte[Scalar8Scalar8Layout.HeaderSize];
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
                    throw new InvalidDataException($"SS8-8 topology assessment found an invalid router at offset {offset}.");

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

            if (magic == Scalar8Scalar8Layout.Magic)
            {
                kernel.Read(offset, ordinaryShelfHeader);
                ValidateScalar8Scalar8AssessmentShelf(ordinaryShelfHeader, profile, offset);

                ushort itemCount = Scalar8Scalar8Layout.ReadItemCount(ordinaryShelfHeader);
                bool duplicateRun = Scalar8Scalar8Layout.HasDuplicateRunFlag(ordinaryShelfHeader);
                tupleCount = checked(tupleCount + itemCount);
                ordinaryShelfCount++;
                if (duplicateRun)
                {
                    duplicateRunShelfCount++;
                    long next = Scalar8Scalar8Layout.ReadDuplicateRunNextOffset(ordinaryShelfHeader);
                    if (next > 0)
                        pending.Push(next);
                }

                reachableBytes = checked(reachableBytes + profile.ShelfExtentSize);
                continue;
            }

            if (magic == TerminalIdentityRootLayout.Magic)
            {
                kernel.Read(offset, terminalRootHeader);
                ValidateScalar8Scalar8AssessmentTerminalRoot(terminalRootHeader, profile, offset);

                terminalRootCount++;
                reachableBytes = checked(reachableBytes + TerminalIdentityRootLayout.Size);
                long shelfOffset = TerminalIdentityRootLayout.ReadFirstShelfOffset(terminalRootHeader);
                while (shelfOffset != 0)
                {
                    if (!visited.Add(shelfOffset))
                        break;

                    kernel.Read(shelfOffset, terminalShelfHeader);
                    ValidateScalar8Scalar8AssessmentTerminalShelf(terminalShelfHeader, profile, shelfOffset);
                    tupleCount = checked(tupleCount + TerminalIdentity8ShelfLayout.ReadItemCount(terminalShelfHeader));
                    terminalShelfCount++;
                    reachableBytes = checked(reachableBytes + profile.ShelfExtentSize);
                    shelfOffset = TerminalIdentity8ShelfLayout.ReadNextShelfOffset(terminalShelfHeader);
                }

                continue;
            }

            throw new InvalidDataException(
                $"SS8-8 topology assessment found unsupported target magic 0x{magic:X8} at offset {offset}.");
        }

        return new Scalar8Scalar8TopologyAssessment(
            tupleCount,
            reachableBytes,
            routerCount,
            ordinaryShelfCount,
            duplicateRunShelfCount,
            terminalRootCount,
            terminalShelfCount,
            profile.ShelfExtentSize,
            visited.ToArray());
    }

    /// <summary>
    /// Validates one ordinary or duplicate-run `SS8-8` shelf header for exact reachability accounting.<br/>
    /// </summary>
    /// <param name="header">The persisted fixed header bytes.<br/></param>
    /// <param name="profile">The directory-selected shelf profile.<br/></param>
    /// <param name="offset">The physical shelf offset used in diagnostic exceptions.<br/></param>
    private static void ValidateScalar8Scalar8AssessmentShelf(
        ReadOnlySpan<byte> header,
        Scalar8Scalar8Profile profile,
        long offset)
    {
        if (Scalar8Scalar8Layout.ReadMagic(header) != Scalar8Scalar8Layout.Magic ||
            Scalar8Scalar8Layout.ReadFormatVersion(header) != Scalar8Scalar8Layout.FormatVersion ||
            Scalar8Scalar8Layout.ReadHeaderSize(header) != Scalar8Scalar8Layout.HeaderSize)
        {
            throw new InvalidDataException($"SS8-8 topology assessment found an invalid shelf header at offset {offset}.");
        }

        ushort itemCount = Scalar8Scalar8Layout.ReadItemCount(header);
        int capacity = Scalar8Scalar8Layout.HasDuplicateRunFlag(header)
            ? Scalar8Scalar8Layout.GetDuplicateRunCapacity(profile)
            : profile.MaxItemCount;
        if (itemCount > capacity)
        {
            throw new InvalidDataException(
                $"SS8-8 topology assessment shelf {offset} reports {itemCount} items above its {capacity} item capacity.");
        }
    }

    /// <summary>
    /// Validates one `SS8-8` terminal identity root and its recorded shelf extent.<br/>
    /// </summary>
    /// <param name="header">The persisted terminal-root header bytes.<br/></param>
    /// <param name="profile">The directory-selected ordinary and terminal shelf profile.<br/></param>
    /// <param name="offset">The physical root offset used in diagnostic exceptions.<br/></param>
    private static void ValidateScalar8Scalar8AssessmentTerminalRoot(
        ReadOnlySpan<byte> header,
        Scalar8Scalar8Profile profile,
        long offset)
    {
        if (TerminalIdentityRootLayout.ReadMagic(header) != TerminalIdentityRootLayout.Magic ||
            TerminalIdentityRootLayout.ReadFormatVersion(header) != TerminalIdentityRootLayout.FormatVersion ||
            TerminalIdentityRootLayout.ReadHeaderSize(header) != TerminalIdentityRootLayout.HeaderSize ||
            TerminalIdentityRootLayout.ReadShape(header) != TerminalIdentityRootLayout.ShapeScalar8 ||
            TerminalIdentityRootLayout.ReadKeyLength(header) != Scalar8Scalar8Layout.KeySize ||
            TerminalIdentityRootLayout.ReadShelfExtentSize(header) != profile.ShelfExtentSize)
        {
            throw new InvalidDataException($"SS8-8 topology assessment found an incompatible terminal root at offset {offset}.");
        }
    }

    /// <summary>
    /// Validates one terminal scalar-8 identity shelf before counting its complete fixed extent.<br/>
    /// </summary>
    /// <param name="header">The persisted terminal-shelf header bytes.<br/></param>
    /// <param name="profile">The directory-selected terminal shelf profile.<br/></param>
    /// <param name="offset">The physical shelf offset used in diagnostic exceptions.<br/></param>
    private static void ValidateScalar8Scalar8AssessmentTerminalShelf(
        ReadOnlySpan<byte> header,
        Scalar8Scalar8Profile profile,
        long offset)
    {
        if (TerminalIdentity8ShelfLayout.ReadMagic(header) != TerminalIdentity8ShelfLayout.Magic ||
            TerminalIdentity8ShelfLayout.ReadFormatVersion(header) != TerminalIdentity8ShelfLayout.FormatVersion ||
            TerminalIdentity8ShelfLayout.ReadHeaderSize(header) != TerminalIdentity8ShelfLayout.HeaderSize)
        {
            throw new InvalidDataException($"SS8-8 topology assessment found an invalid terminal shelf at offset {offset}.");
        }

        int itemCount = TerminalIdentity8ShelfLayout.ReadItemCount(header);
        int capacity = TerminalIdentity8ShelfLayout.GetCapacity(profile.ShelfExtentSize);
        if ((uint)itemCount > (uint)capacity)
        {
            throw new InvalidDataException(
                $"SS8-8 topology assessment terminal shelf {offset} reports {itemCount} items above its {capacity} item capacity.");
        }
    }
}

/// <summary>
/// Carries exact current-generation physical accounting for one active `SS8-8` topology.<br/>
/// </summary>
internal readonly record struct Scalar8Scalar8TopologyAssessment(
    long TupleCount,
    long ReachableBytes,
    int RouterCount,
    int OrdinaryShelfCount,
    int DuplicateRunShelfCount,
    int TerminalRootCount,
    int TerminalShelfCount,
    int ShelfExtentSize,
    long[] ClaimedOffsets);
