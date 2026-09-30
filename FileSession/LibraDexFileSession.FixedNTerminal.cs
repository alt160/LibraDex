using System.Buffers.Binary;
using LibraDex.Layouts;
using LibraDex.Views;

namespace LibraDex;

internal sealed partial class LibraDexFileSession
{
    /// <summary>
    /// Classifies one fixed-N target by its persisted four-byte magic without allocating or reading a complete 4 KiB router page.<br/>
    /// Full router validation remains the responsibility of the caller that consumes the page after this inexpensive branch decision.<br/>
    /// </summary>
    /// <param name="offset">The positive routed target offset to classify.<br/></param>
    /// <returns><see langword="true"/> when the target begins with router magic; otherwise <see langword="false"/>.<br/></returns>
    private bool IsFixedNRouterPage(long offset)
    {
        Span<byte> magicBytes = stackalloc byte[sizeof(uint)];
        kernel.Read(offset, magicBytes);
        return BinaryPrimitives.ReadUInt32LittleEndian(magicBytes) == RouterLayout.Magic;
    }

    /// <summary>
    /// Tests one routed fixed-N target for the expected terminal-root contract.<br/>
    /// A non-terminal target returns false without reading the complete root page; a terminal root with the wrong shape, key, or shelf extent is rejected as corrupt topology.<br/>
    /// </summary>
    /// <param name="targetOffset">The routed target offset to classify.<br/></param>
    /// <param name="expectedShape">The fixed-N terminal shape byte required by the caller.<br/></param>
    /// <param name="expectedKey">The complete canonical fixed-width key.<br/></param>
    /// <param name="expectedShelfExtentSize">The identity-only shelf extent required by the owning index.<br/></param>
    /// <param name="rootBytes">Receives the validated terminal-root page when the target is terminal.<br/></param>
    /// <returns><see langword="true"/> when the target is a matching terminal identity root; otherwise <see langword="false"/>.<br/></returns>
    private bool TryReadFixedNTerminalRoot(
        long targetOffset,
        byte expectedShape,
        ReadOnlySpan<byte> expectedKey,
        int expectedShelfExtentSize,
        out byte[] rootBytes)
    {
        Span<byte> magicBytes = stackalloc byte[sizeof(uint)];
        kernel.Read(targetOffset, magicBytes);
        if (BinaryPrimitives.ReadUInt32LittleEndian(magicBytes) != TerminalIdentityRootLayout.Magic)
        {
            rootBytes = [];
            return false;
        }

        rootBytes = ReadTerminalIdentityRootBytes(targetOffset);
        if (TerminalIdentityRootLayout.ReadShape(rootBytes) != expectedShape ||
            TerminalIdentityRootLayout.ReadKeyLength(rootBytes) != expectedKey.Length ||
            TerminalIdentityRootLayout.ReadShelfExtentSize(rootBytes) != expectedShelfExtentSize ||
            !rootBytes.AsSpan(TerminalIdentityRootLayout.KeyBytesOffset, expectedKey.Length).SequenceEqual(expectedKey))
        {
            throw new InvalidDataException("The routed fixed-N terminal root does not match its owning index shape, key, or shelf extent.");
        }

        return true;
    }

