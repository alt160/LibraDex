using System.Buffers.Binary;
using LibraDex.Layouts;
using LibraDex.Views;

namespace LibraDex;

internal sealed partial class LibraDexFileSession
{
    /// <summary>
    /// Deletes one encoded scalar identity from a fixed-key scalar-eight terminal route.<br/>
    /// The shared local terminal mutation rewrites only the containing identity shelf or its root/predecessor link, preserving the fixed key and every unaffected identity shelf.<br/>
    /// </summary>
    /// <param name="rootOffset">The terminal root selected by the fixed range cursor.<br/></param>
    /// <param name="keyBytes">The exact canonical fixed key stored by the terminal root.<br/></param>
    /// <param name="shelfExtentSize">The persisted terminal identity shelf extent size.<br/></param>
    /// <param name="encodedIdentity">The encoded scalar identity to delete.<br/></param>
    /// <returns>The number of deleted identities, which is zero or one for an exact identity mutation.<br/></returns>
    internal int DeleteFixedScalar8TerminalIdentity(long rootOffset, ReadOnlySpan<byte> keyBytes, int shelfExtentSize, ulong encodedIdentity)
    {
        if (!TryDeleteTerminalIdentity8Locally(
            rootOffset,
            TerminalIdentityRootLayout.ShapeFixedKeyScalar8Identity,
            keyBytes,
            shelfExtentSize,
            encodedIdentity,
            out int deleted))
        {
            throw new InvalidDataException("The fixed-key scalar-eight terminal identity delete could not use the local mutation path.");
        }

        return deleted;
    }

    /// <summary>
    /// Inserts one scalar-eight identity into an `SS16-8` terminal route or separates a neighboring key from that route.<br/>
    /// Matching keys reuse the shared identity-only shelf mutation primitives; a different key replaces the terminal page with the shortest exact-byte router chain that separates both keys.<br/>
    /// </summary>
    /// <param name="pathTarget">The walked route target and its owning parent route.<br/></param>
    /// <param name="profile">The owning `SS16-8` shelf profile.<br/></param>
    /// <param name="encodedKeyHigh">The high encoded key lane.<br/></param>
    /// <param name="encodedKeyLow">The low encoded key lane.<br/></param>
    /// <param name="encodedIdentity">The encoded scalar identity.<br/></param>
    /// <param name="allowDuplicateKeys">Whether distinct identities may share one key.<br/></param>
    /// <returns>The routed insert result for the terminal mutation or key separation.<br/></returns>
    private Scalar16Scalar8RoutedInsertResult InsertIntoScalar16Scalar8TerminalIdentityRoute(
        Scalar16Scalar8RoutePathTarget pathTarget,
        Scalar16Scalar8Profile profile,
        ulong encodedKeyHigh,
        ulong encodedKeyLow,
        ulong encodedIdentity,
        bool allowDuplicateKeys)
    {
        Span<byte> keyBytes = stackalloc byte[Scalar16Scalar8Layout.KeySize];
        BinaryPrimitives.WriteUInt64BigEndian(keyBytes, encodedKeyHigh);
        BinaryPrimitives.WriteUInt64BigEndian(keyBytes.Slice(sizeof(ulong)), encodedKeyLow);
        byte[] rootBytes = ReadTerminalIdentityRootBytes(pathTarget.Target.Offset);
        ValidateFixedScalar8TerminalRoot(rootBytes, keyBytes.Length, profile.ShelfExtentSize);
        ReadOnlySpan<byte> rootKey = rootBytes.AsSpan(TerminalIdentityRootLayout.KeyBytesOffset, keyBytes.Length);
        if (!rootKey.SequenceEqual(keyBytes))
        {
            byte[] newShelfBytes = new byte[profile.ShelfExtentSize];
            Scalar16Scalar8 newShelf = new(newShelfBytes, profile);
            newShelf.Initialize();
            Scalar16Scalar8InsertResult newShelfInsert = newShelf.Insert(encodedKeyHigh, encodedKeyLow, encodedIdentity, allowDuplicateKeys);
            if (newShelfInsert != Scalar16Scalar8InsertResult.Inserted)
                throw new InvalidDataException($"Expected SS16-8 terminal-neighbor shelf insert, got {newShelfInsert}.");

            DataKernelCommitTelemetry splitTelemetry = SplitMismatchedFixedScalar8TerminalIdentityRoute(
                pathTarget.Target.Offset,
                pathTarget.Target.RouterDepth,
                pathTarget.Target.AllocationClassId,
                rootBytes,
                keyBytes,
                newShelfBytes);
            return new Scalar16Scalar8RoutedInsertResult(
                Scalar16Scalar8RoutedInsertKind.WalkedShelfTransformSplit,
                Scalar16Scalar8InsertResult.Inserted,
                pathTarget.Target.Offset,
                pathTarget.Target.Offset,
                0,
                splitTelemetry);
        }

        if (TryAppendTerminalIdentity8Tail(pathTarget.Target.Offset, keyBytes, profile.ShelfExtentSize, encodedIdentity, allowDuplicateKeys, out DataKernelCommitTelemetry appendTelemetry))
            return CreateScalar16Scalar8TerminalInsertResult(pathTarget.Target.Offset, Scalar8Scalar8InsertResult.Inserted, appendTelemetry);

        if (TryInsertTerminalIdentity8Locally(
            pathTarget.Target.Offset,
            TerminalIdentityRootLayout.ShapeFixedKeyScalar8Identity,
            keyBytes,
            profile.ShelfExtentSize,
            encodedIdentity,
            allowDuplicateKeys,
            out Scalar8Scalar8InsertResult localResult,
            out DataKernelCommitTelemetry localTelemetry))
        {
            return CreateScalar16Scalar8TerminalInsertResult(pathTarget.Target.Offset, localResult, localTelemetry);
        }

        throw new InvalidDataException("The SS16-8 terminal identity route could not accept the encoded identity.");
    }

