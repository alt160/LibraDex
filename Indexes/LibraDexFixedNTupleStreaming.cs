using LibraDex.Layouts;
using LibraDex.Views;

namespace LibraDex;

/// <summary>
/// Carries one authoritative fixed-key / scalar-8 tuple from physical storage.<br/>
/// Both arrays and scalar values are disconnected from the shelf buffer so the tuple remains valid after traversal advances.<br/>
/// </summary>
/// <param name="Key">The owned sortable fixed-width key bytes.<br/></param>
/// <param name="EncodedIdentity">The encoded sortable scalar-8 identity.<br/></param>
internal readonly record struct FixedNScalar8Tuple(byte[] Key, ulong EncodedIdentity);

/// <summary>
/// Carries one authoritative fixed-key / scalar-16 tuple from physical storage.<br/>
/// Both byte arrays are disconnected from the shelf buffer so the tuple remains valid after traversal advances.<br/>
/// </summary>
/// <param name="Key">The owned sortable fixed-width key bytes.<br/></param>
/// <param name="EncodedIdentity">The owned encoded sortable scalar-16 identity bytes.<br/></param>
internal readonly record struct FixedNScalar16Tuple(byte[] Key, byte[] EncodedIdentity);

/// <summary>
/// Carries one authoritative fixed-key / variable-identity tuple from physical storage.<br/>
/// Both byte arrays are disconnected from the shelf buffer so the tuple remains valid after traversal advances.<br/>
/// </summary>
/// <param name="Key">The owned sortable fixed-width key bytes.<br/></param>
/// <param name="Identity">The owned raw variable-length identity bytes.<br/></param>
internal readonly record struct FixedNVarIdentityTuple(byte[] Key, byte[] Identity);

internal sealed partial class LibraDexFileSession
{
    /// <summary>
    /// Streams every live tuple reachable from one fixed-key / scalar-8 handle in physical key order.<br/>
    /// Routed traversal follows prefix targets from low to high and suppresses repeated target offsets, while single-shelf handles read their one durable shelf directly.<br/>
    /// </summary>
    /// <param name="handle">The fixed-N scalar-8 index handle to traverse.<br/></param>
    /// <param name="direction">The requested complete key and identity tuple direction.<br/></param>
    /// <param name="lowerKey">Optional inclusive encoded lower key for router and shelf pruning.<br/></param>
    /// <param name="upperKey">Optional inclusive encoded upper key for router and shelf pruning.<br/></param>
    /// <returns>Owned encoded tuples in key/identity order.<br/></returns>
    internal IEnumerable<FixedNScalar8Tuple> IterateFixedNScalar8Tuples(
        FixedNScalar8IndexHandle handle,
        QueryDirection direction = QueryDirection.Ascending,
        byte[]? lowerKey = null,
        byte[]? upperKey = null)
    {
        handle.Validate();
        bool descending = direction == QueryDirection.Descending;
        if (!descending && direction != QueryDirection.Ascending)
            throw new ArgumentOutOfRangeException(nameof(direction));
        if ((lowerKey is not null && lowerKey.Length != handle.Profile.KeySize) ||
            (upperKey is not null && upperKey.Length != handle.Profile.KeySize) ||
            (lowerKey is not null && upperKey is not null && lowerKey.AsSpan().SequenceCompareTo(upperKey) > 0))
            throw new ArgumentException("FSN-8 tuple-stream bounds must match the fixed key width and retain logical lower-to-upper order.");
        if (!handle.IsRouted)
        {
            foreach (FixedNScalar8Tuple tuple in ReadFixedNScalar8ShelfTuples(handle.RootOffset, handle.Profile, descending, lowerKey, upperKey))
                yield return tuple;
            yield break;
        }

        HashSet<long> visitedShelves = new();
        HashSet<long> visitedRouters = new();
        foreach (FixedNScalar8Tuple tuple in IterateFixedNScalar8RouterTuples(
            handle.RootOffset,
            handle.Profile,
            visitedShelves,
            visitedRouters,
            descending,
            lowerKey,
            upperKey,
            lowerEdge: true,
            upperEdge: true))
        {
            yield return tuple;
        }
    }

