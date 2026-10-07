using System.Buffers.Binary;
using LibraDex.Layouts;
using LibraDex.Views;

namespace LibraDex;

internal sealed partial class LibraDexFileSession
{
    private const int VarKeyScalar16TerminalIdentitySize = 16;

    /// <summary>
    /// Inserts one encoded scalar-16 identity into an exact-key `VS16` terminal route.<br/>
    /// The terminal root stores the variable key once while the shared variable-identity shelf chain stores each scalar identity as its canonical 16-byte big-endian representation.<br/>
    /// Tail append and shelf-local ordered insertion are attempted before the bounded full-chain rewrite fallback.<br/>
    /// </summary>
    /// <param name="pathTarget">The classified route path that reached the terminal root.<br/></param>
    /// <param name="maxKeyLength">The maximum raw key length accepted by the owning index.<br/></param>
    /// <param name="key">The exact raw key expected on the terminal root.<br/></param>
    /// <param name="encodedIdentityHigh">The high encoded identity half.<br/></param>
    /// <param name="encodedIdentityLow">The low encoded identity half.<br/></param>
    /// <param name="allowDuplicateKeys">Whether multiple distinct identities may share the same key.<br/></param>
    /// <returns>The routed insert result for the terminal mutation.<br/></returns>
    private VarKeyScalar16RoutedInsertResult InsertIntoVarKeyScalar16TerminalIdentityRoute(
        VarKeyScalar16RoutePathTarget pathTarget,
        int maxKeyLength,
        ReadOnlySpan<byte> key,
        ulong encodedIdentityHigh,
        ulong encodedIdentityLow,
        bool allowDuplicateKeys)
    {
        if ((uint)key.Length == 0 || key.Length > maxKeyLength)
        {
            return CreateVarKeyScalar16TerminalResult(
                VarKeyScalar16RoutedInsertKind.Invalid,
                VarKeyScalar16InsertResult.Invalid,
                pathTarget,
                0,
                0,
                default);
        }

        if (!allowDuplicateKeys)
        {
            return CreateVarKeyScalar16TerminalResult(
                VarKeyScalar16RoutedInsertKind.KeyConflict,
                VarKeyScalar16InsertResult.KeyConflict,
                pathTarget,
                0,
                0,
                default);
        }

        byte[] rootBytes = ReadTerminalIdentityRootBytes(pathTarget.Target.Offset);
        if (TerminalIdentityRootLayout.ReadShape(rootBytes) != TerminalIdentityRootLayout.ShapeVarKeyScalar16Identity)
        {
            throw new InvalidDataException("The routed VS16 terminal target has the wrong terminal root shape.");
        }

        if (!IsTerminalIdentityRootForKey(rootBytes, key, out long firstShelfOffset))
        {
            return SplitMismatchedVarKeyScalar16TerminalIdentityRoute(
                pathTarget,
                maxKeyLength,
                key,
                encodedIdentityHigh,
                encodedIdentityLow,
                allowDuplicateKeys);
        }

        Span<byte> identity = stackalloc byte[VarKeyScalar16TerminalIdentitySize];
        WriteVarKeyScalar16TerminalIdentity(identity, encodedIdentityHigh, encodedIdentityLow);
        int shelfExtentSize = TerminalIdentityRootLayout.ReadShelfExtentSize(rootBytes);
        bool descending = rootBytes[TerminalIdentityRootLayout.SortDirectionOffset] != 0;
        long tailShelfOffset = TerminalIdentityRootLayout.ReadTailShelfOffset(rootBytes);
        if (TryAppendScalar8VarIdentityTerminalTail(
            pathTarget.Target.Offset,
            rootBytes,
            firstShelfOffset,
            tailShelfOffset,
            shelfExtentSize,
            identity,
            out DataKernelCommitTelemetry appendTelemetry))
        {
            return CreateVarKeyScalar16TerminalResult(
                VarKeyScalar16RoutedInsertKind.WalkedTerminalDuplicate,
                VarKeyScalar16InsertResult.Inserted,
                pathTarget,
                pathTarget.Target.Offset,
                shelfExtentSize,
                appendTelemetry);
        }

        if (TryInsertIntoScalar8VarIdentityTerminalChain(
            pathTarget.Target.Offset,
            rootBytes,
            firstShelfOffset,
            shelfExtentSize,
            identity,
            out Scalar8VarIdentityRoutedInsertResult chainResult))
        {
            return CreateVarKeyScalar16TerminalResult(
                chainResult.InsertResult == Scalar8VarIdentityInsertResult.AlreadyPresent
                    ? VarKeyScalar16RoutedInsertKind.NoOp
                    : VarKeyScalar16RoutedInsertKind.WalkedTerminalDuplicate,
                MapVarKeyScalar16TerminalInsertResult(chainResult.InsertResult),
                pathTarget,
                chainResult.NewShelfOffset,
                chainResult.TargetShelfExtentSize,
                chainResult.Commit,
                chainResult.TargetShelfItemCount);
        }

        using PooledTerminalVarIdentitySet identities = ReadScalar8VarIdentityTerminalIdentitiesPooled(
            pathTarget.Target.Offset,
            key,
            shelfExtentSize);
        int insertIndex = LowerBoundTerminalVarIdentity(identities, identity, descending);
        if (insertIndex < identities.Count && identities.ReadAt(insertIndex).SequenceEqual(identity))
        {
            return CreateVarKeyScalar16TerminalResult(
                VarKeyScalar16RoutedInsertKind.NoOp,
                VarKeyScalar16InsertResult.AlreadyPresent,
                pathTarget,
                0,
                shelfExtentSize,
                default,
                identities.Count);
        }

        identities.InsertAt(insertIndex, identity);
        DataKernelCommitTelemetry rewriteTelemetry = RewriteScalar8VarIdentityTerminalRoute(
            pathTarget.Target.Offset,
            TerminalIdentityRootLayout.ShapeVarKeyScalar16Identity,
            key,
            shelfExtentSize,
            identities);
        return CreateVarKeyScalar16TerminalResult(
            VarKeyScalar16RoutedInsertKind.WalkedTerminalDuplicate,
            VarKeyScalar16InsertResult.Inserted,
            pathTarget,
            pathTarget.Target.Offset,
            shelfExtentSize,
            rewriteTelemetry,
            identities.Count);
    }

