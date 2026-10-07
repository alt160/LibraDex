using System.Buffers.Binary;
using LibraDex.Layouts;
using LibraDex.Views;

namespace LibraDex;

internal sealed partial class LibraDexFileSession
{
    private const int FixedScalar16TerminalIdentitySize = 16;

    /// <summary>
    /// Deletes one encoded scalar-16 identity from a fixed-key terminal route.<br/>
    /// The shared variable-identity terminal mutation rewrites only the containing shelf or its predecessor/root link and preserves every unaffected shelf.<br/>
    /// </summary>
    /// <param name="rootOffset">The terminal root selected by the fixed range cursor.<br/></param>
    /// <param name="keyBytes">The exact canonical fixed key stored by the terminal root.<br/></param>
    /// <param name="shelfExtentSize">The persisted terminal identity shelf extent size.<br/></param>
    /// <param name="encodedIdentityHigh">The encoded high identity lane.<br/></param>
    /// <param name="encodedIdentityLow">The encoded low identity lane.<br/></param>
    /// <returns>One when the exact tuple was removed; otherwise zero.<br/></returns>
    internal int DeleteFixedScalar16TerminalIdentity(
        long rootOffset,
        ReadOnlySpan<byte> keyBytes,
        int shelfExtentSize,
        ulong encodedIdentityHigh,
        ulong encodedIdentityLow)
    {
        byte[] rootBytes = ReadTerminalIdentityRootBytes(rootOffset);
        ValidateFixedScalar16TerminalRoot(rootBytes, keyBytes.Length, shelfExtentSize);
        Span<byte> identity = stackalloc byte[FixedScalar16TerminalIdentitySize];
        WriteFixedScalar16TerminalIdentity(identity, encodedIdentityHigh, encodedIdentityLow);
        return DeleteTerminalVarIdentityExactTupleLocally(
            rootOffset,
            TerminalIdentityRootLayout.ShapeFixedKeyScalar16Identity,
            keyBytes,
            identity) ? 1 : 0;
    }

    /// <summary>
    /// Deletes every identity from one exact fixed-key scalar-16 terminal route.<br/>
    /// The method publishes one empty terminal root and makes the old shelf chain unreachable instead of deleting tuples one at a time.<br/>
    /// </summary>
    /// <param name="rootOffset">The terminal root to clear.<br/></param>
    /// <param name="keyBytes">The exact canonical fixed key stored by the terminal root.<br/></param>
    /// <param name="shelfExtentSize">The persisted terminal shelf extent size.<br/></param>
    /// <returns>The number of identities removed.<br/></returns>
    internal long DeleteFixedScalar16TerminalRoute(long rootOffset, ReadOnlySpan<byte> keyBytes, int shelfExtentSize)
    {
        byte[] rootBytes = ReadTerminalIdentityRootBytes(rootOffset);
        ValidateFixedScalar16TerminalRoot(rootBytes, keyBytes.Length, shelfExtentSize);
        if (!rootBytes.AsSpan(TerminalIdentityRootLayout.KeyBytesOffset, keyBytes.Length).SequenceEqual(keyBytes))
            throw new InvalidDataException("The fixed-key scalar-16 terminal delete root does not match the requested key.");

        long deleted = CountTerminalVarIdentityRootNarrow(rootOffset, TerminalIdentityRootLayout.ShapeFixedKeyScalar16Identity);
        if (deleted == 0)
            return 0;

        _ = RewriteScalar8VarIdentityTerminalRoute(
            rootOffset,
            TerminalIdentityRootLayout.ShapeFixedKeyScalar16Identity,
            keyBytes,
            shelfExtentSize,
            ReadOnlySpan<byte[]>.Empty);
        return deleted;
    }

    /// <summary>
    /// Reads every canonical scalar-16 identity from one fixed-key terminal route in persisted order.<br/>
    /// Cursor projections use this bounded bridge while the persisted representation continues to store the key only once.<br/>
    /// </summary>
    /// <param name="rootOffset">The terminal root to read.<br/></param>
    /// <param name="keyBytes">The exact canonical fixed key stored by the terminal root.<br/></param>
    /// <param name="shelfExtentSize">The persisted terminal shelf extent size.<br/></param>
    /// <returns>Canonical 16-byte identities in the terminal root's persisted physical order.<br/></returns>
    internal byte[][] ReadFixedScalar16TerminalIdentities(long rootOffset, ReadOnlySpan<byte> keyBytes, int shelfExtentSize)
    {
        byte[] rootBytes = ReadTerminalIdentityRootBytes(rootOffset);
        ValidateFixedScalar16TerminalRoot(rootBytes, keyBytes.Length, shelfExtentSize);
        return ReadScalar8VarIdentityTerminalIdentities(rootOffset, keyBytes, shelfExtentSize);
    }

