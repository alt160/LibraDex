using System.Buffers;
using LibraDex.Layouts;
using LibraDex.Views;

namespace LibraDex;

internal sealed partial class LibraDexFileSession
{
    /// <summary>
    /// Opens a directional `VS8` routed range reader for an inclusive raw-key range.<br/>
    /// The reader streams rows as key spans plus scalar identities so callers can choose identity-only, key-only, or full tuple iteration without a materializing adapter.<br/>
    /// </summary>
    /// <param name="rootRouterOffset">The root router offset for the index.</param>
    /// <param name="maxKeyLength">The maximum raw key length accepted by the profile family.</param>
    /// <param name="lowerKey">The inclusive lower raw key.</param>
    /// <param name="upperKey">The inclusive upper raw key.</param>
    /// <param name="maxRouterHops">The maximum number of router pages to follow.</param>
    /// <param name="direction">The requested physical tuple traversal direction.<br/></param>
    /// <param name="allowWriteUpgrade">Whether this internal reader owns the maintenance-only upgradeable coherent lease needed to publish replacements while preserving its source generation.<br/></param>
    /// <param name="captureDiagnostics">Whether the reader records optional traversal diagnostics.<br/></param>
    /// <param name="decodeLogicalKeys">Whether the reader decodes physical key bytes into the logical key representation.<br/></param>
    /// <returns>A cursor positioned before the first matching row.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="upperKey"/> sorts before <paramref name="lowerKey"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="maxRouterHops"/> is not positive.</exception>
    internal VarKeyScalar8RangeReader OpenVarKeyScalar8RangeReader(
        long rootRouterOffset,
        int maxKeyLength,
        ReadOnlySpan<byte> lowerKey,
        ReadOnlySpan<byte> upperKey,
        int maxRouterHops = DefaultVarKeyScalar8MaxRouterHops,
        bool decodeLogicalKeys = false,
        bool captureDiagnostics = false,
        QueryDirection direction = QueryDirection.Ascending,
        bool allowWriteUpgrade = false)
    {
        if (lowerKey.SequenceCompareTo(upperKey) > 0)
        {
            throw new ArgumentException("The upper VS8 key must be greater than or equal to the lower key.", nameof(upperKey));
        }

        if (maxRouterHops <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxRouterHops), maxRouterHops, "The VS8 range reader maximum router hop count must be positive.");
        }

        DataKernel.CoherentReadLease? coherentRead = allowWriteUpgrade
            ? EnterCoherentUpgradeableRead()
            : EnterCoherentRead();
        try
        {
            VarKeyScalar8RangeReader reader = new(
                this,
                rootRouterOffset,
                maxKeyLength,
                lowerKey,
                upperKey,
                maxRouterHops,
                decodeLogicalKeys,
                captureDiagnostics,
                direction,
                coherentRead);
            if (allowWriteUpgrade)
            {
                // Keep the upgradeable generation barrier, but do not let its memory snapshot
                // bleed into writes to the unpublished destination index during maintenance.
                coherentRead.Pause();
            }
            coherentRead = null;
            return reader;
        }
        finally
        {
            coherentRead?.Dispose();
        }
    }

    /// <summary>
    /// Opens a forward-only `VS16` routed range reader for an inclusive raw-key range.<br/>
    /// The reader streams rows as key spans plus high/low scalar identity halves so callers can choose identity-only, key-only, or full tuple iteration without a materializing adapter.<br/>
    /// </summary>
    /// <param name="rootRouterOffset">The root router offset for the index.</param>
    /// <param name="maxKeyLength">The maximum raw key length accepted by the profile family.</param>
    /// <param name="lowerKey">The inclusive lower raw key.</param>
    /// <param name="upperKey">The inclusive upper raw key.</param>
    /// <param name="maxRouterHops">The maximum number of router pages to follow.</param>
    /// <param name="descending">Whether to visit higher-key route targets first.</param>
    /// <param name="decodeLogicalKeys">Whether the reader decodes physical key bytes into the logical key representation.<br/></param>
    /// <returns>A cursor positioned before the first matching row.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="upperKey"/> sorts before <paramref name="lowerKey"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="maxRouterHops"/> is not positive.</exception>
    internal VarKeyScalar16RangeReader OpenVarKeyScalar16RangeReader(
        long rootRouterOffset,
        int maxKeyLength,
        ReadOnlySpan<byte> lowerKey,
        ReadOnlySpan<byte> upperKey,
        int maxRouterHops = DefaultVarKeyScalar16MaxRouterHops,
        bool decodeLogicalKeys = false,
        bool descending = false)
    {
        if (lowerKey.SequenceCompareTo(upperKey) > 0)
        {
            throw new ArgumentException("The upper VS16 key must be greater than or equal to the lower key.", nameof(upperKey));
        }

        if (maxRouterHops <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxRouterHops), maxRouterHops, "The VS16 range reader maximum router hop count must be positive.");
        }

        return new VarKeyScalar16RangeReader(this, rootRouterOffset, maxKeyLength, lowerKey, upperKey, maxRouterHops, decodeLogicalKeys, descending);
    }

    /// <summary>
    /// Reads and validates one `VS8` shelf as a read-only span-backed view.<br/>
    /// The method reuses the session shelf cache when available so range readers share DK/cache behavior with existing identity-copy range reads.<br/>
    /// </summary>
    /// <param name="shelfOffset">The file offset of the shelf to read.</param>
    /// <param name="maxKeyLength">The maximum raw key length accepted by the profile family.</param>
    /// <returns>A valid read-only `VS8` shelf view.</returns>
    /// <exception cref="InvalidDataException">Thrown when the loaded shelf bytes fail validation.</exception>
    internal VarKeyScalar8ReadOnly ReadVarKeyScalar8ReadOnlyShelf(long shelfOffset, int maxKeyLength)
    {
        byte[] shelfBytes = ReadVarKeyScalar8ShelfBytes(shelfOffset, maxKeyLength, out VarKeyScalar8Profile profile);
        VarKeyScalar8ReadOnly shelf = new(shelfBytes, profile);
        if (!shelf.IsValid)
        {
            throw new InvalidDataException("The routed VS8 shelf bytes are invalid.");
        }

        return shelf;
    }

    /// <summary>
    /// Reads and validates one `VS8` shelf for a forward-only, one-pass streaming cursor.<br/>
    /// A shelf already present in mutable batch state or the ordinary session read cache retains its existing ownership contract.<br/>
    /// A cold shelf is read into an ArrayPool buffer without admission to the unbounded session cache, allowing the streaming reader to return that buffer as soon as its final matching slot has been consumed.<br/>
    /// </summary>
    /// <param name="shelfOffset">The file offset of the shelf to read.<br/></param>
    /// <param name="maxKeyLength">The maximum raw key length accepted by the profile family.<br/></param>
    /// <param name="rentedShelfBytes">The ArrayPool-owned buffer that the caller must return, or <see langword="null"/> when existing session-owned bytes were reused.<br/></param>
    /// <returns>A valid read-only `VS8` shelf view.<br/></returns>
    /// <exception cref="InvalidDataException">Thrown when the loaded shelf header, extent, or payload fails validation.<br/></exception>
    internal VarKeyScalar8ReadOnly ReadVarKeyScalar8ReadOnlyShelfForStreaming(
        long shelfOffset,
        int maxKeyLength,
        out byte[]? rentedShelfBytes)
    {
        rentedShelfBytes = null;
        if (durabilityBatchActive || varKeyScalar8ReadShelfCache.ContainsKey(shelfOffset))
        {
            return ReadVarKeyScalar8ReadOnlyShelf(shelfOffset, maxKeyLength);
        }

        Span<byte> header = stackalloc byte[VarKeyScalar8Layout.HeaderSize];
        kernel.Read(shelfOffset, header);
        if (VarKeyScalar8Layout.ReadMagic(header) != VarKeyScalar8Layout.Magic ||
            VarKeyScalar8Layout.ReadFormatVersion(header) != VarKeyScalar8Layout.FormatVersion ||
            VarKeyScalar8Layout.ReadHeaderSize(header) != VarKeyScalar8Layout.HeaderSize)
        {
            throw new InvalidDataException("The routed VS8 streaming shelf header is invalid.");
        }

        int shelfExtentSize = VarKeyScalar8Layout.ReadShelfExtentSize(header);
        if (shelfExtentSize < VarKeyScalar8Layout.HeaderSize + 32)
        {
            throw new InvalidDataException(
                $"The persisted VS8 streaming shelf header contains an invalid extent. ShelfOffset={shelfOffset}; ShelfExtentSize={shelfExtentSize}.");
        }

        VarKeyScalar8Profile profile = VarKeyScalar8Profile.Create(shelfExtentSize, maxKeyLength) with { Descending = (VarKeyScalar8Layout.ReadFlags(header) & VarKeyScalar8Layout.DescendingFlag) != 0 };
        byte[] rented = ArrayPool<byte>.Shared.Rent(shelfExtentSize);
        try
        {
            kernel.Read(shelfOffset, rented.AsSpan(0, shelfExtentSize));
            VarKeyScalar8ReadOnly shelf = new(new ReadOnlyMemory<byte>(rented, 0, shelfExtentSize), profile);
            if (!shelf.IsValid)
            {
                throw new InvalidDataException("The routed VS8 streaming shelf bytes are invalid.");
            }

            rentedShelfBytes = rented;
            return shelf;
        }
        catch
        {
            ArrayPool<byte>.Shared.Return(rented, clearArray: false);
            throw;
        }
    }

    /// <summary>
    /// Reads and validates one `VS16` shelf as a read-only span-backed view.<br/>
    /// The method reuses the session shelf cache when available so range readers share DK/cache behavior with existing identity-copy range reads.<br/>
    /// </summary>
    /// <param name="shelfOffset">The file offset of the shelf to read.</param>
    /// <param name="maxKeyLength">The maximum raw key length accepted by the profile family.</param>
    /// <returns>A valid read-only `VS16` shelf view.</returns>
    /// <exception cref="InvalidDataException">Thrown when the loaded shelf bytes fail validation.</exception>
    internal VarKeyScalar16ReadOnly ReadVarKeyScalar16ReadOnlyShelf(long shelfOffset, int maxKeyLength)
    {
        byte[] shelfBytes = ReadVarKeyScalar16ShelfBytes(shelfOffset, maxKeyLength, out VarKeyScalar16Profile profile);
        VarKeyScalar16ReadOnly shelf = new(shelfBytes, profile);
        if (!shelf.IsValid)
        {
            throw new InvalidDataException("The routed VS16 shelf bytes are invalid.");
        }

        return shelf;
    }

    /// <summary>
    /// Opens one worker-owned `VS8` reader from a planner-produced set of disjoint continuation targets.<br/>
    /// A coherent read is acquired on the calling worker thread before any target is queued, allowing the coordinator's planning read to remain held until every worker owns a reader for the same published generation.<br/>
    /// This overload never re-enters the root router and never materializes identities during partition setup.<br/>
    /// </summary>
    /// <param name="maxKeyLength">The maximum encoded key length accepted by the index.<br/></param>
    /// <param name="lowerKey">The inclusive global lower encoded key bound.<br/></param>
    /// <param name="upperKey">The inclusive global upper encoded key bound.<br/></param>
    /// <param name="targets">Disjoint continuation targets assigned to the calling worker.<br/></param>
    /// <param name="decodeLogicalKeys">Whether current keys should expose decoded logical payload bytes.<br/></param>
    /// <param name="captureDiagnostics">Whether physical cursor counters should be retained.<br/></param>
    /// <param name="direction">The requested within-worker traversal direction.<br/></param>
    /// <param name="usePooledStreamingShelfReads">Whether cold shelves should bypass managed cache admission and return pooled buffers immediately after forward consumption.<br/></param>
    /// <returns>A cursor positioned before the first matching row.<br/></returns>
    internal VarKeyScalar8RangeReader OpenVarKeyScalar8RangeReaderFromTargets(
        int maxKeyLength,
        ReadOnlySpan<byte> lowerKey,
        ReadOnlySpan<byte> upperKey,
        IReadOnlyList<VarKeyScalar8PhysicalTarget> targets,
        bool decodeLogicalKeys = false,
        bool captureDiagnostics = false,
        QueryDirection direction = QueryDirection.Ascending,
        bool usePooledStreamingShelfReads = false)
    {
        ArgumentNullException.ThrowIfNull(targets);
        if (lowerKey.SequenceCompareTo(upperKey) > 0)
            throw new ArgumentException("The upper VS8 key must be greater than or equal to the lower key.", nameof(upperKey));
        if (targets.Count == 0)
            throw new ArgumentException("A physical VS8 worker reader requires at least one continuation target.", nameof(targets));

        DataKernel.CoherentReadLease? coherentRead = EnterCoherentRead();
        try
        {
            VarKeyScalar8RangeReader reader = new(
                this,
                maxKeyLength,
                lowerKey,
                upperKey,
                targets,
                decodeLogicalKeys,
                captureDiagnostics,
                direction,
                usePooledStreamingShelfReads,
                coherentRead);
            coherentRead = null;
            return reader;
        }
        finally
        {
            coherentRead?.Dispose();
        }
    }
}