    /// <summary>
    /// Streams every live tuple reachable from one fixed-key / scalar-16 handle in physical key order.<br/>
    /// The returned identity bytes remain in the sortable persisted representation expected by the BigInteger facade codec.<br/>
    /// </summary>
    /// <param name="handle">The fixed-N scalar-16 index handle to traverse.<br/></param>
    /// <param name="direction">The requested complete key and identity tuple direction.<br/></param>
    /// <param name="lowerKey">Optional inclusive encoded lower key for router and shelf pruning.<br/></param>
    /// <param name="upperKey">Optional inclusive encoded upper key for router and shelf pruning.<br/></param>
    /// <returns>Owned encoded tuples in key/identity order.<br/></returns>
    internal IEnumerable<FixedNScalar16Tuple> IterateFixedNScalar16Tuples(
        FixedNScalar16IndexHandle handle,
        QueryDirection direction = QueryDirection.Ascending,
        byte[]? lowerKey = null,
        byte[]? upperKey = null)
    {
        handle.Validate();
        bool descending = direction == QueryDirection.Descending;
        if (!descending && direction != QueryDirection.Ascending)
            throw new ArgumentOutOfRangeException(nameof(direction));
        if ((lowerKey is not null && lowerKey.Length != handle.Profile.KeySize) ||
            (upperKey is not null && upperKey.Length != handle.Profile.KeySize) ||
            (lowerKey is not null && upperKey is not null && lowerKey.AsSpan().SequenceCompareTo(upperKey) > 0))
            throw new ArgumentException("FSN-16 tuple-stream bounds must match the fixed key width and retain logical lower-to-upper order.");
        if (!handle.IsRouted)
        {
            foreach (FixedNScalar16Tuple tuple in ReadFixedNScalar16ShelfTuples(handle.RootOffset, handle.Profile, descending, lowerKey, upperKey))
                yield return tuple;
            yield break;
        }

        HashSet<long> visitedShelves = new();
        HashSet<long> visitedRouters = new();
        foreach (FixedNScalar16Tuple tuple in IterateFixedNScalar16RouterTuples(
            handle.RootOffset,
            handle.Profile,
            visitedShelves,
            visitedRouters,
            descending,
            lowerKey,
            upperKey,
            lowerEdge: true,
            upperEdge: true))
        {
            yield return tuple;
        }
    }

    /// <summary>
    /// Streams every live tuple reachable from one fixed-key / variable-identity handle in physical key order.<br/>
    /// Variable identity payloads are copied once per yielded row because their shelf spans become invalid when traversal advances.<br/>
    /// </summary>
    /// <param name="handle">The fixed-N variable-identity index handle to traverse.<br/></param>
    /// <param name="direction">The requested complete key and identity tuple direction.<br/></param>
    /// <param name="lowerKey">Optional inclusive encoded lower key for router and shelf pruning.<br/></param>
    /// <param name="upperKey">Optional inclusive encoded upper key for router and shelf pruning.<br/></param>
    /// <returns>Owned encoded key/raw identity tuples in key/identity order.<br/></returns>
    internal IEnumerable<FixedNVarIdentityTuple> IterateFixedNVarIdentityTuples(
        FixedNVarIdentityIndexHandle handle,
        QueryDirection direction = QueryDirection.Ascending,
        byte[]? lowerKey = null,
        byte[]? upperKey = null)
    {
        handle.Validate();
        bool descending = direction == QueryDirection.Descending;
        if (!descending && direction != QueryDirection.Ascending)
            throw new ArgumentOutOfRangeException(nameof(direction));
        if ((lowerKey is not null && lowerKey.Length != handle.Profile.KeySize) ||
            (upperKey is not null && upperKey.Length != handle.Profile.KeySize) ||
            (lowerKey is not null && upperKey is not null && lowerKey.AsSpan().SequenceCompareTo(upperKey) > 0))
            throw new ArgumentException("FSN-V tuple-stream bounds must match the fixed key width and retain logical lower-to-upper order.");
        if (!handle.IsRouted)
        {
            foreach (FixedNVarIdentityTuple tuple in ReadFixedNVarIdentityShelfTuples(handle.RootOffset, handle.Profile, descending, lowerKey, upperKey))
                yield return tuple;
            yield break;
        }

        HashSet<long> visitedShelves = new();
        HashSet<long> visitedRouters = new();
        foreach (FixedNVarIdentityTuple tuple in IterateFixedNVarIdentityRouterTuples(
            handle.RootOffset,
            handle.Profile,
            visitedShelves,
            visitedRouters,
            descending,
            lowerKey,
            upperKey,
            lowerEdge: true,
            upperEdge: true))
        {
            yield return tuple;
        }
    }