    /// <summary>
    /// Inserts one scalar-16 identity into an `SS8-16` terminal route or separates a neighboring key from that route.<br/>
    /// Matching keys reuse the shared terminal variable-identity mutation path; a different key replaces the terminal page with the shortest exact-byte router chain that separates both keys.<br/>
    /// </summary>
    private Scalar8Scalar16RoutedInsertResult InsertIntoScalar8Scalar16TerminalIdentityRoute(
        Scalar8Scalar16RoutePathTarget pathTarget,
        Scalar8Scalar16Profile profile,
        ulong encodedKey,
        ulong encodedIdentityHigh,
        ulong encodedIdentityLow,
        bool allowDuplicateKeys)
    {
        Span<byte> keyBytes = stackalloc byte[Scalar8Scalar16Layout.KeySize];
        BinaryPrimitives.WriteUInt64BigEndian(keyBytes, encodedKey);
        byte[] rootBytes = ReadTerminalIdentityRootBytes(pathTarget.Target.Offset);
        ValidateFixedScalar16TerminalRoot(rootBytes, keyBytes.Length, profile.ShelfExtentSize);
        if (!rootBytes.AsSpan(TerminalIdentityRootLayout.KeyBytesOffset, keyBytes.Length).SequenceEqual(keyBytes))
        {
            byte[] newShelfBytes = new byte[profile.ShelfExtentSize];
            Scalar8Scalar16 newShelf = new(newShelfBytes, profile);
            newShelf.Initialize();
            Scalar8Scalar16InsertResult newShelfInsert = newShelf.Insert(encodedKey, encodedIdentityHigh, encodedIdentityLow, allowDuplicateKeys);
            if (newShelfInsert != Scalar8Scalar16InsertResult.Inserted)
                throw new InvalidDataException($"Expected SS8-16 terminal-neighbor shelf insert, got {newShelfInsert}.");

            DataKernelCommitTelemetry splitTelemetry = SplitMismatchedFixedScalar8TerminalIdentityRoute(
                pathTarget.Target.Offset,
                pathTarget.Target.RouterDepth,
                pathTarget.Target.AllocationClassId,
                rootBytes,
                keyBytes,
                newShelfBytes);
            return new Scalar8Scalar16RoutedInsertResult(
                Scalar8Scalar16RoutedInsertKind.WalkedShelfTransformSplit,
                Scalar8Scalar16InsertResult.Inserted,
                pathTarget.Target.Offset,
                pathTarget.Target.Offset,
                0,
                splitTelemetry);
        }

        FixedScalar16TerminalMutationResult mutation = InsertFixedScalar16TerminalIdentity(
            pathTarget.Target.Offset,
            rootBytes,
            keyBytes,
            encodedIdentityHigh,
            encodedIdentityLow,
            allowDuplicateKeys);
        return CreateScalar8Scalar16TerminalInsertResult(pathTarget.Target.Offset, mutation.InsertResult, mutation.Commit);
    }