    /// <summary>
    /// Converts one full same-key ordinary `VS16` shelf into an exact-key terminal identity route.<br/>
    /// Every scalar identity is copied once into the pooled terminal workspace in existing tuple order, and the incoming identity is merged without sorting or per-row allocation.<br/>
    /// The replacement chain leaves nonmatching branches unset so neighboring keys allocate independent cold shelves instead of sharing a fallback alias.<br/>
    /// </summary>
    /// <param name="pathTarget">The routed full ordinary shelf and its parent route.<br/></param>
    /// <param name="existingShelf">The full mutable source shelf.<br/></param>
    /// <param name="profile">The source shelf profile.<br/></param>
    /// <param name="key">The raw key shared by the source shelf and incoming tuple.<br/></param>
    /// <param name="encodedIdentityHigh">The incoming high encoded identity half.<br/></param>
    /// <param name="encodedIdentityLow">The incoming low encoded identity half.<br/></param>
    /// <param name="allowDuplicateKeys">Whether duplicate keys with distinct identities are allowed.<br/></param>
    /// <param name="result">Receives the routed result when conversion or duplicate detection handled the full shelf.<br/></param>
    /// <returns><see langword="true"/> when the shelf was handled as a same-key terminal candidate; otherwise <see langword="false"/>.<br/></returns>
    private bool TryConvertVarKeyScalar16DuplicateRunToTerminalRoute(
        VarKeyScalar16RoutePathTarget pathTarget,
        VarKeyScalar16MutableShelf existingShelf,
        VarKeyScalar16Profile profile,
        ReadOnlySpan<byte> key,
        ulong encodedIdentityHigh,
        ulong encodedIdentityLow,
        bool allowDuplicateKeys,
        out VarKeyScalar16RoutedInsertResult result)
    {
        result = default;
        if (!allowDuplicateKeys ||
            existingShelf.ItemCount == 0 ||
            !existingShelf.ReadKeyAt(0).SequenceEqual(key) ||
            !existingShelf.ReadKeyAt(existingShelf.ItemCount - 1).SequenceEqual(key))
        {
            return false;
        }

        for (int i = 1; i + 1 < existingShelf.ItemCount; i++)
        {
            if (!existingShelf.ReadKeyAt(i).SequenceEqual(key))
            {
                return false;
            }
        }

        using PooledTerminalVarIdentitySet identities = PooledTerminalVarIdentitySet.Rent(
            existingShelf.ItemCount + 1,
            checked((existingShelf.ItemCount + 1) * VarKeyScalar16TerminalIdentitySize));
        Span<byte> incomingIdentity = stackalloc byte[VarKeyScalar16TerminalIdentitySize];
        WriteVarKeyScalar16TerminalIdentity(incomingIdentity, encodedIdentityHigh, encodedIdentityLow);
        Span<byte> currentIdentity = stackalloc byte[VarKeyScalar16TerminalIdentitySize];
        bool incomingAdded = false;
        for (int i = 0; i < existingShelf.ItemCount; i++)
        {
            existingShelf.ReadIdentityAt(i, out ulong currentHigh, out ulong currentLow);
            WriteVarKeyScalar16TerminalIdentity(currentIdentity, currentHigh, currentLow);
            int order = currentIdentity.SequenceCompareTo(incomingIdentity);
            if (!incomingAdded && (profile.Descending ? order < 0 : order > 0))
            {
                identities.Add(incomingIdentity);
                incomingAdded = true;
            }
            else if (order == 0)
            {
                result = CreateVarKeyScalar16TerminalResult(
                    VarKeyScalar16RoutedInsertKind.NoOp,
                    VarKeyScalar16InsertResult.AlreadyPresent,
                    pathTarget,
                    0,
                    profile.ShelfExtentSize,
                    default,
                    existingShelf.ItemCount);
                return true;
            }

            identities.Add(currentIdentity);
        }

        if (!incomingAdded)
        {
            identities.Add(incomingIdentity);
        }

        int terminalShelfExtentSize = SelectScalar8VarIdentityTerminalShelfExtentSize(profile.ShelfExtentSize, identities);
        long terminalRootOffset = CreateScalar8VarIdentityTerminalRoute(
            TerminalIdentityRootLayout.ShapeVarKeyScalar16Identity,
            key,
            terminalShelfExtentSize,
            identities,
            profile.Descending);
        long replacementOffset = CreateVarKeyVarIdentityTerminalRouterChain(
            firstDepth: 0,
            pathTarget.Target.AllocationClassId,
            key,
            profile.MaxKeyLength,
            emptyShelfOffset: 0,
            terminalRootOffset);
        varKeyScalar16MutableBatchShelves.Remove(pathTarget.Target.Offset);
        RepointMatchingRoutes(pathTarget.ParentRouterOffset, pathTarget.Target.Offset, replacementOffset);
        kernel.StageExtentRetirement(pathTarget.Target.Offset, profile.ShelfExtentSize);
        DataKernelCommitTelemetry telemetry = CommitAndInvalidateRouterReadCache();
        ReleaseVarKeyScalar16MutableShelf(existingShelf, clearShelfBytes: true);
        result = CreateVarKeyScalar16TerminalResult(
            VarKeyScalar16RoutedInsertKind.WalkedTerminalDuplicate,
            VarKeyScalar16InsertResult.Inserted,
            pathTarget,
            replacementOffset,
            terminalShelfExtentSize,
            telemetry,
            identities.Count);
        return true;
    }