    private IEnumerable<FixedNScalar8Tuple> IterateFixedNScalar8RouterTuples(
        long routerOffset,
        FixedNScalar8Profile profile,
        HashSet<long> visitedShelves,
        HashSet<long> visitedRouters,
        bool descending,
        byte[]? lowerKey,
        byte[]? upperKey,
        bool lowerEdge,
        bool upperEdge)
    {
        if (!visitedRouters.Add(routerOffset))
            yield break;

        long[] targets = ReadFixedNTupleRouterTargets(routerOffset, "FSN-8");
        ushort keyDepth = ReadRouterSnapshot(routerOffset).KeyDepth;
        int startPrefix = lowerEdge && lowerKey is not null ? lowerKey[keyDepth] : byte.MinValue;
        int endPrefix = upperEdge && upperKey is not null ? upperKey[keyDepth] : byte.MaxValue;
        for (int i = descending ? endPrefix : startPrefix;
            descending ? i >= startPrefix : i <= endPrefix;
            i += descending ? -1 : 1)
        {
            long targetOffset = targets[i];
            if (targetOffset == 0)
                continue;

            if (IsFixedNScalar8Router(targetOffset))
            {
                bool childLowerEdge = lowerEdge && i == startPrefix;
                bool childUpperEdge = upperEdge && i == endPrefix;
                if (childLowerEdge || childUpperEdge)
                {
                    for (int aliasPrefix = startPrefix; aliasPrefix <= endPrefix; aliasPrefix++)
                    {
                        if (aliasPrefix != i && targets[aliasPrefix] == targetOffset)
                        {
                            childLowerEdge = false;
                            childUpperEdge = false;
                            break;
                        }
                    }
                }
                foreach (FixedNScalar8Tuple tuple in IterateFixedNScalar8RouterTuples(
                    targetOffset,
                    profile,
                    visitedShelves,
                    visitedRouters,
                    descending,
                    lowerKey,
                    upperKey,
                    childLowerEdge,
                    childUpperEdge))
                {
                    yield return tuple;
                }

                continue;
            }

            if (TryReadFixedNTerminalRoot(
                targetOffset,
                TerminalIdentityRootLayout.ShapeFixedKeyScalar8Identity,
                profile.KeySize,
                profile.ShelfExtentSize,
                out byte[] terminalRoot))
            {
                if (!visitedShelves.Add(targetOffset))
                    continue;

                byte[] terminalKey = terminalRoot.AsSpan(TerminalIdentityRootLayout.KeyBytesOffset, profile.KeySize).ToArray();
                if ((lowerKey is not null && terminalKey.AsSpan().SequenceCompareTo(lowerKey) < 0) ||
                    (upperKey is not null && terminalKey.AsSpan().SequenceCompareTo(upperKey) > 0))
                    continue;
                bool terminalDescending = terminalRoot[TerminalIdentityRootLayout.SortDirectionOffset] != 0;
                if (terminalDescending != profile.Descending)
                    throw new InvalidDataException("The routed FSN-8 terminal direction does not match its owning index.");
                if (descending == terminalDescending)
                {
                    long shelfOffset = TerminalIdentityRootLayout.ReadFirstShelfOffset(terminalRoot);
                    while (shelfOffset != 0)
                    {
                        byte[] shelfBytes = ReadTerminalIdentity8ShelfBytesCached(shelfOffset, profile.ShelfExtentSize);
                        int count = TerminalIdentity8ShelfLayout.ReadItemCount(shelfBytes);
                        for (int identityIndex = 0; identityIndex < count; identityIndex++)
                            yield return new FixedNScalar8Tuple(terminalKey, TerminalIdentity8ShelfLayout.ReadIdentity(shelfBytes, identityIndex));
                        shelfOffset = TerminalIdentity8ShelfLayout.ReadNextShelfOffset(shelfBytes);
                    }
                }
                else
                {
                    List<ulong> terminalIdentities = ReadTerminalIdentity8RouteIdentities(targetOffset, terminalKey, profile.ShelfExtentSize);
                    for (int identityIndex = terminalIdentities.Count - 1; identityIndex >= 0; identityIndex--)
                        yield return new FixedNScalar8Tuple(terminalKey, terminalIdentities[identityIndex]);
                }
                continue;
            }

            if (!visitedShelves.Add(targetOffset))
                continue;

            foreach (FixedNScalar8Tuple tuple in ReadFixedNScalar8ShelfTuples(targetOffset, profile, descending, lowerKey, upperKey))
                yield return tuple;
        }
    }

