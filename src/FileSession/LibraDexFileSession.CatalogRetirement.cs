namespace LibraDex;

internal sealed partial class LibraDexFileSession
{
    /// <summary>
    /// Collects every exact current-generation topology offset that can safely be retired with one catalog slot.<br/>
    /// The active root is always included because its directory slot is its sole authoritative owner; richer shape walkers add routed shelves, subordinate routers, terminal identity storage, and null/empty key-state roots.<br/>
    /// A malformed, legacy, or unsupported topology intentionally degrades to root-only retirement so index deletion remains available while uncertain descendants remain for closed-catalog compaction.<br/>
    /// </summary>
    /// <param name="slot">The still-active physical index slot being deactivated.<br/></param>
    /// <param name="offsets">The de-duplicated retirement plan shared by the complete logical index or index set.<br/></param>
    /// <param name="inferredVarKeyScalar8MaxKeyLength">The owning string index's physical key cap for a metadata-free projection companion, or zero for an independently described slot.<br/></param>
    private void CollectAllocatorRetirementOffsets(
        IndexDirectorySlotSnapshot slot,
        HashSet<long> offsets,
        int inferredVarKeyScalar8MaxKeyLength)
    {
        if (slot.RootRouterOffset > 0)
            offsets.Add(slot.RootRouterOffset);
        if (inferredVarKeyScalar8MaxKeyLength > 0)
        {
            try
            {
                long[] companionOffsets = AssessVarKeyScalar8Topology(
                    slot.SlotIndex,
                    inferredVarKeyScalar8MaxKeyLength,
                    nullKeyRouteOffset: 0,
                    emptyKeyRouteOffset: 0).ClaimedOffsets;
                for (int offsetIndex = 0; offsetIndex < companionOffsets.Length; offsetIndex++)
                {
                    if (companionOffsets[offsetIndex] > 0)
                        offsets.Add(companionOffsets[offsetIndex]);
                }
            }
            catch (InvalidDataException)
            {
                // A malformed or legacy companion remains root-only and unavailable for speculative reuse.
            }

            return;
        }

        if (!TryReadCatalogIndexMetadata(slot, out CatalogIndexMetadata metadata))
            return;

        try
        {
            long[] claimedOffsets = AssessCatalogSlotRetirementTopology(slot, metadata);
            for (int offsetIndex = 0; offsetIndex < claimedOffsets.Length; offsetIndex++)
            {
                if (claimedOffsets[offsetIndex] > 0)
                    offsets.Add(claimedOffsets[offsetIndex]);
            }
        }
        catch (InvalidDataException)
        {
            // Deactivation is still safe. Unproven descendants remain unavailable for reuse and can be recovered by compaction.
        }
        catch (NotSupportedException)
        {
            // Unknown physical families preserve the same conservative compaction fallback as legacy files.
        }
    }

    /// <summary>
    /// Registers metadata-free VS8 projection slots owned by one rich string index that is being retired in the same catalog operation.<br/>
    /// Projection slots deliberately avoid duplicate rich metadata; ownership fields on the logical source therefore supply their exact physical key cap for the topology walk.<br/>
    /// Only companion slots present in the caller's deactivation plan are registered, preventing an independently retained additive projection from being retired by association alone.<br/>
    /// </summary>
    /// <param name="metadata">The logical owner's persisted string metadata.<br/></param>
    /// <param name="deactivate">The complete fixed-slot deactivation bitmap.<br/></param>
    /// <param name="companions">Destination map from physical companion slot to inferred VS8 maximum key length.<br/></param>
    private static void AddOwnedVarKeyScalar8CompanionRetirementShapes(
        CatalogIndexMetadata metadata,
        bool[] deactivate,
        Dictionary<int, int> companions)
    {
        if (metadata.KeyFamily != CatalogIndexKeyFamily.String || metadata.VarKeyMaxKeyLength <= 0)
            return;

        AddCompanion(metadata.ExactReversedProjectionSlotIndex);
        AddCompanion(metadata.FoldedProjectionSlotIndex);
        AddCompanion(metadata.SortKeyProjectionSlotIndex);
        AddCompanion(metadata.FoldedReversedProjectionSlotIndex);
        AddCompanion(metadata.NormalizedProjectionSlotIndex);
        AddCompanion(metadata.NormalizedReversedProjectionSlotIndex);
        IReadOnlyList<LibraDexStringSortKeyProjectionMetadata> profiles = metadata.SortKeyProfiles ?? Array.Empty<LibraDexStringSortKeyProjectionMetadata>();
        for (int profileIndex = 0; profileIndex < profiles.Count; profileIndex++)
            AddCompanion(profiles[profileIndex].SlotIndex);

        void AddCompanion(int slotIndex)
        {
            if ((uint)slotIndex < (uint)deactivate.Length && deactivate[slotIndex])
                companions[slotIndex] = metadata.VarKeyMaxKeyLength;
        }
    }