    /// <summary>
    /// Splits a `VS16` route that reached an exact-key terminal root for a different incoming key.<br/>
    /// The existing terminal root remains intact while the incoming tuple is placed in a new ordinary shelf and the shared variable-key mismatch router builder separates both targets at the first differing key byte.<br/>
    /// </summary>
    /// <param name="pathTarget">The terminal route reached by the incoming key.<br/></param>
    /// <param name="maxKeyLength">The maximum raw key length accepted by the index.<br/></param>
    /// <param name="key">The incoming nonmatching raw key.<br/></param>
    /// <param name="encodedIdentityHigh">The incoming high encoded identity half.<br/></param>
    /// <param name="encodedIdentityLow">The incoming low encoded identity half.<br/></param>
    /// <param name="allowDuplicateKeys">Whether duplicate keys with distinct identities are allowed.<br/></param>
    /// <returns>The routed insert result after the mismatch split.<br/></returns>
    private VarKeyScalar16RoutedInsertResult SplitMismatchedVarKeyScalar16TerminalIdentityRoute(
        VarKeyScalar16RoutePathTarget pathTarget,
        int maxKeyLength,
        ReadOnlySpan<byte> key,
        ulong encodedIdentityHigh,
        ulong encodedIdentityLow,
        bool allowDuplicateKeys)
    {
        byte[] rootBytes = ReadTerminalIdentityRootBytes(pathTarget.Target.Offset);
        int terminalKeyLength = TerminalIdentityRootLayout.ReadKeyLength(rootBytes);
        ReadOnlySpan<byte> terminalKey = rootBytes.AsSpan(TerminalIdentityRootLayout.KeyBytesOffset, terminalKeyLength);
        VarKeyScalar16Profile profile = VarKeyScalar16Profile.Create(4 * 1024, maxKeyLength) with
        {
            Descending = rootBytes[TerminalIdentityRootLayout.SortDirectionOffset] != 0
        };
        byte[] incomingShelf = VarKeyScalar16.CreateEmpty(profile);
        VarKeyScalar16InsertResult insertResult = VarKeyScalar16.Insert(
            incomingShelf,
            profile,
            key,
            encodedIdentityHigh,
            encodedIdentityLow,
            allowDuplicateKeys,
            out byte[] rewrittenIncomingShelf);
        if (insertResult != VarKeyScalar16InsertResult.Inserted)
        {
            return CreateVarKeyScalar16TerminalResult(
                insertResult == VarKeyScalar16InsertResult.KeyConflict
                    ? VarKeyScalar16RoutedInsertKind.KeyConflict
                    : VarKeyScalar16RoutedInsertKind.Invalid,
                insertResult,
                pathTarget,
                0,
                profile.ShelfExtentSize,
                default);
        }

        RawDataReservation shelfReservation = kernel.Reserve(profile.ShelfExtentSize);
        rewrittenIncomingShelf.CopyTo(shelfReservation.Span);
        long replacementOffset = CreateVarKeyVarIdentityTerminalMismatchRouterChain(
            firstDepth: 0,
            pathTarget.Target.AllocationClassId,
            terminalKey,
            pathTarget.Target.Offset,
            key,
            shelfReservation.Extent.Offset,
            maxKeyLength);
        RepointMatchingRoutes(pathTarget.ParentRouterOffset, pathTarget.Target.Offset, replacementOffset);
        DataKernelCommitTelemetry telemetry = CommitAndInvalidateRouterReadCache();
        return CreateVarKeyScalar16TerminalResult(
            VarKeyScalar16RoutedInsertKind.WalkedShelfTransformSplit,
            VarKeyScalar16InsertResult.Inserted,
            pathTarget,
            replacementOffset,
            profile.ShelfExtentSize,
            telemetry,
            1);
    }