    /// <summary>
    /// Converts one full same-key `SS8-16` shelf plus its incoming identity into the shared fixed-key scalar-16 terminal representation.<br/>
    /// The terminal root reuses the routed shelf offset, stores the eight-byte key once, and moves sorted canonical identities into variable-identity shelves.<br/>
    /// </summary>
    private bool TryConvertScalar8Scalar16SameKeyShelfToTerminal(
        Scalar8Scalar16RoutePathTarget pathTarget,
        ReadOnlySpan<byte> existingShelfBytes,
        Scalar8Scalar16Profile profile,
        ulong encodedKey,
        ulong encodedIdentityHigh,
        ulong encodedIdentityLow,
        bool allowDuplicateKeys,
        out Scalar8Scalar16RoutedInsertResult result)
    {
        result = default;
        Scalar8Scalar16ReadOnly shelf = new(existingShelfBytes, profile);
        int count = shelf.ItemCount;
        if (!allowDuplicateKeys || count == 0 || shelf.ReadKeyAt(0) != encodedKey || shelf.ReadKeyAt(count - 1) != encodedKey)
            return false;

        using PooledTerminalVarIdentitySet identities = PooledTerminalVarIdentitySet.Rent(
            count + 1,
            checked((count + 1) * FixedScalar16TerminalIdentitySize));
        Span<byte> incoming = stackalloc byte[FixedScalar16TerminalIdentitySize];
        Span<byte> current = stackalloc byte[FixedScalar16TerminalIdentitySize];
        WriteFixedScalar16TerminalIdentity(incoming, encodedIdentityHigh, encodedIdentityLow);
        bool added = false;
        for (int i = 0; i < count; i++)
        {
            if (shelf.ReadKeyAt(i) != encodedKey)
                return false;

            WriteFixedScalar16TerminalIdentity(current, shelf.ReadIdentityHighAt(i), shelf.ReadIdentityLowAt(i));
            int order = current.SequenceCompareTo(incoming);
            if (!added && (profile.Descending ? order < 0 : order > 0))
            {
                identities.Add(incoming);
                added = true;
            }
            else if (order == 0)
            {
                result = CreateScalar8Scalar16TerminalInsertResult(pathTarget.Target.Offset, Scalar8VarIdentityInsertResult.AlreadyPresent, default);
                return true;
            }

            identities.Add(current);
        }

        if (!added)
            identities.Add(incoming);

        Span<byte> keyBytes = stackalloc byte[Scalar8Scalar16Layout.KeySize];
        BinaryPrimitives.WriteUInt64BigEndian(keyBytes, encodedKey);
        byte parentPrefix = keyBytes[pathTarget.Target.RouterDepth];
        RefineDirectShelfOwner(pathTarget.ParentRouterOffset, pathTarget.Target.Offset,
            checked((ushort)(pathTarget.Target.RouterDepth + 1)), parentPrefix, parentPrefix, parentPrefix);
        DataKernelCommitTelemetry telemetry = RewriteFixedScalar16TerminalRoute(
            pathTarget.Target.Offset,
            keyBytes,
            profile.ShelfExtentSize,
            identities,
            profile.Descending);
        result = CreateScalar8Scalar16TerminalInsertResult(pathTarget.Target.Offset, Scalar8VarIdentityInsertResult.Inserted, telemetry);
        return true;
    }

    /// <summary>
    /// Inserts one scalar-16 identity into an `SS16-16` terminal route or separates a neighboring key from that route.<br/>
    /// Matching keys use the shared identity-only shelf chain and neighboring keys return to ordinary exact-byte routing.<br/>
    /// </summary>
    private Scalar16Scalar16RoutedInsertResult InsertIntoScalar16Scalar16TerminalIdentityRoute(
        Scalar16Scalar16RoutePathTarget pathTarget,
        Scalar16Scalar16Profile profile,
        ulong encodedKeyHigh,
        ulong encodedKeyLow,
        ulong encodedIdentityHigh,
        ulong encodedIdentityLow,
        bool allowDuplicateKeys)
    {
        Span<byte> keyBytes = stackalloc byte[Scalar16Scalar16Layout.KeySize];
        BinaryPrimitives.WriteUInt64BigEndian(keyBytes, encodedKeyHigh);
        BinaryPrimitives.WriteUInt64BigEndian(keyBytes.Slice(sizeof(ulong)), encodedKeyLow);
        byte[] rootBytes = ReadTerminalIdentityRootBytes(pathTarget.Target.Offset);
        ValidateFixedScalar16TerminalRoot(rootBytes, keyBytes.Length, profile.ShelfExtentSize);
        if (!rootBytes.AsSpan(TerminalIdentityRootLayout.KeyBytesOffset, keyBytes.Length).SequenceEqual(keyBytes))
        {
            byte[] newShelfBytes = new byte[profile.ShelfExtentSize];
            Scalar16Scalar16 newShelf = new(newShelfBytes, profile);
            newShelf.Initialize();
            Scalar16Scalar16InsertResult newShelfInsert = newShelf.Insert(encodedKeyHigh, encodedKeyLow, encodedIdentityHigh, encodedIdentityLow, allowDuplicateKeys);
            if (newShelfInsert != Scalar16Scalar16InsertResult.Inserted)
                throw new InvalidDataException($"Expected SS16-16 terminal-neighbor shelf insert, got {newShelfInsert}.");

            DataKernelCommitTelemetry splitTelemetry = SplitMismatchedFixedScalar8TerminalIdentityRoute(
                pathTarget.Target.Offset,
                pathTarget.Target.RouterDepth,
                pathTarget.Target.AllocationClassId,
                rootBytes,
                keyBytes,
                newShelfBytes);
            return new Scalar16Scalar16RoutedInsertResult(
                Scalar16Scalar16RoutedInsertKind.WalkedShelfTransformSplit,
                Scalar16Scalar16InsertResult.Inserted,
                pathTarget.Target.Offset,
                pathTarget.Target.Offset,
                0,
                splitTelemetry);
        }

        FixedScalar16TerminalMutationResult mutation = InsertFixedScalar16TerminalIdentity(
            pathTarget.Target.Offset,
            rootBytes,
            keyBytes,
            encodedIdentityHigh,
            encodedIdentityLow,
            allowDuplicateKeys);
        return CreateScalar16Scalar16TerminalInsertResult(pathTarget.Target.Offset, mutation.InsertResult, mutation.Commit);
    }