    /// <summary>
    /// Converts one full same-key `SS16-8` shelf plus its incoming identity into the shared fixed-key scalar-eight terminal representation.<br/>
    /// The terminal root reuses the routed shelf offset, stores the sixteen-byte key once, and moves only sorted identities into compact identity shelves.<br/>
    /// </summary>
    /// <param name="pathTarget">The full shelf route being converted.<br/></param>
    /// <param name="existingShelfBytes">The current or batch-local full shelf image.<br/></param>
    /// <param name="profile">The owning `SS16-8` profile.<br/></param>
    /// <param name="encodedKeyHigh">The high encoded key lane.<br/></param>
    /// <param name="encodedKeyLow">The low encoded key lane.<br/></param>
    /// <param name="encodedIdentity">The incoming encoded identity.<br/></param>
    /// <param name="allowDuplicateKeys">Whether distinct identities may share one key.<br/></param>
    /// <param name="result">The completed routed insert result when conversion applies.<br/></param>
    /// <returns><see langword="true"/> when the shelf is one same-key run and was converted; otherwise <see langword="false"/>.<br/></returns>
    private bool TryConvertScalar16Scalar8SameKeyShelfToTerminal(
        Scalar16Scalar8RoutePathTarget pathTarget,
        ReadOnlySpan<byte> existingShelfBytes,
        Scalar16Scalar8Profile profile,
        ulong encodedKeyHigh,
        ulong encodedKeyLow,
        ulong encodedIdentity,
        bool allowDuplicateKeys,
        out Scalar16Scalar8RoutedInsertResult result)
    {
        result = default;
        Scalar16Scalar8ReadOnly shelf = new(existingShelfBytes, profile);
        int count = shelf.ItemCount;
        if (!allowDuplicateKeys || count == 0 ||
            shelf.ReadKeyHighAt(0) != encodedKeyHigh || shelf.ReadKeyLowAt(0) != encodedKeyLow ||
            shelf.ReadKeyHighAt(count - 1) != encodedKeyHigh || shelf.ReadKeyLowAt(count - 1) != encodedKeyLow)
        {
            return false;
        }

        List<ulong> identities = new(count + 1);
        for (int i = 0; i < count; i++)
            identities.Add(shelf.ReadIdentityAt(i));
        if (profile.Descending)
            identities.Reverse();
        int insertIndex = identities.BinarySearch(encodedIdentity);
        if (insertIndex >= 0)
        {
            result = CreateScalar16Scalar8TerminalInsertResult(pathTarget.Target.Offset, Scalar8Scalar8InsertResult.AlreadyPresent, default);
            return true;
        }

        identities.Insert(~insertIndex, encodedIdentity);
        if (profile.Descending)
            identities.Reverse();
        Span<byte> keyBytes = stackalloc byte[Scalar16Scalar8Layout.KeySize];
        BinaryPrimitives.WriteUInt64BigEndian(keyBytes, encodedKeyHigh);
        BinaryPrimitives.WriteUInt64BigEndian(keyBytes.Slice(sizeof(ulong)), encodedKeyLow);
        byte parentPrefix = keyBytes[pathTarget.Target.RouterDepth];
        RefineDirectShelfOwner(pathTarget.ParentRouterOffset, pathTarget.Target.Offset,
            checked((ushort)(pathTarget.Target.RouterDepth + 1)), parentPrefix, parentPrefix, parentPrefix);
        DataKernelCommitTelemetry telemetry = RewriteTerminalIdentity8Route(
            pathTarget.Target.Offset,
            TerminalIdentityRootLayout.ShapeFixedKeyScalar8Identity,
            keyBytes,
            profile.ShelfExtentSize,
            identities,
            descending: profile.Descending);
        result = CreateScalar16Scalar8TerminalInsertResult(pathTarget.Target.Offset, Scalar8Scalar8InsertResult.Inserted, telemetry);
        return true;
    }