    /// <summary>
    /// Creates one shape-specific routed result for a `VS16` terminal operation.<br/>
    /// Centralizing result construction keeps terminal adapters aligned with the existing routed telemetry contract without exposing terminal implementation details publicly.<br/>
    /// </summary>
    /// <param name="kind">The routed operation classification.<br/></param>
    /// <param name="insertResult">The shape-specific insertion result.<br/></param>
    /// <param name="pathTarget">The route path that selected the terminal root.<br/></param>
    /// <param name="newOffset">The newly created shelf, router, or reused terminal-root offset.<br/></param>
    /// <param name="extentSize">The target shelf extent size.<br/></param>
    /// <param name="telemetry">The DataKernel commit telemetry.<br/></param>
    /// <param name="itemCount">The target shelf or terminal identity count.<br/></param>
    /// <returns>The completed routed result.<br/></returns>
    private static VarKeyScalar16RoutedInsertResult CreateVarKeyScalar16TerminalResult(
        VarKeyScalar16RoutedInsertKind kind,
        VarKeyScalar16InsertResult insertResult,
        VarKeyScalar16RoutePathTarget pathTarget,
        long newOffset,
        int extentSize,
        DataKernelCommitTelemetry telemetry,
        int itemCount = 0)
    {
        return new VarKeyScalar16RoutedInsertResult(
            kind,
            insertResult,
            pathTarget.Target.Offset,
            newOffset,
            telemetry,
            default,
            itemCount,
            extentSize,
            pathTarget.Target.RouterDepth);
    }