    /// <summary>
    /// Converts one full same-key `SS16-16` shelf plus its incoming identity into the shared fixed-key scalar-16 terminal representation.<br/>
    /// The root stores the sixteen-byte key once and the shared terminal shelves retain strict canonical identity order.<br/>
    /// </summary>
    private bool TryConvertScalar16Scalar16SameKeyShelfToTerminal(
        Scalar16Scalar16RoutePathTarget pathTarget,
        ReadOnlySpan<byte> existingShelfBytes,
        Scalar16Scalar16Profile profile,
        ulong encodedKeyHigh,
        ulong encodedKeyLow,
        ulong encodedIdentityHigh,
        ulong encodedIdentityLow,
        bool allowDuplicateKeys,
        out Scalar16Scalar16RoutedInsertResult result)
    {
        result = default;
        Scalar16Scalar16ReadOnly shelf = new(existingShelfBytes, profile);
        int count = shelf.ItemCount;
        if (!allowDuplicateKeys || count == 0 ||
            shelf.ReadKeyHighAt(0) != encodedKeyHigh || shelf.ReadKeyLowAt(0) != encodedKeyLow ||
            shelf.ReadKeyHighAt(count - 1) != encodedKeyHigh || shelf.ReadKeyLowAt(count - 1) != encodedKeyLow)
        {
            return false;
        }

        using PooledTerminalVarIdentitySet identities = PooledTerminalVarIdentitySet.Rent(
            count + 1,
            checked((count + 1) * FixedScalar16TerminalIdentitySize));
        Span<byte> incoming = stackalloc byte[FixedScalar16TerminalIdentitySize];
        Span<byte> current = stackalloc byte[FixedScalar16TerminalIdentitySize];
        WriteFixedScalar16TerminalIdentity(incoming, encodedIdentityHigh, encodedIdentityLow);
        bool added = false;
        for (int i = 0; i < count; i++)
        {
            if (shelf.ReadKeyHighAt(i) != encodedKeyHigh || shelf.ReadKeyLowAt(i) != encodedKeyLow)
                return false;

            WriteFixedScalar16TerminalIdentity(current, shelf.ReadIdentityHighAt(i), shelf.ReadIdentityLowAt(i));
            int order = current.SequenceCompareTo(incoming);
            if (!added && (profile.Descending ? order < 0 : order > 0))
            {
                identities.Add(incoming);
                added = true;
            }
            else if (order == 0)
            {
                result = CreateScalar16Scalar16TerminalInsertResult(pathTarget.Target.Offset, Scalar8VarIdentityInsertResult.AlreadyPresent, default);
                return true;
            }

            identities.Add(current);
        }

        if (!added)
            identities.Add(incoming);

        Span<byte> keyBytes = stackalloc byte[Scalar16Scalar16Layout.KeySize];
        BinaryPrimitives.WriteUInt64BigEndian(keyBytes, encodedKeyHigh);
        BinaryPrimitives.WriteUInt64BigEndian(keyBytes.Slice(sizeof(ulong)), encodedKeyLow);
        byte parentPrefix = keyBytes[pathTarget.Target.RouterDepth];
        RefineDirectShelfOwner(pathTarget.ParentRouterOffset, pathTarget.Target.Offset,
            checked((ushort)(pathTarget.Target.RouterDepth + 1)), parentPrefix, parentPrefix, parentPrefix);
        DataKernelCommitTelemetry telemetry = RewriteFixedScalar16TerminalRoute(
            pathTarget.Target.Offset,
            keyBytes,
            profile.ShelfExtentSize,
            identities,
            profile.Descending);
        result = CreateScalar16Scalar16TerminalInsertResult(pathTarget.Target.Offset, Scalar8VarIdentityInsertResult.Inserted, telemetry);
        return true;
    }