    /// <summary>
    /// Selects the exact topology walker from persisted physical metadata and returns its claimed extent offsets.<br/>
    /// Fixed generic widths include byte-array lanes recorded in metadata, while explicit variable-key and variable-identity projection contracts take precedence over numerically similar byte caps.<br/>
    /// </summary>
    /// <param name="slot">The active physical slot being assessed.<br/></param>
    /// <param name="metadata">The validated rich catalog metadata for the slot.<br/></param>
    /// <returns>Every distinct current-generation topology offset, or an empty array when the shape has no safe walker.<br/></returns>
    private long[] AssessCatalogSlotRetirementTopology(IndexDirectorySlotSnapshot slot, CatalogIndexMetadata metadata)
    {
        if (LibraDexMaintenanceAssessmentBuilder.IsFixedNScalar8Metadata(metadata))
            return AssessFixedNScalar8Topology(slot.SlotIndex, metadata.VarKeyMaxKeyLength).ClaimedOffsets;
        if (LibraDexMaintenanceAssessmentBuilder.IsFixedNScalar16Metadata(metadata))
            return AssessFixedNScalar16Topology(slot.SlotIndex, metadata.VarKeyMaxKeyLength).ClaimedOffsets;
        if (LibraDexMaintenanceAssessmentBuilder.IsFixedNVarIdentityMetadata(metadata))
        {
            return AssessFixedNVarIdentityTopology(
                slot.SlotIndex,
                metadata.VarKeyMaxKeyLength,
                metadata.VarIdentityMaxLength).ClaimedOffsets;
        }

        Type? keyType = Type.GetType(metadata.KeyTypeName, throwOnError: false);
        Type? identityType = Type.GetType(metadata.IdentityTypeName, throwOnError: false);
        int keyWidth = ResolveRetirementScalarWidth(keyType, metadata.VarKeyMaxKeyLength);
        int identityWidth = ResolveRetirementScalarWidth(identityType, metadata.VarIdentityMaxLength);

        if (IsVariableKeyScalarMetadata(metadata))
        {
            return identityWidth switch
            {
                8 => AssessVarKeyScalar8Topology(
                    slot.SlotIndex,
                    metadata.VarKeyMaxKeyLength,
                    metadata.NullKeyRouteOffset,
                    metadata.EmptyKeyRouteOffset).ClaimedOffsets,
                16 => AssessVarKeyScalar16Topology(
                    slot.SlotIndex,
                    metadata.VarKeyMaxKeyLength,
                    metadata.NullKeyRouteOffset,
                    metadata.EmptyKeyRouteOffset).ClaimedOffsets,
                _ => []
            };
        }

        if (metadata.IdentityFamily == CatalogIndexIdentityFamily.Blob &&
            metadata.VarIdentityMaxLength > 0)
        {
            if (metadata.KeyFamily == CatalogIndexKeyFamily.Blob && metadata.VarKeyMaxKeyLength > 0)
            {
                return AssessVarKeyVarIdentityTopology(
                    slot.SlotIndex,
                    metadata.VarKeyMaxKeyLength,
                    metadata.VarIdentityMaxLength).ClaimedOffsets;
            }

            return keyWidth switch
            {
                8 => AssessScalar8VarIdentityTopology(slot.SlotIndex, metadata.VarIdentityMaxLength).ClaimedOffsets,
                16 => AssessScalar16VarIdentityTopology(slot.SlotIndex, metadata.VarIdentityMaxLength).ClaimedOffsets,
                _ => []
            };
        }

        return (keyWidth, identityWidth) switch
        {
            (8, 8) => AssessScalar8Scalar8Topology(slot.SlotIndex).ClaimedOffsets,
            (16, 8) => AssessScalar16Scalar8Topology(slot.SlotIndex).ClaimedOffsets,
            (8, 16) => AssessScalar8Scalar16Topology(slot.SlotIndex).ClaimedOffsets,
            (16, 16) => AssessScalar16Scalar16Topology(slot.SlotIndex).ClaimedOffsets,
            (32, 8) => AssessFixed32Scalar8Topology(slot.SlotIndex).ClaimedOffsets,
            (32, 16) => AssessFixed32Scalar16Topology(slot.SlotIndex).ClaimedOffsets,
            _ => []
        };
    }

    /// <summary>
    /// Determines whether metadata selects a routed variable-key shelf rather than a fixed generic scalar shelf.<br/>
    /// String, explicit variable-blob, and variable-width BigInteger projections are durable discriminators; legacy string metadata remains variable by family contract.<br/>
    /// </summary>
    /// <param name="metadata">The persisted physical metadata to classify.<br/></param>
    /// <returns><see langword="true"/> only for a variable-key/scalar-identity physical family.<br/></returns>
    private static bool IsVariableKeyScalarMetadata(CatalogIndexMetadata metadata)
    {
        if (metadata.IdentityFamily != CatalogIndexIdentityFamily.Scalar || metadata.VarKeyMaxKeyLength <= 0)
            return false;
        if (metadata.KeyFamily == CatalogIndexKeyFamily.String)
            return true;

        for (int projectionIndex = 0; projectionIndex < metadata.Projections.Count; projectionIndex++)
        {
            LibraDexIndexProjectionKind kind = metadata.Projections[projectionIndex].Kind;
            if (kind is LibraDexIndexProjectionKind.VariableBlobExact or LibraDexIndexProjectionKind.BigIntVarLen)
                return true;
        }

        return false;
    }

    /// <summary>
    /// Resolves one persisted CLR lane to its exact physical fixed width for topology selection.<br/>
    /// Byte arrays carry their fixed width in catalog metadata; ordinary scalar CLR types reuse the same eight- and sixteen-byte codec contracts as generic index creation.<br/>
    /// </summary>
    /// <param name="type">The resolved persisted CLR type, or null when unavailable.<br/></param>
    /// <param name="persistedByteWidth">The metadata width used only for byte-array lanes.<br/></param>
    /// <returns>Eight, sixteen, or thirty-two for supported fixed lanes; otherwise zero.<br/></returns>
    private static int ResolveRetirementScalarWidth(Type? type, int persistedByteWidth)
    {
        if (type == typeof(byte[]))
            return persistedByteWidth is 8 or 16 or 32 ? persistedByteWidth : 0;
        if (type is not null && LibraDexMaintenanceAssessmentBuilder.IsScalar8Type(type))
            return 8;
        if (type is not null && LibraDexMaintenanceAssessmentBuilder.IsScalar16Type(type))
            return 16;
        return 0;
    }
}
