using System.Globalization;

namespace LibraDex;

/// <summary>
/// Controls closed-file catalog compaction.<br/>
/// The default keeps no rollback file after the replacement catalog has reopened successfully; failures retain or restore the original automatically.<br/>
/// </summary>
public sealed class LibraDexCompactionOptions
{
    /// <summary>
    /// Gets or initializes the catalog policy used while opening the source and creating, validating, and reopening the compacted catalog.<br/>
    /// Owners that constrain identity types, such as Abraxas, should pass the same policy used for their normal catalog lifetime.<br/>
    /// </summary>
    public CatalogOptions? CatalogOptions { get; init; }

    /// <summary>
    /// Gets or initializes whether the replaced source file should remain at <see cref="BackupPath"/> after successful replacement validation.<br/>
    /// A failed replacement or replacement validation retains or restores the rollback file regardless of this setting.<br/>
    /// </summary>
    public bool KeepBackup { get; init; }

    /// <summary>
    /// Gets or initializes the optional rollback-file path.<br/>
    /// Null creates a unique sibling path on the same volume so replacement remains an atomic filesystem operation where the platform supports it.<br/>
    /// </summary>
    public string? BackupPath { get; init; }
}

/// <summary>
/// Reports one completed closed-file catalog compaction.<br/>
/// Byte counts come from the source immediately before rebuild and the validated replacement after swap, so <see cref="ReclaimedBytes"/> is exact rather than estimated.<br/>
/// </summary>
/// <param name="Path">The full replacement catalog path.<br/></param>
/// <param name="BackupPath">The retained rollback path, or null when successful validation allowed it to be removed.<br/></param>
/// <param name="SourceBytes">The original catalog file length.<br/></param>
/// <param name="CompactedBytes">The validated replacement catalog file length.<br/></param>
/// <param name="ReclaimedBytes">The signed number of bytes removed; a negative value means the rebuilt current-format catalog became larger.<br/></param>
/// <param name="LogicalIndexCount">The number of logical source indexes rebuilt, excluding owned physical projection companions.<br/></param>
/// <param name="TupleCount">The number of authoritative source tuples copied.<br/></param>
public readonly record struct LibraDexCompactionResult(
    string Path,
    string? BackupPath,
    long SourceBytes,
    long CompactedBytes,
    long ReclaimedBytes,
    int LogicalIndexCount,
    long TupleCount)
{
    /// <summary>
    /// Gets the number of logical source indexes whose legacy traversal was not globally ordered and therefore required order-independent per-tuple recovery.<br/>
    /// A nonzero value means compaction repaired physical ordering while preserving every distinct source tuple through explicit membership validation.<br/>
    /// </summary>
    public int RecoveredUnorderedIndexCount { get; init; }
}

internal enum LibraDexCompactionFaultPoint
{
    None = 0,
    BeforeReplacement = 1,
    AfterReplacementBeforeValidation = 2
}

internal static class LibraDexCatalogCompactor
{
    private readonly record struct IndexProof(string Group, string Name, long TupleCount);

    /// <summary>
    /// Rebuilds and replaces one closed catalog through a same-volume shadow file.<br/>
    /// Source and shadow tuple streams are compared structurally before replacement, and the installed replacement is reopened before rollback removal.<br/>
    /// </summary>
    /// <param name="path">The closed source catalog path.<br/></param>
    /// <param name="options">Optional compaction policy.<br/></param>
    /// <param name="cancellationToken">Cancellation observed only while the original remains authoritative.<br/></param>
    /// <returns>The completed compaction result.<br/></returns>
    internal static LibraDexCompactionResult Compact(
        string path,
        LibraDexCompactionOptions? options,
        CancellationToken cancellationToken)
        => Compact(path, options, cancellationToken, LibraDexCompactionFaultPoint.None);