    private IEnumerable<FixedNScalar16Tuple> IterateFixedNScalar16RouterTuples(
        long routerOffset,
        FixedNScalar16Profile profile,
        HashSet<long> visitedShelves,
        HashSet<long> visitedRouters,
        bool descending,
        byte[]? lowerKey,
        byte[]? upperKey,
        bool lowerEdge,
        bool upperEdge)
    {
        if (!visitedRouters.Add(routerOffset))
            yield break;

        long[] targets = ReadFixedNTupleRouterTargets(routerOffset, "FSN-16");
        ushort keyDepth = ReadRouterSnapshot(routerOffset).KeyDepth;
        int startPrefix = lowerEdge && lowerKey is not null ? lowerKey[keyDepth] : byte.MinValue;
        int endPrefix = upperEdge && upperKey is not null ? upperKey[keyDepth] : byte.MaxValue;
        for (int i = descending ? endPrefix : startPrefix;
            descending ? i >= startPrefix : i <= endPrefix;
            i += descending ? -1 : 1)
        {
            long targetOffset = targets[i];
            if (targetOffset == 0)
                continue;

            if (IsFixedNScalar16Router(targetOffset))
            {
                bool childLowerEdge = lowerEdge && i == startPrefix;
                bool childUpperEdge = upperEdge && i == endPrefix;
                if (childLowerEdge || childUpperEdge)
                {
                    for (int aliasPrefix = startPrefix; aliasPrefix <= endPrefix; aliasPrefix++)
                    {
                        if (aliasPrefix != i && targets[aliasPrefix] == targetOffset)
                        {
                            childLowerEdge = false;
                            childUpperEdge = false;
                            break;
                        }
                    }
                }
                foreach (FixedNScalar16Tuple tuple in IterateFixedNScalar16RouterTuples(
                    targetOffset,
                    profile,
                    visitedShelves,
                    visitedRouters,
                    descending,
                    lowerKey,
                    upperKey,
                    childLowerEdge,
                    childUpperEdge))
                {
                    yield return tuple;
                }

                continue;
            }

            if (TryReadFixedNTerminalRoot(
                targetOffset,
                TerminalIdentityRootLayout.ShapeFixedKeyScalar16Identity,
                profile.KeySize,
                profile.ShelfExtentSize,
                out byte[] terminalRoot))
            {
                if (!visitedShelves.Add(targetOffset))
                    continue;

                byte[] terminalKey = terminalRoot.AsSpan(TerminalIdentityRootLayout.KeyBytesOffset, profile.KeySize).ToArray();
                if ((lowerKey is not null && terminalKey.AsSpan().SequenceCompareTo(lowerKey) < 0) ||
                    (upperKey is not null && terminalKey.AsSpan().SequenceCompareTo(upperKey) > 0))
                    continue;
                bool terminalDescending = terminalRoot[TerminalIdentityRootLayout.SortDirectionOffset] != 0;
                if (terminalDescending != profile.Descending)
                    throw new InvalidDataException("The routed FSN-16 terminal direction does not match its owning index.");
                if (descending == terminalDescending)
                {
                    long shelfOffset = TerminalIdentityRootLayout.ReadFirstShelfOffset(terminalRoot);
                    while (shelfOffset != 0)
                    {
                        byte[] shelfBytes = ReadTerminalVarIdentityShelfBytesCached(shelfOffset, profile.ShelfExtentSize);
                        int count = TerminalVarIdentityShelfLayout.ReadItemCount(shelfBytes);
                        for (int identityIndex = 0; identityIndex < count; identityIndex++)
                            yield return new FixedNScalar16Tuple(terminalKey, TerminalVarIdentityShelfLayout.ReadIdentityAt(shelfBytes, identityIndex).ToArray());
                        shelfOffset = TerminalVarIdentityShelfLayout.ReadNextShelfOffset(shelfBytes);
                    }
                }
                else
                {
                    byte[][] terminalIdentities = ReadScalar8VarIdentityTerminalIdentities(targetOffset, terminalKey, profile.ShelfExtentSize);
                    for (int identityIndex = terminalIdentities.Length - 1; identityIndex >= 0; identityIndex--)
                        yield return new FixedNScalar16Tuple(terminalKey, terminalIdentities[identityIndex]);
                }
                continue;
            }

            if (!visitedShelves.Add(targetOffset))
                continue;

            foreach (FixedNScalar16Tuple tuple in ReadFixedNScalar16ShelfTuples(targetOffset, profile, descending, lowerKey, upperKey))
                yield return tuple;
        }
    }