    /// <summary>
    /// Tests one routed fixed-N target for a shape-qualified terminal root when the caller has not materialized the terminal key yet.<br/>
    /// The stored key width and shelf extent are validated before the root page is returned for count, range, or tuple projection.<br/>
    /// </summary>
    /// <param name="targetOffset">The routed target offset to classify.<br/></param>
    /// <param name="expectedShape">The fixed-N terminal shape byte required by the caller.<br/></param>
    /// <param name="expectedKeyLength">The fixed encoded key width required by the owning index.<br/></param>
    /// <param name="expectedShelfExtentSize">The identity-only shelf extent required by the owning index.<br/></param>
    /// <param name="rootBytes">Receives the validated terminal-root page when the target is terminal.<br/></param>
    /// <returns><see langword="true"/> when the target is a matching terminal identity root; otherwise <see langword="false"/>.<br/></returns>
    private bool TryReadFixedNTerminalRoot(
        long targetOffset,
        byte expectedShape,
        int expectedKeyLength,
        int expectedShelfExtentSize,
        out byte[] rootBytes)
    {
        Span<byte> magicBytes = stackalloc byte[sizeof(uint)];
        kernel.Read(targetOffset, magicBytes);
        if (BinaryPrimitives.ReadUInt32LittleEndian(magicBytes) != TerminalIdentityRootLayout.Magic)
        {
            rootBytes = [];
            return false;
        }

        rootBytes = ReadTerminalIdentityRootBytes(targetOffset);
        if (TerminalIdentityRootLayout.ReadShape(rootBytes) != expectedShape ||
            TerminalIdentityRootLayout.ReadKeyLength(rootBytes) != expectedKeyLength ||
            TerminalIdentityRootLayout.ReadShelfExtentSize(rootBytes) != expectedShelfExtentSize)
        {
            throw new InvalidDataException("The routed fixed-N terminal root does not match its owning index shape, key width, or shelf extent.");
        }

        return true;
    }

    /// <summary>
    /// Counts one shape-qualified fixed-N terminal root without reading ordinary shelf bytes.<br/>
    /// Scalar-eight terminals use fixed identity-shelf headers; scalar-sixteen and variable-identity terminals use the shared variable-identity chain counter.<br/>
    /// </summary>
    /// <param name="rootOffset">The validated terminal root offset.<br/></param>
    /// <param name="shape">The validated fixed-N terminal shape.<br/></param>
    /// <returns>The exact number of identities linked from the terminal root.<br/></returns>
    private long CountFixedNTerminalRoot(long rootOffset, byte shape)
    {
        return shape == TerminalIdentityRootLayout.ShapeFixedKeyScalar8Identity
            ? CountTerminalIdentity8RootNarrow(rootOffset)
            : CountTerminalVarIdentityRootNarrow(rootOffset, shape);
    }

    /// <summary>
    /// Tests whether the key stored once in a fixed-N terminal root is inside an inclusive encoded key range.<br/>
    /// </summary>
    /// <param name="rootBytes">The validated terminal root bytes.<br/></param>
    /// <param name="lowerKey">The inclusive lower encoded key.<br/></param>
    /// <param name="upperKey">The inclusive upper encoded key.<br/></param>
    /// <returns><see langword="true"/> when the terminal key is inside the requested range.<br/></returns>
    private static bool IsFixedNTerminalKeyInRange(ReadOnlySpan<byte> rootBytes, ReadOnlySpan<byte> lowerKey, ReadOnlySpan<byte> upperKey)
    {
        int keyLength = TerminalIdentityRootLayout.ReadKeyLength(rootBytes);
        ReadOnlySpan<byte> key = rootBytes.Slice(TerminalIdentityRootLayout.KeyBytesOffset, keyLength);
        return key.SequenceCompareTo(lowerKey) >= 0 && key.SequenceCompareTo(upperKey) <= 0;
    }

    /// <summary>
    /// Appends all scalar-eight identities from one validated fixed-N terminal root to a caller-owned result list.<br/>
    /// </summary>
    /// <param name="rootOffset">The terminal root offset.<br/></param>
    /// <param name="rootBytes">The validated terminal root bytes.<br/></param>
    /// <param name="shelfExtentSize">The terminal shelf extent.<br/></param>
    /// <param name="identities">The destination identity list.<br/></param>
    private void AppendFixedNScalar8TerminalIdentities(long rootOffset, ReadOnlySpan<byte> rootBytes, int shelfExtentSize, List<ulong> identities)
    {
        int keyLength = TerminalIdentityRootLayout.ReadKeyLength(rootBytes);
        ReadOnlySpan<byte> key = rootBytes.Slice(TerminalIdentityRootLayout.KeyBytesOffset, keyLength);
        identities.AddRange(ReadTerminalIdentity8RouteIdentities(rootOffset, key, shelfExtentSize));
    }