    /// <summary>
    /// Maps the shared byte-terminal insertion result into the `VS16` insertion result enum.<br/>
    /// The shared terminal engine is byte-oriented; the adapter prevents that internal physical reuse from leaking into the shape-specific routed contract.<br/>
    /// </summary>
    /// <param name="result">The shared terminal insertion result.<br/></param>
    /// <returns>The equivalent `VS16` insertion result.<br/></returns>
    private static VarKeyScalar16InsertResult MapVarKeyScalar16TerminalInsertResult(Scalar8VarIdentityInsertResult result)
    {
        return result switch
        {
            Scalar8VarIdentityInsertResult.Inserted => VarKeyScalar16InsertResult.Inserted,
            Scalar8VarIdentityInsertResult.AlreadyPresent => VarKeyScalar16InsertResult.AlreadyPresent,
            Scalar8VarIdentityInsertResult.KeyConflict => VarKeyScalar16InsertResult.KeyConflict,
            Scalar8VarIdentityInsertResult.Full => VarKeyScalar16InsertResult.Full,
            _ => VarKeyScalar16InsertResult.Invalid
        };
    }

    /// <summary>
    /// Writes one scalar-16 encoded identity into its canonical lexicographically sortable terminal byte representation.<br/>
    /// Big-endian high then low halves preserve the same unsigned ordering used by ordinary `VS16` tuple comparison.<br/>
    /// </summary>
    /// <param name="destination">The 16-byte destination span.<br/></param>
    /// <param name="encodedIdentityHigh">The high encoded identity half.<br/></param>
    /// <param name="encodedIdentityLow">The low encoded identity half.<br/></param>
    private static void WriteVarKeyScalar16TerminalIdentity(Span<byte> destination, ulong encodedIdentityHigh, ulong encodedIdentityLow)
    {
        if (destination.Length < VarKeyScalar16TerminalIdentitySize)
        {
            throw new ArgumentException("The VS16 terminal identity destination must contain at least 16 bytes.", nameof(destination));
        }

        BinaryPrimitives.WriteUInt64BigEndian(destination[..sizeof(ulong)], encodedIdentityHigh);
        BinaryPrimitives.WriteUInt64BigEndian(destination.Slice(sizeof(ulong), sizeof(ulong)), encodedIdentityLow);
    }

