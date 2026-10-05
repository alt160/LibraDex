using LibraDex.Layouts;
using LibraDex.Views;

namespace LibraDex;

internal sealed partial class LibraDexFileSession
{
    /// <summary>
    /// Rebuilds populated `SV16` root-prefix subtrees from authoritative encoded key halves and raw identities through the shape-native routed writer.<br/>
    /// Prefix-local detached construction keeps publication atomic while retaining exact byte-ordinal key and identity ordering.<br/>
    /// </summary>
    /// <param name="rootRouterOffset">The physical index root router offset.<br/></param>
    /// <param name="maxIdentityLength">The maximum variable identity length for the index.<br/></param>
    /// <param name="maxWorkItems">The maximum authoritative tuples to consume, or null for the complete tuple stream.<br/></param>
    /// <param name="descending">Whether the physical profile stores tuples in descending natural order.<br/></param>
    /// <returns>The exact tuple work count, published subtree count, and any reason the tuple stream was not completed.<br/></returns>
    internal LibraDexMaintenanceWalkResult OptimizeScalar16VarIdentityTopology(
        long rootRouterOffset,
        int maxIdentityLength,
        int? maxWorkItems,
        bool descending = false)
    {
        if (maxWorkItems is <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxWorkItems), maxWorkItems, "The optimizer work limit must be positive when supplied.");

        using DataKernel.CoherentReadLease maintenanceRead = EnterCoherentUpgradeableRead();
        using Scalar16VarIdentityRangeReader reader = OpenScalar16VarIdentityRangeReader(
            rootRouterOffset,
            maxIdentityLength,
            ulong.MinValue,
            ulong.MinValue,
            ulong.MaxValue,
            ulong.MaxValue,
            descending);
        maintenanceRead.Pause();
        List<ulong> keyHighs = new();
        List<ulong> keyLows = new();
        List<byte[]> identities = new();
        int considered = 0;
        int changed = 0;
        byte activePrefix = 0;
        bool hasPrefix = false;
        while (true)
        {
            if (maxWorkItems is int limit && considered >= limit)
            {
                return new LibraDexMaintenanceWalkResult(
                    considered,
                    changed,
                    LibraDexMaintenanceIncompleteReason.WorkLimit);
            }
            bool hasNext;
            ulong keyHigh = 0;
            ulong keyLow = 0;
            byte[] identity = [];
            using (maintenanceRead.Use())
            {
                hasNext = reader.MoveNext();
                if (hasNext)
                {
                    keyHigh = reader.CurrentEncodedKeyHigh;
                    keyLow = reader.CurrentEncodedKeyLow;
                    identity = reader.CurrentIdentity.ToArray();
                }
            }
            if (!hasNext)
                break;

            byte prefix = GetScalar16VarIdentityPrefix(keyHigh, keyLow, 0);
            if (hasPrefix && prefix != activePrefix)
            {
                changed += Publish(activePrefix, keyHighs, keyLows, identities) ? 1 : 0;
                keyHighs.Clear();
                keyLows.Clear();
                identities.Clear();
            }

            considered++;
            activePrefix = prefix;
            hasPrefix = true;
            keyHighs.Add(keyHigh);
            keyLows.Add(keyLow);
            identities.Add(identity);
        }

        if (hasPrefix)
            changed += Publish(activePrefix, keyHighs, keyLows, identities) ? 1 : 0;

        return new LibraDexMaintenanceWalkResult(
            considered,
            changed,
            LibraDexMaintenanceIncompleteReason.None);