    /// <summary>
    /// Rebuilds and replaces one closed catalog with an internal deterministic fault point for rollback proofs.<br/>
    /// The fault hook is assembly-internal and therefore cannot weaken the public compaction contract or become application configuration.<br/>
    /// </summary>
    /// <param name="path">The closed source catalog path.<br/></param>
    /// <param name="options">Optional compaction policy.<br/></param>
    /// <param name="cancellationToken">Cancellation observed only while the original remains authoritative.<br/></param>
    /// <param name="faultPoint">The internal proof-only failure point.<br/></param>
    /// <returns>The completed compaction result when no fault is injected.<br/></returns>
    internal static LibraDexCompactionResult Compact(
        string path,
        LibraDexCompactionOptions? options,
        CancellationToken cancellationToken,
        LibraDexCompactionFaultPoint faultPoint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!Enum.IsDefined(faultPoint))
            throw new ArgumentOutOfRangeException(nameof(faultPoint), faultPoint, "The compaction fault point is not defined.");
        string sourcePath = System.IO.Path.GetFullPath(path);
        if (!File.Exists(sourcePath))
            throw new FileNotFoundException("The LibraDex catalog file to compact does not exist.", sourcePath);

        LibraDexCompactionOptions effective = options ?? new LibraDexCompactionOptions();
        CatalogOptions catalogOptions = effective.CatalogOptions ?? new CatalogOptions();
        string directory = System.IO.Path.GetDirectoryName(sourcePath)
            ?? throw new InvalidOperationException("The LibraDex catalog path has no containing directory.");
        string fileName = System.IO.Path.GetFileName(sourcePath);
        string token = Guid.NewGuid().ToString("N");
        string shadowPath = System.IO.Path.Combine(directory, $".{fileName}.compact-{token}.tmp");
        string backupPath = string.IsNullOrWhiteSpace(effective.BackupPath)
            ? System.IO.Path.Combine(directory, $".{fileName}.compact-{token}.rollback")
            : System.IO.Path.GetFullPath(effective.BackupPath);
        if (string.Equals(sourcePath, backupPath, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(shadowPath, backupPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The compaction backup path must differ from the source and temporary paths.", nameof(options));
        }

        long sourceBytes = new FileInfo(sourcePath).Length;
        List<IndexProof> proofs = new();
        long tupleCount = 0;
        int recoveredUnorderedIndexCount = 0;
        bool replaced = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            using (Catalog source = Catalog.Open(sourcePath, catalogOptions))
            using (Catalog shadow = Catalog.Create(shadowPath, catalogOptions))
            {
                CatalogIndexInfo[] infos = source.Indexes.List();
                HashSet<int> companionSlots = GetCompanionSlots(infos);
                for (int i = 0; i < infos.Length; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    CatalogIndexInfo info = infos[i];
                    if (companionSlots.Contains(info.SlotIndex))
                        continue;
                    if (!info.TryCreateShape(out LibraDexIndexShapeSpec? shape))
                    {
                        throw new NotSupportedException(
                            $"Catalog compaction cannot rebuild legacy index slot {info.SlotIndex} ('{info.Group}/{info.Name}') because it has no reconstructable logical shape metadata.");
                    }

                    IIndex sourceIndex = source.OpenIndex(info);
                    IIndex shadowIndex = CreateShadowIndex(shadow, info, shape);
                    CompactionCopyResult copy = CopyTuples(sourceIndex, shadowIndex, cancellationToken);
                    source.Session.AssertNoRetainedCoherentRead($"Compaction copy for source index '{info.Group}/{info.Name}'");
                    shadow.Session.AssertNoRetainedCoherentRead($"Compaction copy for shadow index '{info.Group}/{info.Name}'");
                    if (copy.RecoveredUnorderedSource)
                    {
                        CompareTupleMembership(sourceIndex, shadowIndex, info, copy.TupleCount, cancellationToken);
                        recoveredUnorderedIndexCount++;
                    }
                    else
                    {
                        CompareTuples(sourceIndex, shadowIndex, info, cancellationToken);
                    }
                    source.Session.AssertNoRetainedCoherentRead($"Compaction validation for source index '{info.Group}/{info.Name}'");
                    shadow.Session.AssertNoRetainedCoherentRead($"Compaction validation for shadow index '{info.Group}/{info.Name}'");
                    proofs.Add(new IndexProof(info.Group, info.Name, copy.TupleCount));
                    tupleCount = checked(tupleCount + copy.TupleCount);
                }

                shadow.Session.FlushFileToDisk();
            }

            cancellationToken.ThrowIfCancellationRequested();
            using (Catalog validatedShadow = Catalog.Open(shadowPath, catalogOptions))
                ValidateInstalledCatalog(validatedShadow, proofs);

            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(backupPath))
                throw new IOException($"The LibraDex compaction rollback path already exists: {backupPath}");

            InjectFault(LibraDexCompactionFaultPoint.BeforeReplacement);
            File.Replace(shadowPath, sourcePath, backupPath, ignoreMetadataErrors: true);
            replaced = true;

            InjectFault(LibraDexCompactionFaultPoint.AfterReplacementBeforeValidation);
            using (Catalog replacement = Catalog.Open(sourcePath, catalogOptions))
                ValidateInstalledCatalog(replacement, proofs);

            long compactedBytes = new FileInfo(sourcePath).Length;
            string? retainedBackup = effective.KeepBackup ? backupPath : null;
            if (!effective.KeepBackup)
                File.Delete(backupPath);
            return new LibraDexCompactionResult(
                sourcePath,
                retainedBackup,
                sourceBytes,
                compactedBytes,
                checked(sourceBytes - compactedBytes),
                proofs.Count,
                tupleCount)
            {
                RecoveredUnorderedIndexCount = recoveredUnorderedIndexCount
            };
        }
        catch
        {
            if (replaced && File.Exists(backupPath))
            {
                if (File.Exists(sourcePath))
                    File.Delete(sourcePath);
                File.Move(backupPath, sourcePath);
            }

            throw;
        }
        finally
        {
            if (File.Exists(shadowPath))
                File.Delete(shadowPath);
        }