    /// <summary>
    /// Reads one canonical 16-byte terminal identity into the encoded scalar halves used by `VS16` APIs.<br/>
    /// The method validates fixed width so a malformed variable-identity shelf cannot be interpreted as a scalar-16 terminal route.<br/>
    /// </summary>
    /// <param name="identity">The persisted terminal identity bytes.<br/></param>
    /// <param name="encodedIdentityHigh">Receives the high encoded identity half.<br/></param>
    /// <param name="encodedIdentityLow">Receives the low encoded identity half.<br/></param>
    private static void ReadVarKeyScalar16TerminalIdentity(ReadOnlySpan<byte> identity, out ulong encodedIdentityHigh, out ulong encodedIdentityLow)
    {
        if (identity.Length != VarKeyScalar16TerminalIdentitySize)
        {
            throw new InvalidDataException($"A VS16 terminal identity has {identity.Length} bytes instead of {VarKeyScalar16TerminalIdentitySize}.");
        }

        encodedIdentityHigh = BinaryPrimitives.ReadUInt64BigEndian(identity[..sizeof(ulong)]);
        encodedIdentityLow = BinaryPrimitives.ReadUInt64BigEndian(identity.Slice(sizeof(ulong), sizeof(ulong)));
    }

    /// <summary>
    /// Counts identities in one exact-key `VS16` terminal root when its stored key falls inside an inclusive raw-key range.<br/>
    /// The root key is validated once and matching routes use narrow terminal shelf-count metadata without decoding identity payloads.<br/>
    /// </summary>
    /// <param name="rootOffset">The terminal root offset to count.<br/></param>
    /// <param name="lowerKey">The inclusive lower raw key.<br/></param>
    /// <param name="upperKey">The inclusive upper raw key.<br/></param>
    /// <returns>The terminal identity count when the key is in range; otherwise zero.<br/></returns>
    private long CountVarKeyScalar16TerminalIdentityRootInRange(
        long rootOffset,
        ReadOnlySpan<byte> lowerKey,
        ReadOnlySpan<byte> upperKey)
    {
        byte[] rootBytes = ReadTerminalIdentityRootBytes(rootOffset);
        if (TerminalIdentityRootLayout.ReadShape(rootBytes) != TerminalIdentityRootLayout.ShapeVarKeyScalar16Identity)
        {
            throw new InvalidDataException("The routed VS16 range-count terminal root has the wrong shape.");
        }

        int keyLength = TerminalIdentityRootLayout.ReadKeyLength(rootBytes);
        ReadOnlySpan<byte> key = rootBytes.AsSpan(TerminalIdentityRootLayout.KeyBytesOffset, keyLength);
        if (key.SequenceCompareTo(lowerKey) < 0 || key.SequenceCompareTo(upperKey) > 0)
        {
            return 0;
        }

        return CountTerminalVarIdentityRootNarrow(rootOffset, TerminalIdentityRootLayout.ShapeVarKeyScalar16Identity);
    }