    /// <summary>
    /// Inserts one scalar-16 identity into an `FS32-16` terminal route or separates a neighboring fixed key from that route.<br/>
    /// Matching keys reuse the shared identity-only shelves and mismatches publish only the exact router stem needed to separate both keys.<br/>
    /// </summary>
    private Fixed32Scalar16RoutedInsertResult InsertIntoFixed32Scalar16TerminalIdentityRoute(
        Fixed32Scalar16RoutePathTarget pathTarget,
        Fixed32Scalar16Profile profile,
        ulong key0,
        ulong key1,
        ulong key2,
        ulong key3,
        ulong encodedIdentityHigh,
        ulong encodedIdentityLow,
        bool allowDuplicateKeys)
    {
        Span<byte> keyBytes = stackalloc byte[Fixed32Scalar16Layout.KeySize];
        WriteFixed32Scalar8KeyBytes(keyBytes, key0, key1, key2, key3);
        byte[] rootBytes = ReadTerminalIdentityRootBytes(pathTarget.Target.Offset);
        ValidateFixedScalar16TerminalRoot(rootBytes, keyBytes.Length, profile.ShelfExtentSize);
        if (!rootBytes.AsSpan(TerminalIdentityRootLayout.KeyBytesOffset, keyBytes.Length).SequenceEqual(keyBytes))
        {
            byte[] newShelfBytes = new byte[profile.ShelfExtentSize];
            Fixed32Scalar16 newShelf = new(newShelfBytes, profile);
            newShelf.Initialize();
            Fixed32Scalar16InsertResult newShelfInsert = newShelf.Insert(key0, key1, key2, key3, encodedIdentityHigh, encodedIdentityLow, allowDuplicateKeys);
            if (newShelfInsert != Fixed32Scalar16InsertResult.Inserted)
                throw new InvalidDataException($"Expected FS32-16 terminal-neighbor shelf insert, got {newShelfInsert}.");

            DataKernelCommitTelemetry splitTelemetry = SplitMismatchedFixedScalar8TerminalIdentityRoute(
                pathTarget.Target.Offset,
                pathTarget.Target.RouterDepth,
                pathTarget.Target.AllocationClassId,
                rootBytes,
                keyBytes,
                newShelfBytes);
            return new Fixed32Scalar16RoutedInsertResult(
                Fixed32Scalar16RoutedInsertKind.WalkedShelfTransformSplit,
                Fixed32Scalar16InsertResult.Inserted,
                pathTarget.Target.Offset,
                pathTarget.Target.Offset,
                0,
                splitTelemetry);
        }

        FixedScalar16TerminalMutationResult mutation = InsertFixedScalar16TerminalIdentity(
            pathTarget.Target.Offset,
            rootBytes,
            keyBytes,
            encodedIdentityHigh,
            encodedIdentityLow,
            allowDuplicateKeys);
        return CreateFixed32Scalar16TerminalInsertResult(pathTarget.Target.Offset, mutation.InsertResult, mutation.Commit);
    }

    /// <summary>
    /// Converts one full same-key `FS32-16` shelf plus its incoming identity into the shared fixed-key scalar-16 terminal representation.<br/>
    /// The thirty-two-byte key is retained once in the root and the canonical identities remain strictly ordered below it.<br/>
    /// </summary>
    private bool TryConvertFixed32Scalar16SameKeyShelfToTerminal(
        Fixed32Scalar16RoutePathTarget pathTarget,
        ReadOnlySpan<byte> existingShelfBytes,
        Fixed32Scalar16Profile profile,
        ulong key0,
        ulong key1,
        ulong key2,
        ulong key3,
        ulong encodedIdentityHigh,
        ulong encodedIdentityLow,
        bool allowDuplicateKeys,
        out Fixed32Scalar16RoutedInsertResult result)
    {
        result = default;
        Fixed32Scalar16ReadOnly shelf = new(existingShelfBytes, profile);
        int count = shelf.ItemCount;
        if (!allowDuplicateKeys || count == 0 ||
            !Fixed32Scalar16ShelfKeyEquals(shelf, 0, key0, key1, key2, key3) ||
            !Fixed32Scalar16ShelfKeyEquals(shelf, count - 1, key0, key1, key2, key3))
        {
            return false;
        }

        using PooledTerminalVarIdentitySet identities = PooledTerminalVarIdentitySet.Rent(
            count + 1,
            checked((count + 1) * FixedScalar16TerminalIdentitySize));
        Span<byte> incoming = stackalloc byte[FixedScalar16TerminalIdentitySize];
        Span<byte> current = stackalloc byte[FixedScalar16TerminalIdentitySize];
        WriteFixedScalar16TerminalIdentity(incoming, encodedIdentityHigh, encodedIdentityLow);
        bool added = false;
        for (int i = 0; i < count; i++)
        {
            if (!Fixed32Scalar16ShelfKeyEquals(shelf, i, key0, key1, key2, key3))
                return false;

            WriteFixedScalar16TerminalIdentity(current, shelf.ReadIdentityHighAt(i), shelf.ReadIdentityLowAt(i));
            int order = current.SequenceCompareTo(incoming);
            if (!added && (profile.Descending ? order < 0 : order > 0))
            {
                identities.Add(incoming);
                added = true;
            }
            else if (order == 0)
            {
                result = CreateFixed32Scalar16TerminalInsertResult(pathTarget.Target.Offset, Scalar8VarIdentityInsertResult.AlreadyPresent, default);
                return true;
            }

            identities.Add(current);
        }

        if (!added)
            identities.Add(incoming);

        Span<byte> keyBytes = stackalloc byte[Fixed32Scalar16Layout.KeySize];
        WriteFixed32Scalar8KeyBytes(keyBytes, key0, key1, key2, key3);
        byte parentPrefix = keyBytes[pathTarget.Target.RouterDepth];
        RefineDirectShelfOwner(pathTarget.ParentRouterOffset, pathTarget.Target.Offset,
            checked((ushort)(pathTarget.Target.RouterDepth + 1)), parentPrefix, parentPrefix, parentPrefix);
        DataKernelCommitTelemetry telemetry = RewriteFixedScalar16TerminalRoute(
            pathTarget.Target.Offset,
            keyBytes,
            profile.ShelfExtentSize,
            identities,
            descending: profile.Descending);
        result = CreateFixed32Scalar16TerminalInsertResult(pathTarget.Target.Offset, Scalar8VarIdentityInsertResult.Inserted, telemetry);
        return true;
    }