    private IEnumerable<FixedNVarIdentityTuple> IterateFixedNVarIdentityRouterTuples(
        long routerOffset,
        FixedNVarIdentityProfile profile,
        HashSet<long> visitedShelves,
        HashSet<long> visitedRouters,
        bool descending,
        byte[]? lowerKey,
        byte[]? upperKey,
        bool lowerEdge,
        bool upperEdge)
    {
        if (!visitedRouters.Add(routerOffset))
            yield break;

        long[] targets = ReadFixedNTupleRouterTargets(routerOffset, "FV");
        ushort keyDepth = ReadRouterSnapshot(routerOffset).KeyDepth;
        int startPrefix = lowerEdge && lowerKey is not null ? lowerKey[keyDepth] : byte.MinValue;
        int endPrefix = upperEdge && upperKey is not null ? upperKey[keyDepth] : byte.MaxValue;
        for (int i = descending ? endPrefix : startPrefix;
            descending ? i >= startPrefix : i <= endPrefix;
            i += descending ? -1 : 1)
        {
            long targetOffset = targets[i];
            if (targetOffset == 0)
                continue;

            if (IsFixedNVarIdentityRouter(targetOffset))
            {
                bool childLowerEdge = lowerEdge && i == startPrefix;
                bool childUpperEdge = upperEdge && i == endPrefix;
                if (childLowerEdge || childUpperEdge)
                {
                    for (int aliasPrefix = startPrefix; aliasPrefix <= endPrefix; aliasPrefix++)
                    {
                        if (aliasPrefix != i && targets[aliasPrefix] == targetOffset)
                        {
                            childLowerEdge = false;
                            childUpperEdge = false;
                            break;
                        }
                    }
                }
                foreach (FixedNVarIdentityTuple tuple in IterateFixedNVarIdentityRouterTuples(
                    targetOffset,
                    profile,
                    visitedShelves,
                    visitedRouters,
                    descending,
                    lowerKey,
                    upperKey,
                    childLowerEdge,
                    childUpperEdge))
                {
                    yield return tuple;
                }

                continue;
            }

            if (TryReadFixedNTerminalRoot(
                targetOffset,
                TerminalIdentityRootLayout.ShapeFixedKeyVarIdentity,
                profile.KeySize,
                profile.ShelfExtentSize,
                out byte[] terminalRoot))
            {
                if (!visitedShelves.Add(targetOffset))
                    continue;

                byte[] terminalKey = terminalRoot.AsSpan(TerminalIdentityRootLayout.KeyBytesOffset, profile.KeySize).ToArray();
                if ((lowerKey is not null && terminalKey.AsSpan().SequenceCompareTo(lowerKey) < 0) ||
                    (upperKey is not null && terminalKey.AsSpan().SequenceCompareTo(upperKey) > 0))
                    continue;
                bool terminalDescending = terminalRoot[TerminalIdentityRootLayout.SortDirectionOffset] != 0;
                if (terminalDescending != profile.Descending)
                    throw new InvalidDataException("The routed FSN-V terminal direction does not match its owning index.");
                if (descending == terminalDescending)
                {
                    long shelfOffset = TerminalIdentityRootLayout.ReadFirstShelfOffset(terminalRoot);
                    while (shelfOffset != 0)
                    {
                        byte[] shelfBytes = ReadTerminalVarIdentityShelfBytesCached(shelfOffset, profile.ShelfExtentSize);
                        int count = TerminalVarIdentityShelfLayout.ReadItemCount(shelfBytes);
                        for (int identityIndex = 0; identityIndex < count; identityIndex++)
                            yield return new FixedNVarIdentityTuple(terminalKey, TerminalVarIdentityShelfLayout.ReadIdentityAt(shelfBytes, identityIndex).ToArray());
                        shelfOffset = TerminalVarIdentityShelfLayout.ReadNextShelfOffset(shelfBytes);
                    }
                }
                else
                {
                    byte[][] terminalIdentities = ReadScalar8VarIdentityTerminalIdentities(targetOffset, terminalKey, profile.ShelfExtentSize);
                    for (int identityIndex = terminalIdentities.Length - 1; identityIndex >= 0; identityIndex--)
                        yield return new FixedNVarIdentityTuple(terminalKey, terminalIdentities[identityIndex]);
                }
                continue;
            }

            if (!visitedShelves.Add(targetOffset))
                continue;

            foreach (FixedNVarIdentityTuple tuple in ReadFixedNVarIdentityShelfTuples(targetOffset, profile, descending, lowerKey, upperKey))
                yield return tuple;
        }
    }