    /// <summary>
    /// Inserts one scalar-eight identity into an `FS32-8` terminal route or separates a neighboring key from that route.<br/>
    /// Matching keys reuse the shared identity-only shelf mutation primitives; a different key replaces the terminal page with the shortest exact-byte router chain that separates both keys.<br/>
    /// </summary>
    /// <param name="pathTarget">The walked route target and its owning parent route.<br/></param>
    /// <param name="profile">The owning `FS32-8` shelf profile.<br/></param>
    /// <param name="key0">The first encoded key lane.<br/></param>
    /// <param name="key1">The second encoded key lane.<br/></param>
    /// <param name="key2">The third encoded key lane.<br/></param>
    /// <param name="key3">The fourth encoded key lane.<br/></param>
    /// <param name="encodedIdentity">The encoded scalar identity.<br/></param>
    /// <param name="allowDuplicateKeys">Whether distinct identities may share one key.<br/></param>
    /// <returns>The routed insert result for the terminal mutation or key separation.<br/></returns>
    private Fixed32Scalar8RoutedInsertResult InsertIntoFixed32Scalar8TerminalIdentityRoute(
        Fixed32Scalar8RoutePathTarget pathTarget,
        Fixed32Scalar8Profile profile,
        ulong key0,
        ulong key1,
        ulong key2,
        ulong key3,
        ulong encodedIdentity,
        bool allowDuplicateKeys)
    {
        Span<byte> keyBytes = stackalloc byte[Fixed32Scalar8Layout.KeySize];
        WriteFixed32Scalar8KeyBytes(keyBytes, key0, key1, key2, key3);
        byte[] rootBytes = ReadTerminalIdentityRootBytes(pathTarget.Target.Offset);
        ValidateFixedScalar8TerminalRoot(rootBytes, keyBytes.Length, profile.ShelfExtentSize);
        ReadOnlySpan<byte> rootKey = rootBytes.AsSpan(TerminalIdentityRootLayout.KeyBytesOffset, keyBytes.Length);
        if (!rootKey.SequenceEqual(keyBytes))
        {
            byte[] newShelfBytes = new byte[profile.ShelfExtentSize];
            Fixed32Scalar8 newShelf = new(newShelfBytes, profile);
            newShelf.Initialize();
            Fixed32Scalar8InsertResult newShelfInsert = newShelf.Insert(key0, key1, key2, key3, encodedIdentity, allowDuplicateKeys);
            if (newShelfInsert != Fixed32Scalar8InsertResult.Inserted)
                throw new InvalidDataException($"Expected FS32-8 terminal-neighbor shelf insert, got {newShelfInsert}.");

            DataKernelCommitTelemetry splitTelemetry = SplitMismatchedFixedScalar8TerminalIdentityRoute(
                pathTarget.Target.Offset,
                pathTarget.Target.RouterDepth,
                pathTarget.Target.AllocationClassId,
                rootBytes,
                keyBytes,
                newShelfBytes);
            return new Fixed32Scalar8RoutedInsertResult(
                Fixed32Scalar8RoutedInsertKind.WalkedShelfTransformSplit,
                Fixed32Scalar8InsertResult.Inserted,
                pathTarget.Target.Offset,
                pathTarget.Target.Offset,
                0,
                splitTelemetry);
        }

        if (TryAppendTerminalIdentity8Tail(pathTarget.Target.Offset, keyBytes, profile.ShelfExtentSize, encodedIdentity, allowDuplicateKeys, out DataKernelCommitTelemetry appendTelemetry))
            return CreateFixed32Scalar8TerminalInsertResult(pathTarget.Target.Offset, Scalar8Scalar8InsertResult.Inserted, appendTelemetry);

        if (TryInsertTerminalIdentity8Locally(
            pathTarget.Target.Offset,
            TerminalIdentityRootLayout.ShapeFixedKeyScalar8Identity,
            keyBytes,
            profile.ShelfExtentSize,
            encodedIdentity,
            allowDuplicateKeys,
            out Scalar8Scalar8InsertResult localResult,
            out DataKernelCommitTelemetry localTelemetry))
        {
            return CreateFixed32Scalar8TerminalInsertResult(pathTarget.Target.Offset, localResult, localTelemetry);
        }

        throw new InvalidDataException("The FS32-8 terminal identity route could not accept the encoded identity.");
    }