    /// <summary>
    /// Inserts one canonical identity into an existing fixed scalar-16 terminal route.<br/>
    /// Tail append and shelf-local ordered insertion are attempted before the bounded full-chain fallback, preserving the shared terminal mutation economics.<br/>
    /// </summary>
    private FixedScalar16TerminalMutationResult InsertFixedScalar16TerminalIdentity(
        long rootOffset,
        byte[] rootBytes,
        ReadOnlySpan<byte> keyBytes,
        ulong encodedIdentityHigh,
        ulong encodedIdentityLow,
        bool allowDuplicateKeys)
    {
        if (!IsTerminalIdentityRootForKey(rootBytes, keyBytes, out long firstShelfOffset))
            throw new InvalidDataException("The fixed scalar-16 terminal root does not match the routed key.");
        if (!allowDuplicateKeys && firstShelfOffset != 0)
            return new FixedScalar16TerminalMutationResult(Scalar8VarIdentityInsertResult.KeyConflict, default);

        Span<byte> identity = stackalloc byte[FixedScalar16TerminalIdentitySize];
        WriteFixedScalar16TerminalIdentity(identity, encodedIdentityHigh, encodedIdentityLow);
        int shelfExtentSize = TerminalIdentityRootLayout.ReadShelfExtentSize(rootBytes);
        long tailShelfOffset = TerminalIdentityRootLayout.ReadTailShelfOffset(rootBytes);
        if (TryAppendScalar8VarIdentityTerminalTail(
            rootOffset,
            rootBytes,
            firstShelfOffset,
            tailShelfOffset,
            shelfExtentSize,
            identity,
            out DataKernelCommitTelemetry appendTelemetry))
        {
            return new FixedScalar16TerminalMutationResult(Scalar8VarIdentityInsertResult.Inserted, appendTelemetry);
        }

        if (TryInsertIntoScalar8VarIdentityTerminalChain(
            rootOffset,
            rootBytes,
            firstShelfOffset,
            shelfExtentSize,
            identity,
            out Scalar8VarIdentityRoutedInsertResult chainResult))
        {
            return new FixedScalar16TerminalMutationResult(chainResult.InsertResult, chainResult.Commit);
        }

        using PooledTerminalVarIdentitySet identities = ReadScalar8VarIdentityTerminalIdentitiesPooled(rootOffset, keyBytes, shelfExtentSize);
        bool descending = rootBytes[TerminalIdentityRootLayout.SortDirectionOffset] != 0;
        int insertIndex = LowerBoundTerminalVarIdentity(identities, identity, descending);
        if (insertIndex < identities.Count && identities.ReadAt(insertIndex).SequenceEqual(identity))
            return new FixedScalar16TerminalMutationResult(Scalar8VarIdentityInsertResult.AlreadyPresent, default);

        identities.InsertAt(insertIndex, identity);
        DataKernelCommitTelemetry telemetry = RewriteScalar8VarIdentityTerminalRoute(
            rootOffset,
            TerminalIdentityRootLayout.ShapeFixedKeyScalar16Identity,
            keyBytes,
            shelfExtentSize,
            identities);
        return new FixedScalar16TerminalMutationResult(Scalar8VarIdentityInsertResult.Inserted, telemetry);
    }