    private IEnumerable<FixedNScalar8Tuple> ReadFixedNScalar8ShelfTuples(
        long shelfOffset,
        FixedNScalar8Profile profile,
        bool descending,
        byte[]? lowerKey,
        byte[]? upperKey)
    {
        byte[] shelfBytes = ReadFixedNScalar8ShelfBytes(shelfOffset, profile);
        int itemCount = FixedNScalar8Layout.ReadItemCount(shelfBytes);
        bool reverseSlots = descending != profile.Descending;
        for (int i = reverseSlots ? itemCount - 1 : 0;
            reverseSlots ? i >= 0 : i < itemCount;
            i += reverseSlots ? -1 : 1)
        {
            ushort itemOffset = FixedNScalar8Layout.ReadSlot(shelfBytes, profile, i);
            int lowerComparison = lowerKey is null ? 1 : FixedNScalar8Layout.ReadItemKey(shelfBytes, itemOffset, profile).SequenceCompareTo(lowerKey);
            int upperComparison = upperKey is null ? -1 : FixedNScalar8Layout.ReadItemKey(shelfBytes, itemOffset, profile).SequenceCompareTo(upperKey);
            if (descending ? lowerComparison < 0 : upperComparison > 0)
                yield break;
            if (lowerComparison < 0 || upperComparison > 0)
                continue;

            byte[] key = FixedNScalar8Layout.ReadItemKey(shelfBytes, itemOffset, profile).ToArray();
            yield return new FixedNScalar8Tuple(key, FixedNScalar8Layout.ReadItemIdentity(shelfBytes, itemOffset, profile));
        }
    }