    /// <summary>
    /// Converts one full same-key `FS32-8` shelf plus its incoming identity into the shared fixed-key scalar-eight terminal representation.<br/>
    /// The terminal root reuses the routed shelf offset, stores the thirty-two-byte key once, and moves only sorted identities into compact identity shelves.<br/>
    /// </summary>
    /// <param name="pathTarget">The full shelf route being converted.<br/></param>
    /// <param name="existingShelfBytes">The current or batch-local full shelf image.<br/></param>
    /// <param name="profile">The owning `FS32-8` profile.<br/></param>
    /// <param name="key0">The first encoded key lane.<br/></param>
    /// <param name="key1">The second encoded key lane.<br/></param>
    /// <param name="key2">The third encoded key lane.<br/></param>
    /// <param name="key3">The fourth encoded key lane.<br/></param>
    /// <param name="encodedIdentity">The incoming encoded identity.<br/></param>
    /// <param name="allowDuplicateKeys">Whether distinct identities may share one key.<br/></param>
    /// <param name="result">The completed routed insert result when conversion applies.<br/></param>
    /// <returns><see langword="true"/> when the shelf is one same-key run and was converted; otherwise <see langword="false"/>.<br/></returns>
    private bool TryConvertFixed32Scalar8SameKeyShelfToTerminal(
        Fixed32Scalar8RoutePathTarget pathTarget,
        ReadOnlySpan<byte> existingShelfBytes,
        Fixed32Scalar8Profile profile,
        ulong key0,
        ulong key1,
        ulong key2,
        ulong key3,
        ulong encodedIdentity,
        bool allowDuplicateKeys,
        out Fixed32Scalar8RoutedInsertResult result)
    {
        result = default;
        Fixed32Scalar8ReadOnly shelf = new(existingShelfBytes, profile);
        int count = shelf.ItemCount;
        if (!allowDuplicateKeys || count == 0 ||
            !Fixed32Scalar8ShelfKeyEquals(shelf, 0, key0, key1, key2, key3) ||
            !Fixed32Scalar8ShelfKeyEquals(shelf, count - 1, key0, key1, key2, key3))
        {
            return false;
        }

        List<ulong> identities = new(count + 1);
        for (int i = 0; i < count; i++)
            identities.Add(shelf.ReadIdentityAt(i));
        if (profile.Descending)
            identities.Reverse();
        int insertIndex = identities.BinarySearch(encodedIdentity);
        if (insertIndex >= 0)
        {
            result = CreateFixed32Scalar8TerminalInsertResult(pathTarget.Target.Offset, Scalar8Scalar8InsertResult.AlreadyPresent, default);
            return true;
        }

        identities.Insert(~insertIndex, encodedIdentity);
        if (profile.Descending)
            identities.Reverse();
        Span<byte> keyBytes = stackalloc byte[Fixed32Scalar8Layout.KeySize];
        WriteFixed32Scalar8KeyBytes(keyBytes, key0, key1, key2, key3);
        byte parentPrefix = keyBytes[pathTarget.Target.RouterDepth];
        RefineDirectShelfOwner(pathTarget.ParentRouterOffset, pathTarget.Target.Offset,
            checked((ushort)(pathTarget.Target.RouterDepth + 1)), parentPrefix, parentPrefix, parentPrefix);
        DataKernelCommitTelemetry telemetry = RewriteTerminalIdentity8Route(
            pathTarget.Target.Offset,
            TerminalIdentityRootLayout.ShapeFixedKeyScalar8Identity,
            keyBytes,
            profile.ShelfExtentSize,
            identities,
            descending: profile.Descending);
        result = CreateFixed32Scalar8TerminalInsertResult(pathTarget.Target.Offset, Scalar8Scalar8InsertResult.Inserted, telemetry);
        return true;
    }