        bool Publish(
            byte rootPrefix,
            List<ulong> prefixKeyHighs,
            List<ulong> prefixKeyLows,
            List<byte[]> prefixIdentities)
        {
            long currentTarget = FindRouterTarget(rootRouterOffset, rootPrefix);
            using LibraDexFileSessionDurabilityBatch batch = BeginDurabilityBatch();
            long detachedRootOffset = CreateDetachedMaintenanceRootRouter();
            _ = CreateScalar16VarIdentityShelfAndLinkRootRoute(
                detachedRootOffset,
                rootPrefix,
                Scalar16VarIdentityProfile.Create(
                    Scalar16VarIdentityProfile.Default8KiB.ShelfExtentSize,
                    maxIdentityLength,
                    descending));
            for (int i = 0; i < prefixKeyHighs.Count; i++)
            {
                Scalar16VarIdentityRoutedInsertResult inserted = InsertWalkedRoutedScalar16VarIdentity(
                    detachedRootOffset,
                    maxIdentityLength,
                    prefixKeyHighs[i],
                    prefixKeyLows[i],
                    prefixIdentities[i],
                    allowDuplicateKeys: true,
                    maxRouterHops: DefaultScalar16VarIdentityMaxRouterHops,
                    descending: descending);
                if (inserted.InsertResult != Scalar16VarIdentityInsertResult.Inserted)
                {
                    throw new InvalidDataException(
                        $"The SV16 optimizer could not rebuild tuple {i:n0}; insert result was {inserted.InsertResult}.");
                }
            }

            long replacementTarget = FindRouterTarget(detachedRootOffset, rootPrefix);
            if (replacementTarget <= 0)
                throw new InvalidDataException("The SV16 optimizer detached rebuild did not publish its expected root-prefix target.");
            bool published = TryUpdateRouterRouteTargetIfCurrent(
                rootRouterOffset,
                rootPrefix,
                currentTarget,
                replacementTarget,
                out _);
            if (published)
                _ = batch.Commit();
            return published;
        }
    }

    /// <summary>
    /// Rebuilds populated `SV8` root-prefix subtrees from authoritative encoded keys and raw identities through the shape-native routed writer.<br/>
    /// Each completed prefix is built behind a detached root and independently version-checked, allowing bounded calls to publish earlier complete prefixes without exposing a partial later prefix.<br/>
    /// </summary>
    /// <param name="rootRouterOffset">The physical index root router offset.<br/></param>
    /// <param name="maxIdentityLength">The maximum variable identity length for the index.<br/></param>
    /// <param name="maxWorkItems">The maximum authoritative tuples to consume, or null for the complete tuple stream.<br/></param>
    /// <param name="descending">Whether the physical profile stores tuples in descending natural order.<br/></param>
    /// <returns>The exact tuple work count, published subtree count, and any reason the tuple stream was not completed.<br/></returns>
    internal LibraDexMaintenanceWalkResult OptimizeScalar8VarIdentityTopology(
        long rootRouterOffset,
        int maxIdentityLength,
        int? maxWorkItems,
        bool descending = false)
    {
        if (maxWorkItems is <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxWorkItems), maxWorkItems, "The optimizer work limit must be positive when supplied.");

        using DataKernel.CoherentReadLease maintenanceRead = EnterCoherentUpgradeableRead();
        using Scalar8VarIdentityRangeReader reader = OpenScalar8VarIdentityRangeReader(
            rootRouterOffset,
            maxIdentityLength,
            ulong.MinValue,
            ulong.MaxValue,
            descending ? QueryDirection.Descending : QueryDirection.Ascending,
            descending);
        maintenanceRead.Pause();
        List<ulong> keys = new();
        List<byte[]> identities = new();
        int considered = 0;
        int changed = 0;
        byte activePrefix = 0;
        bool hasPrefix = false;
        while (true)
        {
            if (maxWorkItems is int limit && considered >= limit)
            {
                return new LibraDexMaintenanceWalkResult(
                    considered,
                    changed,
                    LibraDexMaintenanceIncompleteReason.WorkLimit);
            }
            bool hasNext;
            ulong key = 0;
            byte[] identity = [];
            using (maintenanceRead.Use())
            {
                hasNext = reader.MoveNext();
                if (hasNext)
                {
                    key = reader.CurrentEncodedKey;
                    identity = reader.CurrentIdentity.ToArray();
                }
            }
            if (!hasNext)
                break;

            byte prefix = GetScalar8VarIdentityPrefix(key, 0);
            if (hasPrefix && prefix != activePrefix)
            {
                changed += Publish(activePrefix, keys, identities) ? 1 : 0;
                keys.Clear();
                identities.Clear();
            }

            considered++;
            activePrefix = prefix;
            hasPrefix = true;
            keys.Add(key);
            identities.Add(identity);
        }

        if (hasPrefix)
            changed += Publish(activePrefix, keys, identities) ? 1 : 0;

        return new LibraDexMaintenanceWalkResult(
            considered,
            changed,
            LibraDexMaintenanceIncompleteReason.None);

        bool Publish(byte rootPrefix, List<ulong> prefixKeys, List<byte[]> prefixIdentities)
        {
            long currentTarget = FindRouterTarget(rootRouterOffset, rootPrefix);
            using LibraDexFileSessionDurabilityBatch batch = BeginDurabilityBatch();
            long detachedRootOffset = CreateDetachedMaintenanceRootRouter();
            _ = CreateScalar8VarIdentityShelfAndLinkRootRoute(
                detachedRootOffset,
                rootPrefix,
                Scalar8VarIdentityProfile.Create(
                    Scalar8VarIdentityProfile.Default8KiB.ShelfExtentSize,
                    maxIdentityLength,
                    descending));
            for (int i = 0; i < prefixKeys.Count; i++)
            {
                Scalar8VarIdentityRoutedInsertResult inserted = InsertWalkedRoutedScalar8VarIdentity(
                    detachedRootOffset,
                    maxIdentityLength,
                    prefixKeys[i],
                    prefixIdentities[i],
                    allowDuplicateKeys: true,
                    maxRouterHops: DefaultScalar8VarIdentityMaxRouterHops);
                if (inserted.InsertResult != Scalar8VarIdentityInsertResult.Inserted)
                {
                    throw new InvalidDataException(
                        $"The SV8 optimizer could not rebuild tuple {i:n0}; insert result was {inserted.InsertResult}.");
                }
            }

            long replacementTarget = FindRouterTarget(detachedRootOffset, rootPrefix);
            if (replacementTarget <= 0)
                throw new InvalidDataException("The SV8 optimizer detached rebuild did not publish its expected root-prefix target.");
            bool published = TryUpdateRouterRouteTargetIfCurrent(
                rootRouterOffset,
                rootPrefix,
                currentTarget,
                replacementTarget,
                out _);
            if (published)
                _ = batch.Commit();
            return published;
        }
    }

    /// <summary>
    /// Rebuilds every populated value-key `VV` root-prefix subtree through the shape-native routed writer and publishes each detached replacement through a version-checked durability batch.<br/>
    /// The detached root is construction-only; only its completed child target becomes reachable from the authoritative root, and closed-file compaction later removes the unused construction root and obsolete source topology.<br/>
    /// </summary>
    /// <param name="rootRouterOffset">The physical index root router offset.<br/></param>
    /// <param name="maxKeyLength">The maximum encoded key length for the index.<br/></param>
    /// <param name="maxIdentityLength">The maximum variable identity length for the index.<br/></param>
    /// <param name="maxWorkItems">The maximum authoritative tuples to consume, or null for the complete tuple stream.<br/></param>
    /// <param name="descending">Whether the physical profile stores tuples in descending natural order.<br/></param>
    /// <returns>The exact tuple work count, published subtree count, and any reason the tuple stream was not completed.<br/></returns>
    internal LibraDexMaintenanceWalkResult OptimizeVarKeyVarIdentityTopology(
        long rootRouterOffset,
        int maxKeyLength,
        int maxIdentityLength,
        int? maxWorkItems,
        bool descending = false)
    {
        if (maxWorkItems is <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxWorkItems), maxWorkItems, "The optimizer work limit must be positive when supplied.");

        using DataKernel.CoherentReadLease maintenanceRead = EnterCoherentUpgradeableRead();
        byte[] lower = [LibraDexVarLenKeyCodec.ValueMarker];
        byte[] upper = GC.AllocateUninitializedArray<byte>(maxKeyLength);
        upper[0] = LibraDexVarLenKeyCodec.ValueMarker;
        upper.AsSpan(1).Fill(byte.MaxValue);
        using VarKeyVarIdentityRangeReader reader = OpenVarKeyVarIdentityRangeReader(
            rootRouterOffset,
            maxKeyLength,
            maxIdentityLength,
            lower,
            upper,
            decodeLogicalKeys: false,
            descending: descending);
        maintenanceRead.Pause();
        List<byte[]> keys = new();
        List<byte[]> identities = new();
        int considered = 0;
        int changed = 0;
        byte activePrefix = 0;
        bool hasPrefix = false;
        while (true)
        {
            if (maxWorkItems is int limit && considered >= limit)
            {
                return new LibraDexMaintenanceWalkResult(
                    considered,
                    changed,
                    LibraDexMaintenanceIncompleteReason.WorkLimit);
            }
            bool hasNext;
            byte[] key = [];
            byte[] identity = [];
            using (maintenanceRead.Use())
            {
                hasNext = reader.MoveNext();
                if (hasNext)
                {
                    key = reader.CurrentKey.ToArray();
                    identity = reader.CurrentIdentity.ToArray();
                }
            }
            if (!hasNext)
                break;

            byte prefix = key[0];
            if (hasPrefix && prefix != activePrefix)
            {
                changed += Publish(activePrefix, keys, identities) ? 1 : 0;
                keys.Clear();
                identities.Clear();
            }

            considered++;
            activePrefix = prefix;
            hasPrefix = true;
            keys.Add(key);
            identities.Add(identity);
        }

        if (hasPrefix)
            changed += Publish(activePrefix, keys, identities) ? 1 : 0;

        return new LibraDexMaintenanceWalkResult(
            considered,
            changed,
            LibraDexMaintenanceIncompleteReason.None);

        bool Publish(byte rootPrefix, List<byte[]> prefixKeys, List<byte[]> prefixIdentities)
        {
            long currentTarget = FindRouterTarget(rootRouterOffset, rootPrefix);
            using LibraDexFileSessionDurabilityBatch batch = BeginDurabilityBatch();
            long detachedRootOffset = CreateDetachedMaintenanceRootRouter();
            for (int i = 0; i < prefixKeys.Count; i++)
            {
                VarKeyVarIdentityRoutedInsertResult inserted = InsertWalkedRoutedVarKeyVarIdentity(
                    detachedRootOffset,
                    maxKeyLength,
                    maxIdentityLength,
                    prefixKeys[i],
                    prefixIdentities[i],
                    allowDuplicateKeys: true,
                    maxRouterHops: DefaultVarKeyVarIdentityMaxRouterHops,
                    descending: descending);
                if (inserted.InsertResult != VarKeyVarIdentityInsertResult.Inserted)
                {
                    throw new InvalidDataException(
                        $"The VV optimizer could not rebuild tuple {i:n0}; insert result was {inserted.InsertResult}.");
                }
            }

            long replacementTarget = FindRouterTarget(detachedRootOffset, rootPrefix);
            if (replacementTarget <= 0)
                throw new InvalidDataException("The VV optimizer detached rebuild did not publish its expected root-prefix target.");
            bool published = TryUpdateRouterRouteTargetIfCurrent(
                rootRouterOffset,
                rootPrefix,
                currentTarget,
                replacementTarget,
                out _);
            if (published)
                _ = batch.Commit();
            return published;
        }
    }

    /// <summary>
    /// Stages one direct-index root router that is intentionally absent from the index directory and exists only while a maintenance batch constructs a replacement subtree.<br/>
    /// Callers must publish a completed child target through a version-checked authoritative route update before committing the batch.<br/>
    /// </summary>
    /// <returns>The staged detached root-router offset.<br/></returns>
    private long CreateDetachedMaintenanceRootRouter()
    {
        if (!IsDurabilityBatchActive)
            throw new InvalidOperationException("A detached maintenance root requires an active durability batch.");

        RawDataReservation router = kernel.Reserve(RouterLayout.Size);
        RouterWriter writer = new(router.Span);
        writer.InitializeRoot(allocationClassId: 0);
        RouterReader reader = new(router.Span);
        if (!reader.IsValid || !reader.HasDirectIndex)
            throw new InvalidDataException("The detached maintenance root router did not validate.");
        return router.Extent.Offset;
    }
}