    /// <summary>
    /// Builds and publishes a fixed-key scalar-16 terminal route at an existing ordinary shelf offset.<br/>
    /// Only the root reuses the source offset; identity shelves are appended once and linked in order before one root publication.<br/>
    /// </summary>
    private DataKernelCommitTelemetry RewriteFixedScalar16TerminalRoute(
        long rootOffset,
        ReadOnlySpan<byte> keyBytes,
        int shelfExtentSize,
        PooledTerminalVarIdentitySet identities,
        bool descending = false)
    {
        long firstShelfOffset = 0;
        long previousShelfOffset = 0;
        long tailShelfOffset = 0;
        RawDataReservation previousReservation = default;
        int index = 0;
        while (index < identities.Count)
        {
            int take = GetTerminalVarIdentityChunkCount(identities, index, identities.Count - index, shelfExtentSize);
            if (take <= 0)
                throw new InvalidDataException("The fixed scalar-16 terminal route could not build a fitting identity shelf.");

            RawDataReservation shelfReservation = kernel.Reserve(shelfExtentSize);
            BuildTerminalVarIdentityShelf(shelfReservation.Span, identities, index, take, shelfExtentSize);
            if (firstShelfOffset == 0)
                firstShelfOffset = shelfReservation.Extent.Offset;
            if (previousShelfOffset != 0)
            {
                RawDataReservation previousRewrite = kernel.ReserveAt(previousShelfOffset, shelfExtentSize);
                previousReservation.Span.Slice(0, shelfExtentSize).CopyTo(previousRewrite.Span);
                TerminalVarIdentityShelfLayout.WriteNextShelfOffset(previousRewrite.Span, shelfReservation.Extent.Offset);
            }

            previousShelfOffset = shelfReservation.Extent.Offset;
            tailShelfOffset = shelfReservation.Extent.Offset;
            previousReservation = shelfReservation;
            index += take;
        }

        RawDataReservation rootRewrite = kernel.ReserveAt(rootOffset, TerminalIdentityRootLayout.Size);
        TerminalIdentityRootLayout.Initialize(
            rootRewrite.Span,
            TerminalIdentityRootLayout.ShapeFixedKeyScalar16Identity,
            keyBytes,
            shelfExtentSize,
            firstShelfOffset);
        rootRewrite.Span[TerminalIdentityRootLayout.SortDirectionOffset] = descending ? (byte)1 : (byte)0;
        TerminalIdentityRootLayout.WriteTailShelfOffset(rootRewrite.Span, tailShelfOffset);
        ClearTerminalIdentityReadCaches();
        return CommitAndInvalidateRouterReadCache();
    }

    /// <summary>
    /// Validates the persisted contract shared by fixed-key scalar-16 terminal roots.<br/>
    /// Shape, exact key width, and terminal shelf extent must match the owning fixed index before mutation or projection.<br/>
    /// </summary>
    private static void ValidateFixedScalar16TerminalRoot(ReadOnlySpan<byte> rootBytes, int expectedKeyLength, int expectedShelfExtentSize)
    {
        if (TerminalIdentityRootLayout.ReadShape(rootBytes) != TerminalIdentityRootLayout.ShapeFixedKeyScalar16Identity ||
            TerminalIdentityRootLayout.ReadKeyLength(rootBytes) != expectedKeyLength ||
            TerminalIdentityRootLayout.ReadShelfExtentSize(rootBytes) != expectedShelfExtentSize)
        {
            throw new InvalidDataException("The routed fixed-key scalar-16 terminal identity root does not match its owning index shape.");
        }
    }

    /// <summary>
    /// Writes one scalar-16 identity into the canonical big-endian representation shared with `VS16` terminal shelves.<br/>
    /// </summary>
    private static void WriteFixedScalar16TerminalIdentity(Span<byte> destination, ulong encodedIdentityHigh, ulong encodedIdentityLow)
        => WriteVarKeyScalar16TerminalIdentity(destination, encodedIdentityHigh, encodedIdentityLow);

    /// <summary>
    /// Tests whether one `FS32-16` shelf slot owns the supplied four-lane fixed key.<br/>
    /// </summary>
    private static bool Fixed32Scalar16ShelfKeyEquals(Fixed32Scalar16ReadOnly shelf, int slot, ulong key0, ulong key1, ulong key2, ulong key3)
    {
        return shelf.ReadKeyPart0At(slot) == key0 &&
            shelf.ReadKeyPart1At(slot) == key1 &&
            shelf.ReadKeyPart2At(slot) == key2 &&
            shelf.ReadKeyPart3At(slot) == key3;
    }