    /// <summary>
    /// Replaces one mismatched fixed-key terminal page with the shortest one-byte router chain that separates its stored key from an incoming key.<br/>
    /// The old terminal root is copied once and continues to own its existing identity chain; the incoming tuple is stored in one ordinary shape-local shelf.<br/>
    /// </summary>
    /// <param name="terminalRootOffset">The terminal root offset that becomes the first router page.<br/></param>
    /// <param name="parentRouterDepth">The key depth consumed by the parent router.<br/></param>
    /// <param name="allocationClassId">The router allocation class to preserve.<br/></param>
    /// <param name="terminalRootBytes">The validated current terminal root bytes.<br/></param>
    /// <param name="incomingKeyBytes">The canonical big-endian incoming key bytes.<br/></param>
    /// <param name="incomingShelfBytes">One initialized ordinary shelf containing the incoming tuple.<br/></param>
    /// <returns>The commit telemetry for the terminal-to-router transformation.<br/></returns>
    private DataKernelCommitTelemetry SplitMismatchedFixedScalar8TerminalIdentityRoute(
        long terminalRootOffset,
        ushort parentRouterDepth,
        ushort allocationClassId,
        ReadOnlySpan<byte> terminalRootBytes,
        ReadOnlySpan<byte> incomingKeyBytes,
        ReadOnlySpan<byte> incomingShelfBytes)
    {
        int keyLength = TerminalIdentityRootLayout.ReadKeyLength(terminalRootBytes);
        ReadOnlySpan<byte> terminalKeyBytes = terminalRootBytes.Slice(TerminalIdentityRootLayout.KeyBytesOffset, keyLength);
        int firstDifferentDepth = 0;
        while (firstDifferentDepth < keyLength && terminalKeyBytes[firstDifferentDepth] == incomingKeyBytes[firstDifferentDepth])
            firstDifferentDepth++;
        if (firstDifferentDepth >= keyLength)
            throw new InvalidDataException("A fixed-key terminal mismatch split was requested for equal keys.");
        if (firstDifferentDepth <= parentRouterDepth)
            throw new InvalidDataException($"The fixed-key terminal route aliases keys that already diverged at consumed depth {firstDifferentDepth}; parent depth is {parentRouterDepth}.");

        RawDataReservation oldRootAppend = kernel.Reserve(TerminalIdentityRootLayout.Size);
        terminalRootBytes.Slice(0, TerminalIdentityRootLayout.Size).CopyTo(oldRootAppend.Span);
        RawDataReservation incomingShelfAppend = kernel.Reserve(incomingShelfBytes.Length);
        incomingShelfBytes.CopyTo(incomingShelfAppend.Span);

        long childRouterOffset = 0;
        for (int depth = firstDifferentDepth; depth > parentRouterDepth; depth--)
        {
            long[] targets = new long[RouterLayout.MaxOneByteRouteCount];
            if (depth == firstDifferentDepth)
            {
                targets[terminalKeyBytes[depth]] = oldRootAppend.Extent.Offset;
                targets[incomingKeyBytes[depth]] = incomingShelfAppend.Extent.Offset;
            }
            else
            {
                targets[terminalKeyBytes[depth]] = childRouterOffset;
            }

            RawDataReservation routerReservation = depth == parentRouterDepth + 1
                ? kernel.ReserveAt(terminalRootOffset, RouterLayout.Size)
                : kernel.Reserve(RouterLayout.Size);
            RouterWriter writer = new(routerReservation.Span);
            writer.InitializeExpandedOneByte(checked((ushort)depth), allocationClassId, targets);
            childRouterOffset = routerReservation.Extent.Offset;
        }

        InvalidateRouterReadCacheForRouterRewrite(terminalRootOffset);
        return CommitAndDeferRouterReadCacheInvalidation();
    }

