namespace LibraDex;

public sealed partial class LibraDexRoutedCompositeIndex
{
    private const int CompositeBulkPublicationBound = 4096;

    /// <summary>
    /// Inserts source-aligned composite keys and identities through one routed bulk operation.<br/>
    /// Changed nodes are collected across at most 4,096 successful inserts, written once each from terminal tiers toward the root, and then published by one catalog-root update.<br/>
    /// A snapshot-backed tree is converted to node pages on its first successful bulk publication; later groups reuse untouched durable child pages.<br/>
    /// The returned count excludes exact tuples already present. An I/O failure can leave an accepted prefix, so a caller coordinating this index with another authoritative store must perform its normal convergence repair.<br/>
    /// </summary>
    /// <typeparam name="TIdentity">The configured composite identity type.<br/></typeparam>
    /// <param name="keys">Composite keys in insertion order.<br/></param>
    /// <param name="identities">Identities aligned one-to-one with <paramref name="keys"/>.<br/></param>
    /// <returns>The number of newly inserted key and identity tuples.<br/></returns>
    public long InsertBatch<TIdentity>(
        IReadOnlyList<LibraDexCompositeKey> keys,
        IReadOnlyList<TIdentity> identities)
        where TIdentity : notnull
    {
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentNullException.ThrowIfNull(identities);
        if (keys.Count != identities.Count)
            throw new ArgumentException("Composite keys and identities must have equal counts.", nameof(identities));

        lock (this)
        {
            ThrowIfSessionDurabilityBatchActiveForMutation();
            for (int index = 0; index < keys.Count; index++)
            {
                ArgumentNullException.ThrowIfNull(keys[index]);
                ValidateIdentity(identities[index]);
                keys[index].ValidateAgainst(shape);
            }

            var path = new CompositeNode[shape.CompositeParts.Count + 1];
            var dirtyNodes = new HashSet<CompositeNode>();
            long insertedCount = 0;
            int uncommittedCount = 0;
            for (int index = 0; index < keys.Count; index++)
            {
                if (!AddEntry(keys[index], identities[index], persist: false, path, dirtyNodes).Inserted)
                    continue;

                insertedCount++;
                uncommittedCount++;
                if (uncommittedCount == CompositeBulkPublicationBound)
                {
                    PublishCompositeBulkGroup(dirtyNodes);
                    dirtyNodes.Clear();
                    uncommittedCount = 0;
                }
            }

            if (uncommittedCount != 0)
                PublishCompositeBulkGroup(dirtyNodes);
            return insertedCount;
        }
    }

    /// <summary>
    /// Appends the changed routed nodes and publishes their new root exactly once for one bounded group.<br/>
    /// An existing snapshot root has no reusable child-page offsets, so its first group materializes the full tier tree as pages.<br/>
    /// </summary>
    /// <param name="dirtyNodes">All nodes on successfully inserted routes in this group.<br/></param>
    private void PublishCompositeBulkGroup(HashSet<CompositeNode> dirtyNodes)
    {
        if (session is null || slotIndex is not int durableSlotIndex || dirtyNodes.Count == 0)
            return;

        PersistCompositeBulkNode(root, 0, dirtyNodes, forceAll: root.DurableOffset <= 0);
        session.UpdateCompositeNodeRoot(durableSlotIndex, root.DurableOffset, itemCount);
    }

    /// <summary>
    /// Persists dirty descendants before their parent so every encoded child reference points at its current durable page.<br/>
    /// Clean subtrees keep their prior offsets except during the one-time conversion from a snapshot root.<br/>
    /// </summary>
    /// <param name="node">Routed node being considered for publication.<br/></param>
    /// <param name="tier">Zero-based tier of <paramref name="node"/>.<br/></param>
    /// <param name="dirtyNodes">Nodes affected by the current bounded insertion group.<br/></param>
    /// <param name="forceAll">Whether every descendant needs its first durable node page.<br/></param>
    private void PersistCompositeBulkNode(
        CompositeNode node,
        int tier,
        HashSet<CompositeNode> dirtyNodes,
        bool forceAll)
    {
        if (!forceAll && !dirtyNodes.Contains(node))
            return;

        EnsureNodeLoaded(node, tier);
        for (int index = 0; index < node.Children.Count; index++)
            PersistCompositeBulkNode(node.Children[index].Node, tier + 1, dirtyNodes, forceAll);

        node.DurableOffset = session!.AppendCompositeNodePage(EncodeNodePage(node, tier));
    }
}