    /// <summary>
    /// Maps one shared terminal mutation outcome to the `SS8-16` routed result contract.<br/>
    /// </summary>
    private static Scalar8Scalar16RoutedInsertResult CreateScalar8Scalar16TerminalInsertResult(long rootOffset, Scalar8VarIdentityInsertResult insertResult, DataKernelCommitTelemetry telemetry)
    {
        Scalar8Scalar16InsertResult mapped = insertResult switch
        {
            Scalar8VarIdentityInsertResult.Inserted => Scalar8Scalar16InsertResult.Inserted,
            Scalar8VarIdentityInsertResult.AlreadyPresent => Scalar8Scalar16InsertResult.AlreadyPresent,
            Scalar8VarIdentityInsertResult.KeyConflict => Scalar8Scalar16InsertResult.KeyConflict,
            _ => throw new InvalidDataException($"Unsupported SS8-16 terminal insert result {insertResult}.")
        };
        Scalar8Scalar16RoutedInsertKind kind = mapped == Scalar8Scalar16InsertResult.Inserted
            ? Scalar8Scalar16RoutedInsertKind.WalkedNoSplit
            : mapped == Scalar8Scalar16InsertResult.KeyConflict
                ? Scalar8Scalar16RoutedInsertKind.KeyConflict
                : Scalar8Scalar16RoutedInsertKind.NoOp;
        return new Scalar8Scalar16RoutedInsertResult(kind, mapped, rootOffset, mapped == Scalar8Scalar16InsertResult.Inserted ? rootOffset : 0, 0, telemetry);
    }

    /// <summary>
    /// Maps one shared terminal mutation outcome to the `SS16-16` routed result contract.<br/>
    /// </summary>
    private static Scalar16Scalar16RoutedInsertResult CreateScalar16Scalar16TerminalInsertResult(long rootOffset, Scalar8VarIdentityInsertResult insertResult, DataKernelCommitTelemetry telemetry)
    {
        Scalar16Scalar16InsertResult mapped = insertResult switch
        {
            Scalar8VarIdentityInsertResult.Inserted => Scalar16Scalar16InsertResult.Inserted,
            Scalar8VarIdentityInsertResult.AlreadyPresent => Scalar16Scalar16InsertResult.AlreadyPresent,
            Scalar8VarIdentityInsertResult.KeyConflict => Scalar16Scalar16InsertResult.KeyConflict,
            _ => throw new InvalidDataException($"Unsupported SS16-16 terminal insert result {insertResult}.")
        };
        Scalar16Scalar16RoutedInsertKind kind = mapped == Scalar16Scalar16InsertResult.Inserted
            ? Scalar16Scalar16RoutedInsertKind.WalkedNoSplit
            : mapped == Scalar16Scalar16InsertResult.KeyConflict
                ? Scalar16Scalar16RoutedInsertKind.KeyConflict
                : Scalar16Scalar16RoutedInsertKind.NoOp;
        return new Scalar16Scalar16RoutedInsertResult(kind, mapped, rootOffset, mapped == Scalar16Scalar16InsertResult.Inserted ? rootOffset : 0, 0, telemetry);
    }

    /// <summary>
    /// Maps one shared terminal mutation outcome to the `FS32-16` routed result contract.<br/>
    /// </summary>
    private static Fixed32Scalar16RoutedInsertResult CreateFixed32Scalar16TerminalInsertResult(long rootOffset, Scalar8VarIdentityInsertResult insertResult, DataKernelCommitTelemetry telemetry)
    {
        Fixed32Scalar16InsertResult mapped = insertResult switch
        {
            Scalar8VarIdentityInsertResult.Inserted => Fixed32Scalar16InsertResult.Inserted,
            Scalar8VarIdentityInsertResult.AlreadyPresent => Fixed32Scalar16InsertResult.AlreadyPresent,
            Scalar8VarIdentityInsertResult.KeyConflict => Fixed32Scalar16InsertResult.KeyConflict,
            _ => throw new InvalidDataException($"Unsupported FS32-16 terminal insert result {insertResult}.")
        };
        Fixed32Scalar16RoutedInsertKind kind = mapped == Fixed32Scalar16InsertResult.Inserted
            ? Fixed32Scalar16RoutedInsertKind.WalkedNoSplit
            : mapped == Fixed32Scalar16InsertResult.KeyConflict
                ? Fixed32Scalar16RoutedInsertKind.KeyConflict
                : Fixed32Scalar16RoutedInsertKind.NoOp;
        return new Fixed32Scalar16RoutedInsertResult(kind, mapped, rootOffset, mapped == Fixed32Scalar16InsertResult.Inserted ? rootOffset : 0, 0, telemetry);
    }

    /// <summary>
    /// Carries one shared fixed scalar-16 terminal mutation result without leaking shape-specific routed contracts into the byte-oriented terminal engine.<br/>
    /// </summary>
    private readonly record struct FixedScalar16TerminalMutationResult(
        Scalar8VarIdentityInsertResult InsertResult,
        DataKernelCommitTelemetry Commit);
}