    /// <summary>
    /// Validates the persisted contract shared by fixed-key scalar-eight terminal roots.<br/>
    /// Shape, exact key width, and terminal shelf extent must all match the owning fixed index before any mutation or read projection proceeds.<br/>
    /// </summary>
    /// <param name="rootBytes">The persisted terminal root bytes.<br/></param>
    /// <param name="expectedKeyLength">The owning fixed key width.<br/></param>
    /// <param name="expectedShelfExtentSize">The owning terminal shelf extent size.<br/></param>
    private static void ValidateFixedScalar8TerminalRoot(ReadOnlySpan<byte> rootBytes, int expectedKeyLength, int expectedShelfExtentSize)
    {
        if (TerminalIdentityRootLayout.ReadShape(rootBytes) != TerminalIdentityRootLayout.ShapeFixedKeyScalar8Identity ||
            TerminalIdentityRootLayout.ReadKeyLength(rootBytes) != expectedKeyLength ||
            TerminalIdentityRootLayout.ReadShelfExtentSize(rootBytes) != expectedShelfExtentSize)
        {
            throw new InvalidDataException("The routed fixed-key scalar-eight terminal identity root does not match its owning index shape.");
        }
    }

    /// <summary>
    /// Writes one canonical thirty-two-byte fixed key from its four already encoded big-endian lanes.<br/>
    /// The resulting byte sequence is suitable for terminal-root persistence and direct byte-depth routing.<br/>
    /// </summary>
    /// <param name="target">The thirty-two-byte destination span.<br/></param>
    /// <param name="key0">The first encoded key lane.<br/></param>
    /// <param name="key1">The second encoded key lane.<br/></param>
    /// <param name="key2">The third encoded key lane.<br/></param>
    /// <param name="key3">The fourth encoded key lane.<br/></param>
    internal static void WriteFixed32Scalar8KeyBytes(Span<byte> target, ulong key0, ulong key1, ulong key2, ulong key3)
    {
        BinaryPrimitives.WriteUInt64BigEndian(target, key0);
        BinaryPrimitives.WriteUInt64BigEndian(target.Slice(8), key1);
        BinaryPrimitives.WriteUInt64BigEndian(target.Slice(16), key2);
        BinaryPrimitives.WriteUInt64BigEndian(target.Slice(24), key3);
    }

    /// <summary>
    /// Compares one `FS32-8` shelf key slot with four encoded key lanes without allocating a temporary key buffer.<br/>
    /// </summary>
    /// <param name="shelf">The validated fixed shelf view.<br/></param>
    /// <param name="slot">The item slot to compare.<br/></param>
    /// <param name="key0">The first encoded key lane.<br/></param>
    /// <param name="key1">The second encoded key lane.<br/></param>
    /// <param name="key2">The third encoded key lane.<br/></param>
    /// <param name="key3">The fourth encoded key lane.<br/></param>
    /// <returns><see langword="true"/> when all four persisted lanes match.<br/></returns>
    private static bool Fixed32Scalar8ShelfKeyEquals(Fixed32Scalar8ReadOnly shelf, int slot, ulong key0, ulong key1, ulong key2, ulong key3)
    {
        return shelf.ReadKeyPart0At(slot) == key0 &&
            shelf.ReadKeyPart1At(slot) == key1 &&
            shelf.ReadKeyPart2At(slot) == key2 &&
            shelf.ReadKeyPart3At(slot) == key3;
    }