        void InjectFault(LibraDexCompactionFaultPoint requested)
        {
            if (faultPoint == requested)
                throw new InvalidOperationException($"Injected LibraDex compaction failure at {requested}.");
        }
    }

    private static CompactionCopyResult CopyTuples(IIndex source, IIndex destination, CancellationToken cancellationToken)
    {
        if (source is not IIdentityPrimitiveTupleStreamer streamer)
            throw new NotSupportedException($"Index '{source.Group}/{source.Name}' does not expose the authoritative tuple stream required for compaction.");

        LibraDexIdentityPrimitiveRequest request = new(LibraDexCriteriaKind.All, Array.Empty<object?>());
        if (destination is ILibraDexNativeSortedBuild native)
        {
            try
            {
                if (native.TryBuildFromSortedObjects(streamer.IterateTuplePrimitive(request), cancellationToken, out long nativeCount))
                    return new CompactionCopyResult(nativeCount, RecoveredUnorderedSource: false);
            }
            catch (ArgumentException exception) when (
                string.Equals(exception.ParamName, "tuples", StringComparison.Ordinal) &&
                exception.Message.Contains("inverted", StringComparison.OrdinalIgnoreCase))
            {
                if (destination.Count() != 0)
                {
                    throw new InvalidDataException(
                        $"Compaction cannot recover unordered source index '{source.Group}/{source.Name}' because the rejected native build changed the destination's visible tuple count.",
                        exception);
                }

                if (native.TryBuildFromUnorderedObjects(
                    streamer.IterateTuplePrimitive(request),
                    cancellationToken,
                    out long recoveredNativeCount))
                {
                    return new CompactionCopyResult(recoveredNativeCount, RecoveredUnorderedSource: true);
                }

                return CopyUnorderedTuples(streamer, destination, request, cancellationToken);
            }
        }

        long copied = 0;
        foreach (LibraDexObjectTuple tuple in streamer.IterateTuplePrimitive(request))
        {
            cancellationToken.ThrowIfCancellationRequested();
            LibraDexGenericInsertResult result = destination.Insert(tuple.Key, tuple.Identity);
            if (!result.Inserted)
                throw new InvalidDataException($"Compaction could not insert tuple {copied:n0} into shadow index '{destination.Group}/{destination.Name}'.");
            copied++;
        }

        return new CompactionCopyResult(copied, RecoveredUnorderedSource: false);
    }

    /// <summary>
    /// Replays an unordered legacy source stream into the empty shadow index through its current tuple-mutation path.<br/>
    /// Every source tuple must insert exactly once; an exact duplicate or rejected tuple stops recovery rather than silently changing multiplicity.<br/>
    /// </summary>
    /// <param name="source">The authoritative legacy source tuple streamer.<br/></param>
    /// <param name="destination">The empty current-format shadow index.<br/></param>
    /// <param name="request">The all-tuples primitive request.<br/></param>
    /// <param name="cancellationToken">Cancellation observed while the original remains authoritative.<br/></param>
    /// <returns>The copied tuple count marked for order-independent validation.<br/></returns>
    private static CompactionCopyResult CopyUnorderedTuples(
        IIdentityPrimitiveTupleStreamer source,
        IIndex destination,
        LibraDexIdentityPrimitiveRequest request,
        CancellationToken cancellationToken)
    {
        long copied = 0;
        foreach (LibraDexObjectTuple tuple in source.IterateTuplePrimitive(request))
        {
            cancellationToken.ThrowIfCancellationRequested();
            LibraDexGenericInsertResult result = destination.Insert(tuple.Key, tuple.Identity);
            if (!result.Inserted)
            {
                throw new InvalidDataException(
                    $"Compaction recovery could not insert unordered tuple {copied:n0} into shadow index '{destination.Group}/{destination.Name}'.");
            }
            copied++;
        }

        return new CompactionCopyResult(copied, RecoveredUnorderedSource: true);
    }

    /// <summary>
    /// Validates an unordered legacy source against its repaired shadow without relying on the corrupt source traversal order.<br/>
    /// Successful one-time insertion establishes source distinctness; exact destination membership for every source tuple plus an equal destination stream count proves set parity.<br/>
    /// </summary>
    /// <param name="source">The authoritative unordered source index.<br/></param>
    /// <param name="shadow">The repaired current-format shadow index.<br/></param>
    /// <param name="info">Logical index metadata used in failures.<br/></param>
    /// <param name="expectedCount">The exact number of distinct source tuples inserted.<br/></param>
    /// <param name="cancellationToken">Cancellation observed while the original remains authoritative.<br/></param>
    private static void CompareTupleMembership(
        IIndex source,
        IIndex shadow,
        CatalogIndexInfo info,
        long expectedCount,
        CancellationToken cancellationToken)
    {
        if (source is not IIdentityPrimitiveTupleStreamer sourceStreamer ||
            shadow is not IIdentityPrimitiveTupleStreamer shadowStreamer ||
            shadow is not IIdentityExactTupleMutator exactShadow)
        {
            throw new NotSupportedException(
                $"Index '{info.Group}/{info.Name}' cannot provide order-independent compaction parity validation.");
        }

        LibraDexIdentityPrimitiveRequest request = new(LibraDexCriteriaKind.All, Array.Empty<object?>());
        long sourceCount = 0;
        foreach (LibraDexObjectTuple tuple in sourceStreamer.IterateTuplePrimitive(request))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!exactShadow.ContainsExactTuple(tuple.Key, tuple.Identity))
            {
                throw new InvalidDataException(
                    $"Compaction recovery membership parity failed for index '{info.Group}/{info.Name}' at unordered source ordinal {sourceCount:n0}.");
            }
            sourceCount++;
        }

        long shadowCount = 0;
        foreach (LibraDexObjectTuple _ in shadowStreamer.IterateTuplePrimitive(request))
        {
            cancellationToken.ThrowIfCancellationRequested();
            shadowCount++;
        }
        if (sourceCount != expectedCount || shadowCount != expectedCount)
        {
            throw new InvalidDataException(
                $"Compaction recovery count parity failed for index '{info.Group}/{info.Name}': expected={expectedCount:n0}, source={sourceCount:n0}, shadow={shadowCount:n0}.");
        }
    }

    private readonly record struct CompactionCopyResult(long TupleCount, bool RecoveredUnorderedSource);

    /// <summary>
    /// Creates one shadow logical index through the facade that owns its physical shape.<br/>
    /// String indexes must recreate their owned projection slots and persisted cultures/policy as a unit; ordinary scalar shapes continue through the generic shape factory.<br/>
    /// </summary>
    /// <param name="shadow">The destination shadow catalog.<br/></param>
    /// <param name="info">The source index metadata used for physical-facade details.<br/></param>
    /// <param name="shape">The reconstructable logical shape.<br/></param>
    /// <returns>The newly created shadow index handle.<br/></returns>
    private static IIndex CreateShadowIndex(
        Catalog shadow,
        CatalogIndexInfo info,
        LibraDexIndexShapeSpec shape)
    {
        if (info.KeyFamily == CatalogIndexKeyFamily.String)
        {
            LibraDexStringComparisonPolicy? policy = LibraDexStringComparisonPolicy.FromPersisted(
                info.StringComparisonPolicyKind,
                info.StringComparisonCulture,
                info.StringComparisonCompareOptions,
                info.StringComparisonCustomComparerTypeName);
            return shadow.Indexes[info.Group][info.Name].String.Create(
                info.StringKeys,
                info.Directions,
                info.SortOrder,
                info.FoldedCulture,
                sortKeyCulture: null,
                stringComparisonPolicy: policy,
                sortKeyProfiles: CreateSortKeyProfiles(info));
        }

        IndexOptions indexOptions = shape.ToIndexOptions();
        int slotIndex = shadow.Indexes.ResolveCreateSlot(null);
        if (info.KeyFamily == CatalogIndexKeyFamily.Blob &&
            info.IdentityFamily == CatalogIndexIdentityFamily.Scalar &&
            info.Projections.Count > 0 &&
            info.Projections[0].Kind == LibraDexIndexProjectionKind.VariableBlobExact)
        {
            return InvokeGenericCreate(
                shadow,
                nameof(Catalog.CreateVariableBlobScalar8Index),
                shape.IdentityType,
                info.Group,
                info.Name,
                slotIndex,
                checked(info.VarKeyMaxKeyLength - 1),
                indexOptions);
        }

        if (info.KeyFamily == CatalogIndexKeyFamily.BigInt &&
            info.IdentityFamily == CatalogIndexIdentityFamily.Scalar)
        {
            LibraDexBigIntKeyStorage storage = info.Projections[0].Kind == LibraDexIndexProjectionKind.BigIntVarLen
                ? LibraDexBigIntKeyStorage.VariableWidth
                : LibraDexBigIntKeyStorage.FixedWidth;
            int maxBytes = storage == LibraDexBigIntKeyStorage.VariableWidth
                ? info.VarKeyMaxKeyLength - 4
                : info.VarKeyMaxKeyLength - 3;
            return InvokeGenericCreate(
                shadow,
                nameof(Catalog.CreateBigIntScalar8Index),
                shape.IdentityType,
                info.Group,
                info.Name,
                slotIndex,
                maxBytes,
                storage,
                indexOptions);
        }

        if (info.KeyFamily == CatalogIndexKeyFamily.BigInt &&
            info.IdentityFamily == CatalogIndexIdentityFamily.Blob)
        {
            return shadow.CreateBigIntVarIdentityIndex(
                info.Group,
                info.Name,
                slotIndex,
                info.VarKeyMaxKeyLength - 3,
                info.VarIdentityMaxLength,
                indexOptions);
        }

        if (info.KeyFamily == CatalogIndexKeyFamily.Scalar &&
            info.IdentityFamily == CatalogIndexIdentityFamily.Blob &&
            shape.KeyType == typeof(ulong))
        {
            return shadow.CreateUInt64VarIdentityIndex(
                info.Group,
                info.Name,
                slotIndex,
                info.VarIdentityMaxLength,
                indexOptions);
        }

        if (shape.KeyType == typeof(byte[]) || shape.IdentityType == typeof(byte[]))
            return CreateFixedBlobShadowIndex(shadow, info, shape, slotIndex, indexOptions);

        return shadow.Indexes.Create(shape);
    }

    private static IIndex InvokeGenericCreate(
        Catalog catalog,
        string methodName,
        Type genericType,
        params object?[] arguments)
    {
        System.Reflection.MethodInfo method = typeof(Catalog).GetMethods(
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            .Single(candidate =>
                candidate.Name == methodName &&
                candidate.IsGenericMethodDefinition &&
                candidate.GetGenericArguments().Length == 1 &&
                candidate.GetParameters().Length == arguments.Length);
        try
        {
            object? created = method.MakeGenericMethod(genericType).Invoke(catalog, arguments);
            return created as IIndex
                ?? throw new InvalidOperationException($"Compaction creation method '{methodName}' did not return an index handle.");
        }
        catch (System.Reflection.TargetInvocationException ex) when (ex.InnerException is not null)
        {
            throw ex.InnerException;
        }
    }

    private static IIndex CreateFixedBlobShadowIndex(
        Catalog shadow,
        CatalogIndexInfo info,
        LibraDexIndexShapeSpec shape,
        int slotIndex,
        IndexOptions options)
    {
        LibraDexScalarWidth? keyWidth = shape.KeyType == typeof(byte[])
            ? ResolveFixedWidth(info.VarKeyMaxKeyLength, "key")
            : null;
        LibraDexScalarWidth? identityWidth = shape.IdentityType == typeof(byte[])
            ? ResolveFixedWidth(info.VarIdentityMaxLength, "identity")
            : null;
        System.Reflection.MethodInfo method = typeof(Catalog).GetMethod(
            "CreateGenericIndex",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?? throw new MissingMethodException(nameof(Catalog), "CreateGenericIndex");
        try
        {
            object? created = method.MakeGenericMethod(shape.KeyType, shape.IdentityType).Invoke(
                shadow,
                new object?[]
                {
                    info.Name,
                    slotIndex,
                    options,
                    keyWidth,
                    identityWidth,
                    info.KeyFamily,
                    info.IdentityFamily,
                    info.Group,
                    shape,
                    -1,
                    null,
                    IdentityLookupMode.Explicit
                });
            return created as IIndex
                ?? throw new InvalidOperationException("Compaction fixed-blob creation did not return an index handle.");
        }
        catch (System.Reflection.TargetInvocationException ex) when (ex.InnerException is not null)
        {
            throw ex.InnerException;
        }
    }

    private static LibraDexScalarWidth ResolveFixedWidth(int width, string side)
    {
        return width switch
        {
            8 => LibraDexScalarWidth.Bytes8,
            16 => LibraDexScalarWidth.Bytes16,
            32 => LibraDexScalarWidth.Bytes32,
            _ => throw new NotSupportedException($"Compaction cannot reconstruct a fixed blob {side} width of {width} bytes.")
        };
    }

    private static void CompareTuples(
        IIndex source,
        IIndex shadow,
        CatalogIndexInfo info,
        CancellationToken cancellationToken)
    {
        if (source is not IIdentityPrimitiveTupleStreamer sourceStreamer ||
            shadow is not IIdentityPrimitiveTupleStreamer shadowStreamer)
        {
            throw new NotSupportedException($"Index '{info.Group}/{info.Name}' cannot provide structural tuple parity validation.");
        }

        LibraDexIdentityPrimitiveRequest request = new(LibraDexCriteriaKind.All, Array.Empty<object?>());
        using IEnumerator<LibraDexObjectTuple> left = sourceStreamer.IterateTuplePrimitive(request).GetEnumerator();
        using IEnumerator<LibraDexObjectTuple> right = shadowStreamer.IterateTuplePrimitive(request).GetEnumerator();
        long ordinal = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            bool hasLeft = left.MoveNext();
            bool hasRight = right.MoveNext();
            if (hasLeft != hasRight)
                throw new InvalidDataException($"Compaction tuple count diverged for index '{info.Group}/{info.Name}' at ordinal {ordinal:n0}.");
            if (!hasLeft)
                return;
            if (!LibraDexObjectTuple.ValueEquals(left.Current.Key, right.Current.Key) ||
                !LibraDexObjectTuple.ValueEquals(left.Current.Identity, right.Current.Identity))
            {
                throw new InvalidDataException($"Compaction tuple parity failed for index '{info.Group}/{info.Name}' at ordinal {ordinal:n0}.");
            }

            ordinal++;
        }
    }

    private static void ValidateInstalledCatalog(Catalog catalog, IReadOnlyList<IndexProof> proofs)
    {
        for (int i = 0; i < proofs.Count; i++)
        {
            IndexProof proof = proofs[i];
            if (!catalog.Indexes.TryGetInfo(proof.Group, proof.Name, out CatalogIndexInfo info))
                throw new InvalidDataException($"Compacted catalog is missing logical index '{proof.Group}/{proof.Name}'.");
            IIndex index = catalog.OpenIndex(info);
            if (index is not IIdentityPrimitiveTupleStreamer streamer)
                throw new NotSupportedException($"Compacted index '{proof.Group}/{proof.Name}' cannot provide tuple-count validation.");
            LibraDexIdentityPrimitiveRequest request = new(LibraDexCriteriaKind.All, Array.Empty<object?>());
            long count = 0;
            foreach (LibraDexObjectTuple _ in streamer.IterateTuplePrimitive(request))
                count++;
            if (count != proof.TupleCount)
            {
                throw new InvalidDataException(
                    $"Compacted index '{proof.Group}/{proof.Name}' reports {count:n0} tuples; expected {proof.TupleCount:n0}.");
            }
        }
    }

    private static HashSet<int> GetCompanionSlots(CatalogIndexInfo[] infos)
    {
        HashSet<int> slots = new();
        for (int i = 0; i < infos.Length; i++)
        {
            AddCompanion(slots, infos[i].ExactReversedProjectionSlotIndex);
            AddCompanion(slots, infos[i].FoldedProjectionSlotIndex);
            AddCompanion(slots, infos[i].SortKeyProjectionSlotIndex);
            if (infos[i].SortKeyProfiles is { Count: > 1 } sortKeyProfiles)
            {
                for (int j = 1; j < sortKeyProfiles.Count; j++)
                    AddCompanion(slots, sortKeyProfiles[j].SlotIndex);
            }
            AddCompanion(slots, infos[i].FoldedReversedProjectionSlotIndex);
            AddCompanion(slots, infos[i].NormalizedProjectionSlotIndex);
            AddCompanion(slots, infos[i].NormalizedReversedProjectionSlotIndex);
        }

        return slots;
    }

    /// <summary>
    /// Reconstructs public sort-key profile declarations from persisted logical-index metadata for shadow compaction.<br/>
    /// The shadow catalog generates fresh collation signatures while preserving profile order, culture, and exact comparison options.<br/>
    /// </summary>
    /// <param name="info">The source logical string index metadata.<br/></param>
    /// <returns>The profile declarations to maintain in the compacted shadow, or null when no sort-key projection exists.<br/></returns>
    private static IReadOnlyList<LibraDexStringSortKeyProfile>? CreateSortKeyProfiles(CatalogIndexInfo info)
    {
        if (info.SortKeyProfiles is { Count: > 0 } profiles)
        {
            LibraDexStringSortKeyProfile[] result = new LibraDexStringSortKeyProfile[profiles.Count];
            for (int i = 0; i < result.Length; i++)
            {
                result[i] = new LibraDexStringSortKeyProfile(profiles[i].CultureName, profiles[i].CompareOptions);
            }

            return result;
        }
        if (info.SortKeyProjectionSlotIndex < 0)
        {
            return null;
        }

        return new[] { new LibraDexStringSortKeyProfile(info.SortKeyCulture, CompareOptions.IgnoreCase) };
    }

    private static void AddCompanion(HashSet<int> slots, int slotIndex)
    {
        if (slotIndex >= 0)
            slots.Add(slotIndex);
    }
}
