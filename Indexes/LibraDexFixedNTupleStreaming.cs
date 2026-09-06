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
    /// <returns>Owned encoded tuples in key/identity order.<br/></returns>
    internal IEnumerable<FixedNScalar8Tuple> IterateFixedNScalar8Tuples(FixedNScalar8IndexHandle handle)
    {
        handle.Validate();
        if (!handle.IsRouted)
        {
            foreach (FixedNScalar8Tuple tuple in ReadFixedNScalar8ShelfTuples(handle.RootOffset, handle.Profile))
                yield return tuple;
            yield break;
        }

        HashSet<long> visitedShelves = new();
        HashSet<long> visitedRouters = new();
        foreach (FixedNScalar8Tuple tuple in IterateFixedNScalar8RouterTuples(
            handle.RootOffset,
            handle.Profile,
            visitedShelves,
            visitedRouters))
        {
            yield return tuple;
        }
    }

    /// <summary>
    /// Streams every live tuple reachable from one fixed-key / scalar-16 handle in physical key order.<br/>
    /// The returned identity bytes remain in the sortable persisted representation expected by the BigInteger facade codec.<br/>
    /// </summary>
    /// <param name="handle">The fixed-N scalar-16 index handle to traverse.<br/></param>
    /// <returns>Owned encoded tuples in key/identity order.<br/></returns>
    internal IEnumerable<FixedNScalar16Tuple> IterateFixedNScalar16Tuples(FixedNScalar16IndexHandle handle)
    {
        handle.Validate();
        if (!handle.IsRouted)
        {
            foreach (FixedNScalar16Tuple tuple in ReadFixedNScalar16ShelfTuples(handle.RootOffset, handle.Profile))
                yield return tuple;
            yield break;
        }

        HashSet<long> visitedShelves = new();
        HashSet<long> visitedRouters = new();
        foreach (FixedNScalar16Tuple tuple in IterateFixedNScalar16RouterTuples(
            handle.RootOffset,
            handle.Profile,
            visitedShelves,
            visitedRouters))
        {
            yield return tuple;
        }
    }

    /// <summary>
    /// Streams every live tuple reachable from one fixed-key / variable-identity handle in physical key order.<br/>
    /// Variable identity payloads are copied once per yielded row because their shelf spans become invalid when traversal advances.<br/>
    /// </summary>
    /// <param name="handle">The fixed-N variable-identity index handle to traverse.<br/></param>
    /// <returns>Owned encoded key/raw identity tuples in key/identity order.<br/></returns>
    internal IEnumerable<FixedNVarIdentityTuple> IterateFixedNVarIdentityTuples(FixedNVarIdentityIndexHandle handle)
    {
        handle.Validate();
        if (!handle.IsRouted)
        {
            foreach (FixedNVarIdentityTuple tuple in ReadFixedNVarIdentityShelfTuples(handle.RootOffset, handle.Profile))
                yield return tuple;
            yield break;
        }

        HashSet<long> visitedShelves = new();
        HashSet<long> visitedRouters = new();
        foreach (FixedNVarIdentityTuple tuple in IterateFixedNVarIdentityRouterTuples(
            handle.RootOffset,
            handle.Profile,
            visitedShelves,
            visitedRouters))
        {
            yield return tuple;
        }
    }

    private IEnumerable<FixedNScalar8Tuple> IterateFixedNScalar8RouterTuples(
        long routerOffset,
        FixedNScalar8Profile profile,
        HashSet<long> visitedShelves,
        HashSet<long> visitedRouters)
    {
        if (!visitedRouters.Add(routerOffset))
            yield break;

        long[] targets = ReadFixedNTupleRouterTargets(routerOffset, "FSN-8");
        for (int i = 0; i < targets.Length; i++)
        {
            long targetOffset = targets[i];
            if (targetOffset == 0)
                continue;

            if (IsFixedNScalar8Router(targetOffset))
            {
                foreach (FixedNScalar8Tuple tuple in IterateFixedNScalar8RouterTuples(
                    targetOffset,
                    profile,
                    visitedShelves,
                    visitedRouters))
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
                List<ulong> terminalIdentities = ReadTerminalIdentity8RouteIdentities(targetOffset, terminalKey, profile.ShelfExtentSize);
                for (int identityIndex = 0; identityIndex < terminalIdentities.Count; identityIndex++)
                    yield return new FixedNScalar8Tuple(terminalKey, terminalIdentities[identityIndex]);
                continue;
            }

            if (!visitedShelves.Add(targetOffset))
                continue;

            foreach (FixedNScalar8Tuple tuple in ReadFixedNScalar8ShelfTuples(targetOffset, profile))
                yield return tuple;
        }
    }

    private IEnumerable<FixedNScalar16Tuple> IterateFixedNScalar16RouterTuples(
        long routerOffset,
        FixedNScalar16Profile profile,
        HashSet<long> visitedShelves,
        HashSet<long> visitedRouters)
    {
        if (!visitedRouters.Add(routerOffset))
            yield break;

        long[] targets = ReadFixedNTupleRouterTargets(routerOffset, "FSN-16");
        for (int i = 0; i < targets.Length; i++)
        {
            long targetOffset = targets[i];
            if (targetOffset == 0)
                continue;

            if (IsFixedNScalar16Router(targetOffset))
            {
                foreach (FixedNScalar16Tuple tuple in IterateFixedNScalar16RouterTuples(
                    targetOffset,
                    profile,
                    visitedShelves,
                    visitedRouters))
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
                byte[][] terminalIdentities = ReadScalar8VarIdentityTerminalIdentities(targetOffset, terminalKey, profile.ShelfExtentSize);
                for (int identityIndex = 0; identityIndex < terminalIdentities.Length; identityIndex++)
                    yield return new FixedNScalar16Tuple(terminalKey, terminalIdentities[identityIndex]);
                continue;
            }

            if (!visitedShelves.Add(targetOffset))
                continue;

            foreach (FixedNScalar16Tuple tuple in ReadFixedNScalar16ShelfTuples(targetOffset, profile))
                yield return tuple;
        }
    }

    private IEnumerable<FixedNVarIdentityTuple> IterateFixedNVarIdentityRouterTuples(
        long routerOffset,
        FixedNVarIdentityProfile profile,
        HashSet<long> visitedShelves,
        HashSet<long> visitedRouters)
    {
        if (!visitedRouters.Add(routerOffset))
            yield break;

        long[] targets = ReadFixedNTupleRouterTargets(routerOffset, "FV");
        for (int i = 0; i < targets.Length; i++)
        {
            long targetOffset = targets[i];
            if (targetOffset == 0)
                continue;

            if (IsFixedNVarIdentityRouter(targetOffset))
            {
                foreach (FixedNVarIdentityTuple tuple in IterateFixedNVarIdentityRouterTuples(
                    targetOffset,
                    profile,
                    visitedShelves,
                    visitedRouters))
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
                byte[][] terminalIdentities = ReadScalar8VarIdentityTerminalIdentities(targetOffset, terminalKey, profile.ShelfExtentSize);
                for (int identityIndex = 0; identityIndex < terminalIdentities.Length; identityIndex++)
                    yield return new FixedNVarIdentityTuple(terminalKey, terminalIdentities[identityIndex]);
                continue;
            }

            if (!visitedShelves.Add(targetOffset))
                continue;

            foreach (FixedNVarIdentityTuple tuple in ReadFixedNVarIdentityShelfTuples(targetOffset, profile))
                yield return tuple;
        }
    }

    private FixedNScalar8Tuple[] ReadFixedNScalar8ShelfTuples(
        long shelfOffset,
        FixedNScalar8Profile profile)
    {
        byte[] shelfBytes = ReadFixedNScalar8ShelfBytes(shelfOffset, profile);
        FixedNScalar8ReadOnly shelf = new(shelfBytes, profile);
        if (!shelf.IsValid)
            throw new InvalidDataException($"The FSN-8 tuple-stream shelf at offset {shelfOffset:n0} is invalid.");

        FixedNScalar8Tuple[] tuples = new FixedNScalar8Tuple[shelf.ItemCount];
        for (int i = 0; i < shelf.ItemCount; i++)
        {
            byte[] key = new byte[profile.KeySize];
            shelf.CopyKeyAt(i, key);
            tuples[i] = new FixedNScalar8Tuple(key, shelf.ReadIdentityAt(i));
        }

        return tuples;
    }

    private FixedNScalar16Tuple[] ReadFixedNScalar16ShelfTuples(
        long shelfOffset,
        FixedNScalar16Profile profile)
    {
        byte[] shelfBytes = ReadFixedNScalar16ShelfBytes(shelfOffset, profile);
        FixedNScalar16ReadOnly shelf = new(shelfBytes, profile);
        if (!shelf.IsValid)
            throw new InvalidDataException($"The FSN-16 tuple-stream shelf at offset {shelfOffset:n0} is invalid.");

        FixedNScalar16Tuple[] tuples = new FixedNScalar16Tuple[shelf.ItemCount];
        for (int i = 0; i < shelf.ItemCount; i++)
        {
            byte[] key = new byte[profile.KeySize];
            byte[] identity = new byte[FixedNScalar16Layout.IdentitySize];
            shelf.CopyKeyAt(i, key);
            shelf.CopyIdentityAt(i, identity);
            tuples[i] = new FixedNScalar16Tuple(key, identity);
        }

        return tuples;
    }

    private FixedNVarIdentityTuple[] ReadFixedNVarIdentityShelfTuples(
        long shelfOffset,
        FixedNVarIdentityProfile profile)
    {
        byte[] shelfBytes = ReadFixedNVarIdentityShelfBytes(shelfOffset, profile);
        FixedNVarIdentityReadOnly shelf = new(shelfBytes, profile);
        if (!shelf.IsValid)
            throw new InvalidDataException($"The FV tuple-stream shelf at offset {shelfOffset:n0} is invalid.");

        FixedNVarIdentityTuple[] tuples = new FixedNVarIdentityTuple[shelf.ItemCount];
        for (int i = 0; i < shelf.ItemCount; i++)
        {
            tuples[i] = new FixedNVarIdentityTuple(
                shelf.ReadKeyAt(i).ToArray(),
                shelf.ReadIdentityAt(i).ToArray());
        }

        return tuples;
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