    /// <summary>
    /// Appends all byte-native identities from one validated fixed-N terminal root to a caller-owned result list.<br/>
    /// </summary>
    /// <param name="rootOffset">The terminal root offset.<br/></param>
    /// <param name="rootBytes">The validated terminal root bytes.<br/></param>
    /// <param name="shelfExtentSize">The terminal shelf extent.<br/></param>
    /// <param name="identities">The destination identity list.<br/></param>
    private void AppendFixedNVarTerminalIdentities(long rootOffset, ReadOnlySpan<byte> rootBytes, int shelfExtentSize, List<byte[]> identities)
    {
        int keyLength = TerminalIdentityRootLayout.ReadKeyLength(rootBytes);
        ReadOnlySpan<byte> key = rootBytes.Slice(TerminalIdentityRootLayout.KeyBytesOffset, keyLength);
        identities.AddRange(ReadScalar8VarIdentityTerminalIdentities(rootOffset, key, shelfExtentSize));
    }

    /// <summary>
    /// Converts one full same-key `FSN-8` shelf into a fixed-key terminal root plus compact scalar-eight identity shelves.<br/>
    /// The source shelf offset becomes the authoritative terminal root, the key is stored once, and the already-sorted identity sequence is preserved without another comparison pass.<br/>
    /// </summary>
    /// <param name="rootOffset">The full ordinary shelf offset to convert.<br/></param>
    /// <param name="key">The single exhausted fixed key shared by every tuple.<br/></param>
    /// <param name="shelfExtentSize">The fixed terminal identity shelf extent.<br/></param>
    /// <param name="identities">The complete sorted scalar-eight identity sequence including the incoming identity.<br/></param>
    /// <returns>The successful insert result and publication telemetry.<br/></returns>
    private (FixedNScalarInsertResult Result, DataKernelCommitTelemetry Commit) RewriteFixedNScalar8TerminalRoute(
        long rootOffset,
        ReadOnlySpan<byte> key,
        int shelfExtentSize,
        IReadOnlyList<ulong> identities,
        bool descending = false)
    {
        DataKernelCommitTelemetry telemetry = RewriteTerminalIdentity8Route(
            rootOffset,
            TerminalIdentityRootLayout.ShapeFixedKeyScalar8Identity,
            key,
            shelfExtentSize,
            identities,
            descending: descending);
        return (FixedNScalarInsertResult.Inserted, telemetry);
    }