    /// <summary>
    /// Maps the shared scalar-eight terminal mutation result to the `SS16-8` routed result contract.<br/>
    /// </summary>
    /// <param name="rootOffset">The terminal root offset.<br/></param>
    /// <param name="insertResult">The shared terminal mutation result.<br/></param>
    /// <param name="telemetry">Commit telemetry when storage changed.<br/></param>
    /// <returns>The shape-local routed result.<br/></returns>
    private static Scalar16Scalar8RoutedInsertResult CreateScalar16Scalar8TerminalInsertResult(long rootOffset, Scalar8Scalar8InsertResult insertResult, DataKernelCommitTelemetry telemetry)
    {
        Scalar16Scalar8InsertResult mappedResult = insertResult switch
        {
            Scalar8Scalar8InsertResult.Inserted => Scalar16Scalar8InsertResult.Inserted,
            Scalar8Scalar8InsertResult.AlreadyPresent => Scalar16Scalar8InsertResult.AlreadyPresent,
            Scalar8Scalar8InsertResult.KeyConflict => Scalar16Scalar8InsertResult.KeyConflict,
            _ => throw new InvalidDataException($"Unsupported SS16-8 terminal insert result {insertResult}.")
        };
        Scalar16Scalar8RoutedInsertKind kind = mappedResult == Scalar16Scalar8InsertResult.Inserted
            ? Scalar16Scalar8RoutedInsertKind.WalkedNoSplit
            : mappedResult == Scalar16Scalar8InsertResult.KeyConflict
                ? Scalar16Scalar8RoutedInsertKind.KeyConflict
                : Scalar16Scalar8RoutedInsertKind.NoOp;
        return new Scalar16Scalar8RoutedInsertResult(kind, mappedResult, rootOffset, mappedResult == Scalar16Scalar8InsertResult.Inserted ? rootOffset : 0, 0, telemetry);
    }

    /// <summary>
    /// Maps the shared scalar-eight terminal mutation result to the `FS32-8` routed result contract.<br/>
    /// </summary>
    /// <param name="rootOffset">The terminal root offset.<br/></param>
    /// <param name="insertResult">The shared terminal mutation result.<br/></param>
    /// <param name="telemetry">Commit telemetry when storage changed.<br/></param>
    /// <returns>The shape-local routed result.<br/></returns>
    private static Fixed32Scalar8RoutedInsertResult CreateFixed32Scalar8TerminalInsertResult(long rootOffset, Scalar8Scalar8InsertResult insertResult, DataKernelCommitTelemetry telemetry)
    {
        Fixed32Scalar8InsertResult mappedResult = insertResult switch
        {
            Scalar8Scalar8InsertResult.Inserted => Fixed32Scalar8InsertResult.Inserted,
            Scalar8Scalar8InsertResult.AlreadyPresent => Fixed32Scalar8InsertResult.AlreadyPresent,
            Scalar8Scalar8InsertResult.KeyConflict => Fixed32Scalar8InsertResult.KeyConflict,
            _ => throw new InvalidDataException($"Unsupported FS32-8 terminal insert result {insertResult}.")
        };
        Fixed32Scalar8RoutedInsertKind kind = mappedResult == Fixed32Scalar8InsertResult.Inserted
            ? Fixed32Scalar8RoutedInsertKind.WalkedNoSplit
            : mappedResult == Fixed32Scalar8InsertResult.KeyConflict
                ? Fixed32Scalar8RoutedInsertKind.KeyConflict
                : Fixed32Scalar8RoutedInsertKind.NoOp;
        return new Fixed32Scalar8RoutedInsertResult(kind, mappedResult, rootOffset, mappedResult == Fixed32Scalar8InsertResult.Inserted ? rootOffset : 0, 0, telemetry);
    }
}