    private IEnumerable<FixedNScalar16Tuple> ReadFixedNScalar16ShelfTuples(
        long shelfOffset,
        FixedNScalar16Profile profile,
        bool descending,
        byte[]? lowerKey,
        byte[]? upperKey)
    {
        byte[] shelfBytes = ReadFixedNScalar16ShelfBytes(shelfOffset, profile);
        int itemCount = FixedNScalar16Layout.ReadItemCount(shelfBytes);
        bool reverseSlots = descending != profile.Descending;
        for (int i = reverseSlots ? itemCount - 1 : 0;
            reverseSlots ? i >= 0 : i < itemCount;
            i += reverseSlots ? -1 : 1)
        {
            ushort itemOffset = FixedNScalar16Layout.ReadSlot(shelfBytes, profile, i);
            int lowerComparison = lowerKey is null ? 1 : FixedNScalar16Layout.ReadItemKey(shelfBytes, itemOffset, profile).SequenceCompareTo(lowerKey);
            int upperComparison = upperKey is null ? -1 : FixedNScalar16Layout.ReadItemKey(shelfBytes, itemOffset, profile).SequenceCompareTo(upperKey);
            if (descending ? lowerComparison < 0 : upperComparison > 0)
                yield break;
            if (lowerComparison < 0 || upperComparison > 0)
                continue;

            byte[] key = FixedNScalar16Layout.ReadItemKey(shelfBytes, itemOffset, profile).ToArray();
            byte[] identity = FixedNScalar16Layout.ReadItemIdentity(shelfBytes, itemOffset, profile).ToArray();
            yield return new FixedNScalar16Tuple(key, identity);
        }
    }

    private IEnumerable<FixedNVarIdentityTuple> ReadFixedNVarIdentityShelfTuples(
        long shelfOffset,
        FixedNVarIdentityProfile profile,
        bool descending,
        byte[]? lowerKey,
        byte[]? upperKey)
    {
        byte[] shelfBytes = ReadFixedNVarIdentityShelfBytes(shelfOffset, profile);
        FixedNVarIdentityReadOnly shelf = new(shelfBytes, profile);
        if (!shelf.IsValid)
            throw new InvalidDataException($"The FV tuple-stream shelf at offset {shelfOffset:n0} is invalid.");

        bool reverseSlots = descending != profile.Descending;
        for (int i = reverseSlots ? shelf.ItemCount - 1 : 0;
            reverseSlots ? i >= 0 : i < shelf.ItemCount;
            i += reverseSlots ? -1 : 1)
        {
            int lowerComparison = lowerKey is null ? 1 : shelf.ReadKeyAt(i).SequenceCompareTo(lowerKey);
            int upperComparison = upperKey is null ? -1 : shelf.ReadKeyAt(i).SequenceCompareTo(upperKey);
            if (descending ? lowerComparison < 0 : upperComparison > 0)
                yield break;
            if (lowerComparison < 0 || upperComparison > 0)
                continue;

            yield return new FixedNVarIdentityTuple(shelf.ReadKeyAt(i).ToArray(), shelf.ReadIdentityAt(i).ToArray());
        }
    }

    private long[] ReadFixedNTupleRouterTargets(long routerOffset, string shapeName)
    {
        byte[] routerBytes = new byte[RouterLayout.Size];
        ReadRouterPageUsingArenaCache(routerOffset, routerBytes);
        RouterReader router = new(routerBytes);
        if (!router.IsValid || !router.HasDirectIndex)
            throw new InvalidDataException($"The routed {shapeName} tuple-stream router at offset {routerOffset:n0} is invalid.");

        long[] targets = new long[256];
        for (int prefix = 0; prefix < targets.Length; prefix++)
            targets[prefix] = router.FindTarget((byte)prefix);
        return targets;
    }
}