    /// <summary>
    /// Inserts one scalar-eight identity into an existing `FSN-8` exhausted-key terminal chain.<br/>
    /// Sorted tail append and shelf-local insertion are attempted before the bounded full-chain rewrite fallback.<br/>
    /// </summary>
    /// <param name="rootOffset">The matching fixed-N terminal root offset.<br/></param>
    /// <param name="key">The complete canonical fixed key stored in the root.<br/></param>
    /// <param name="shelfExtentSize">The terminal identity shelf extent.<br/></param>
    /// <param name="encodedIdentity">The encoded scalar-eight identity to insert.<br/></param>
    /// <param name="allowDuplicateKeys">Whether the fixed key may own more than one identity.<br/></param>
    /// <returns>The fixed-N insert result and publication telemetry.<br/></returns>
    private (FixedNScalarInsertResult Result, DataKernelCommitTelemetry Commit) InsertFixedNScalar8TerminalIdentity(
        long rootOffset,
        ReadOnlySpan<byte> key,
        int shelfExtentSize,
        ulong encodedIdentity,
        bool allowDuplicateKeys)
    {
        if (TryAppendTerminalIdentity8Tail(rootOffset, key, shelfExtentSize, encodedIdentity, allowDuplicateKeys, out DataKernelCommitTelemetry appendTelemetry))
            return (FixedNScalarInsertResult.Inserted, appendTelemetry);

        if (TryInsertTerminalIdentity8Locally(
            rootOffset,
            TerminalIdentityRootLayout.ShapeFixedKeyScalar8Identity,
            key,
            shelfExtentSize,
            encodedIdentity,
            allowDuplicateKeys,
            out Scalar8Scalar8InsertResult localResult,
            out DataKernelCommitTelemetry localTelemetry))
        {
            return (MapFixedNScalarInsertResult(localResult), localTelemetry);
        }

        List<ulong> identities = ReadTerminalIdentity8RouteIdentities(rootOffset, key, shelfExtentSize);
        bool descending = ReadTerminalIdentityRootBytes(rootOffset)[TerminalIdentityRootLayout.SortDirectionOffset] != 0;
        if (descending)
            identities.Reverse();
        int insertIndex = identities.BinarySearch(encodedIdentity);
        if (insertIndex >= 0)
            return (FixedNScalarInsertResult.AlreadyPresent, default);
        if (!allowDuplicateKeys && identities.Count != 0)
            return (FixedNScalarInsertResult.KeyConflict, default);

        identities.Insert(~insertIndex, encodedIdentity);
        if (descending)
            identities.Reverse();
        return RewriteFixedNScalar8TerminalRoute(rootOffset, key, shelfExtentSize, identities, descending);
    }

    /// <summary>
    /// Converts one full same-key `FSN-16` or `FSN-V` shelf into the shared variable-identity terminal representation.<br/>
    /// The caller supplies the shape discriminator and sorted identity bytes, allowing both fixed-N families to retain one key copy and one maintained linked identity chain.<br/>
    /// </summary>
    /// <param name="rootOffset">The full ordinary shelf offset to convert.<br/></param>
    /// <param name="shape">The terminal shape discriminator for `FSN-16` or `FSN-V`.<br/></param>
    /// <param name="key">The single exhausted fixed key shared by every tuple.<br/></param>
    /// <param name="shelfExtentSize">The terminal variable-identity shelf extent.<br/></param>
    /// <param name="identities">The complete sorted identity sequence including the incoming identity.<br/></param>
    /// <returns>Commit telemetry for the one-root publication.<br/></returns>
    private DataKernelCommitTelemetry RewriteFixedNVarIdentityTerminalRoute(
        long rootOffset,
        byte shape,
        ReadOnlySpan<byte> key,
        int shelfExtentSize,
        ReadOnlySpan<byte[]> identities,
        bool descending = false)
    {
        int payloadCapacity = 0;
        for (int i = 0; i < identities.Length; i++)
            payloadCapacity = checked(payloadCapacity + identities[i].Length);

        using PooledTerminalVarIdentitySet pooled = PooledTerminalVarIdentitySet.Rent(identities.Length, Math.Max(payloadCapacity, shelfExtentSize));
        for (int i = 0; i < identities.Length; i++)
            pooled.Add(identities[i]);

        return WriteNewFixedNVarIdentityTerminalRoute(rootOffset, shape, key, shelfExtentSize, pooled, descending);
    }