    /// <summary>
    /// Copies encoded scalar-16 identities from one exact-key terminal route into caller-owned high/low output spans.<br/>
    /// The terminal key is tested once against the inclusive range and each persisted identity is decoded directly from its 16-byte canonical representation without materializing tuples.<br/>
    /// </summary>
    /// <param name="rootOffset">The terminal root offset to read.<br/></param>
    /// <param name="lowerKey">The inclusive lower raw key.<br/></param>
    /// <param name="upperKey">The inclusive upper raw key.<br/></param>
    /// <param name="encodedIdentityHighs">The caller-owned high-half destination.<br/></param>
    /// <param name="encodedIdentityLows">The caller-owned low-half destination.<br/></param>
    /// <param name="copied">The number of identities already present in both destinations.<br/></param>
    /// <returns>The updated copied count.<br/></returns>
    private int CopyVarKeyScalar16TerminalIdentityRange(
        long rootOffset,
        ReadOnlySpan<byte> lowerKey,
        ReadOnlySpan<byte> upperKey,
        Span<ulong> encodedIdentityHighs,
        Span<ulong> encodedIdentityLows,
        int copied)
    {
        byte[] rootBytes = ReadTerminalIdentityRootBytes(rootOffset);
        if (TerminalIdentityRootLayout.ReadShape(rootBytes) != TerminalIdentityRootLayout.ShapeVarKeyScalar16Identity)
        {
            throw new InvalidDataException("The routed VS16 range-read terminal root has the wrong shape.");
        }

        int keyLength = TerminalIdentityRootLayout.ReadKeyLength(rootBytes);
        ReadOnlySpan<byte> key = rootBytes.AsSpan(TerminalIdentityRootLayout.KeyBytesOffset, keyLength);
        if (key.SequenceCompareTo(lowerKey) < 0 || key.SequenceCompareTo(upperKey) > 0)
        {
            return copied;
        }

        int shelfExtentSize = TerminalIdentityRootLayout.ReadShelfExtentSize(rootBytes);
        long shelfOffset = TerminalIdentityRootLayout.ReadFirstShelfOffset(rootBytes);
        while (shelfOffset != 0 && copied < encodedIdentityHighs.Length && copied < encodedIdentityLows.Length)
        {
            byte[] shelfBytes = ReadTerminalVarIdentityShelfBytes(shelfOffset, shelfExtentSize);
            TerminalVarIdentityShelfLayout.Validate(shelfBytes, shelfExtentSize);
            int itemCount = TerminalVarIdentityShelfLayout.ReadItemCount(shelfBytes);
            for (int identityIndex = 0;
                identityIndex < itemCount && copied < encodedIdentityHighs.Length && copied < encodedIdentityLows.Length;
                identityIndex++)
            {
                ReadVarKeyScalar16TerminalIdentity(
                    TerminalVarIdentityShelfLayout.ReadIdentityAt(shelfBytes, identityIndex),
                    out encodedIdentityHighs[copied],
                    out encodedIdentityLows[copied]);
                copied++;
            }

            shelfOffset = TerminalVarIdentityShelfLayout.ReadNextShelfOffset(shelfBytes);
        }

        return copied;
    }

    /// <summary>
    /// Deletes every identity in one exact-key `VS16` terminal route when its stored key falls inside an inclusive range.<br/>
    /// The terminal chain is counted before one root-preserving rewrite to an empty chain, avoiding record-by-record deletion for whole-key range removal.<br/>
    /// </summary>
    /// <param name="rootOffset">The terminal root offset to clear.<br/></param>
    /// <param name="lowerKey">The inclusive lower raw key.<br/></param>
    /// <param name="upperKey">The inclusive upper raw key.<br/></param>
    /// <returns>The number of identities removed.<br/></returns>
    private long DeleteVarKeyScalar16TerminalIdentityRange(
        long rootOffset,
        ReadOnlySpan<byte> lowerKey,
        ReadOnlySpan<byte> upperKey)
    {
        byte[] rootBytes = ReadTerminalIdentityRootBytes(rootOffset);
        if (TerminalIdentityRootLayout.ReadShape(rootBytes) != TerminalIdentityRootLayout.ShapeVarKeyScalar16Identity)
        {
            throw new InvalidDataException("The routed VS16 range-delete terminal root has the wrong shape.");
        }

        int keyLength = TerminalIdentityRootLayout.ReadKeyLength(rootBytes);
        ReadOnlySpan<byte> key = rootBytes.AsSpan(TerminalIdentityRootLayout.KeyBytesOffset, keyLength);
        if (key.SequenceCompareTo(lowerKey) < 0 || key.SequenceCompareTo(upperKey) > 0)
        {
            return 0;
        }

        long deleted = CountTerminalVarIdentityRootNarrow(rootOffset, TerminalIdentityRootLayout.ShapeVarKeyScalar16Identity);
        if (deleted == 0)
        {
            return 0;
        }

        int shelfExtentSize = TerminalIdentityRootLayout.ReadShelfExtentSize(rootBytes);
        _ = RewriteScalar8VarIdentityTerminalRoute(
            rootOffset,
            TerminalIdentityRootLayout.ShapeVarKeyScalar16Identity,
            key,
            shelfExtentSize,
            ReadOnlySpan<byte[]>.Empty);
        return deleted;
    }
}