    /// <summary>
    /// Writes one new fixed-N variable-identity terminal route over an ordinary shelf offset.<br/>
    /// Identity shelves are appended in ascending order and linked completely before the existing shelf offset is atomically republished as the terminal root.<br/>
    /// </summary>
    /// <param name="rootOffset">The ordinary shelf offset reused by the terminal root.<br/></param>
    /// <param name="shape">The persisted fixed-N terminal shape discriminator.<br/></param>
    /// <param name="key">The complete canonical fixed key.<br/></param>
    /// <param name="shelfExtentSize">The terminal shelf extent.<br/></param>
    /// <param name="identities">The pooled sorted identity workspace.<br/></param>
    /// <returns>Commit telemetry for the terminal publication.<br/></returns>
    private DataKernelCommitTelemetry WriteNewFixedNVarIdentityTerminalRoute(
        long rootOffset,
        byte shape,
        ReadOnlySpan<byte> key,
        int shelfExtentSize,
        PooledTerminalVarIdentitySet identities,
        bool descending)
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
                throw new InvalidDataException("The fixed-N terminal route could not build a fitting identity shelf.");

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
        TerminalIdentityRootLayout.Initialize(rootRewrite.Span, shape, key, shelfExtentSize, firstShelfOffset);
        rootRewrite.Span[TerminalIdentityRootLayout.SortDirectionOffset] = descending ? (byte)1 : (byte)0;
        TerminalIdentityRootLayout.WriteTailShelfOffset(rootRewrite.Span, tailShelfOffset);
        ClearTerminalIdentityReadCaches();
        return CommitAndInvalidateRouterReadCache();
    }

    /// <summary>
    /// Inserts one byte-native identity into an existing `FSN-16` or `FSN-V` terminal chain.<br/>
    /// Existing terminal mutation primitives preserve sorted chain order and reuse the current root until a bounded fallback rewrite is required.<br/>
    /// </summary>
    /// <param name="rootOffset">The matching terminal root offset.<br/></param>
    /// <param name="shape">The expected fixed-N terminal shape.<br/></param>
    /// <param name="key">The complete canonical fixed key.<br/></param>
    /// <param name="shelfExtentSize">The terminal shelf extent.<br/></param>
    /// <param name="identity">The canonical identity bytes to insert.<br/></param>
    /// <param name="allowDuplicateKeys">Whether the fixed key may own more than one identity.<br/></param>
    /// <returns>The shared variable-identity result and publication telemetry.<br/></returns>
    private (Scalar8VarIdentityInsertResult Result, DataKernelCommitTelemetry Commit) InsertFixedNVarTerminalIdentity(
        long rootOffset,
        byte shape,
        ReadOnlySpan<byte> key,
        int shelfExtentSize,
        ReadOnlySpan<byte> identity,
        bool allowDuplicateKeys)
    {
        byte[] rootBytes = ReadTerminalIdentityRootBytes(rootOffset);
        if (TerminalIdentityRootLayout.ReadShape(rootBytes) != shape ||
            !IsTerminalIdentityRootForKey(rootBytes, key, out long firstShelfOffset))
        {
            throw new InvalidDataException("The fixed-N variable terminal root does not match the routed key and shape.");
        }

        if (!allowDuplicateKeys && firstShelfOffset != 0)
            return (Scalar8VarIdentityInsertResult.KeyConflict, default);

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
            return (Scalar8VarIdentityInsertResult.Inserted, appendTelemetry);
        }

        if (TryInsertIntoScalar8VarIdentityTerminalChain(
            rootOffset,
            rootBytes,
            firstShelfOffset,
            shelfExtentSize,
            identity,
            out Scalar8VarIdentityRoutedInsertResult localResult))
        {
            return (localResult.InsertResult, localResult.Commit);
        }

        using PooledTerminalVarIdentitySet identities = ReadScalar8VarIdentityTerminalIdentitiesPooled(rootOffset, key, shelfExtentSize);
        bool descending = rootBytes[TerminalIdentityRootLayout.SortDirectionOffset] != 0;
        int insertIndex = LowerBoundTerminalVarIdentity(identities, identity, descending);
        if (insertIndex < identities.Count && identities.ReadAt(insertIndex).SequenceEqual(identity))
            return (Scalar8VarIdentityInsertResult.AlreadyPresent, default);

        identities.InsertAt(insertIndex, identity);
        DataKernelCommitTelemetry telemetry = RewriteScalar8VarIdentityTerminalRoute(rootOffset, shape, key, shelfExtentSize, identities);
        return (Scalar8VarIdentityInsertResult.Inserted, telemetry);
    }

    /// <summary>
    /// Maps the shared scalar-eight terminal insert result onto the fixed-N scalar contract.<br/>
    /// </summary>
    /// <param name="result">The terminal mutation result to map.<br/></param>
    /// <returns>The equivalent fixed-N scalar insert result.<br/></returns>
    private static FixedNScalarInsertResult MapFixedNScalarInsertResult(Scalar8Scalar8InsertResult result) => result switch
    {
        Scalar8Scalar8InsertResult.Inserted => FixedNScalarInsertResult.Inserted,
        Scalar8Scalar8InsertResult.AlreadyPresent => FixedNScalarInsertResult.AlreadyPresent,
        Scalar8Scalar8InsertResult.KeyConflict => FixedNScalarInsertResult.KeyConflict,
        Scalar8Scalar8InsertResult.Full => FixedNScalarInsertResult.Full,
        _ => throw new InvalidDataException($"Unsupported fixed-N terminal scalar insert result {result}.")
    };

    /// <summary>
    /// Reserves the intermediate router offsets required by one fixed-N shelf transform.<br/>
    /// Free 4 KiB pages inside the former shelf extent are selected first; only a chain longer than the local arena capacity appends standalone router pages.<br/>
    /// </summary>
    /// <param name="arenaBaseOffset">The former shelf offset that becomes router page zero.<br/></param>
    /// <param name="arenaLength">The complete former shelf extent available as a router micro-arena.<br/></param>
    /// <param name="routerCount">The number of intermediate router pages required after page zero.<br/></param>
    /// <param name="localRouterCount">Receives the number of returned offsets placed inside the former shelf extent.<br/></param>
    /// <returns>The reserved router offsets in root-to-leaf chain order.<br/></returns>
    private long[] ReserveFixedNTransformRouterOffsets(
        long arenaBaseOffset,
        int arenaLength,
        int routerCount,
        out int localRouterCount)
    {
        int localCapacity = Math.Max(0, (arenaLength / RouterLayout.Size) - 1);
        localRouterCount = Math.Min(routerCount, localCapacity);
        long[] offsets = routerCount == 0 ? [] : new long[routerCount];
        for (int i = 0; i < routerCount; i++)
        {
            offsets[i] = i < localRouterCount
                ? checked(arenaBaseOffset + ((long)(i + 1) * RouterLayout.Size))
                : kernel.Reserve(RouterLayout.Size).Extent.Offset;
        }

        return offsets;
    }

    /// <summary>
    /// Persists router-arena ownership metadata for one fixed-N router page that resides inside a converted shelf extent.<br/>
    /// Standalone appended routers deliberately carry no arena metadata because they do not own reusable neighboring pages.<br/>
    /// </summary>
    /// <param name="writer">The initialized router writer whose header should receive arena metadata.<br/></param>
    /// <param name="routerOffset">The physical offset of the router page.<br/></param>
    /// <param name="arenaBaseOffset">The converted shelf arena base offset.<br/></param>
    /// <param name="arenaLength">The complete converted shelf extent length.<br/></param>
    private static void WriteFixedNTransformRouterArenaMetadata(
        RouterWriter writer,
        long routerOffset,
        long arenaBaseOffset,
        int arenaLength)
    {
        long relativeOffset = routerOffset - arenaBaseOffset;
        if (relativeOffset < 0 || relativeOffset >= arenaLength || relativeOffset % RouterLayout.Size != 0)
            return;

        int pageIndex = checked((int)(relativeOffset / RouterLayout.Size));
        writer.WriteArenaMetadata(
            arenaBaseDelta: checked((int)(arenaBaseOffset - routerOffset)),
            arenaLength,
            routerPageSize: checked((ushort)RouterLayout.Size),
            routerPageIndex: checked((ushort)pageIndex),
            routerPageCount: checked((ushort)(arenaLength / RouterLayout.Size)),
            arenaFlags: 0);
    }
}
