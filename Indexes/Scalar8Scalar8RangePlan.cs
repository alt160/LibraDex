using System.Buffers;
using LibraDex.Layouts;
using LibraDex.Views;

namespace LibraDex;

/// <summary>
/// Owns a planned `SS8-8` range traversal as retained shelf images plus shelf-local slot extents.<br/>
/// The plan is the shared low-level shape behind cursor reads, counted reads, skips, and future bulk-copy consumers.<br/>
/// Shelf byte arrays and extent arrays are rented; callers must dispose the plan exactly once when all consumers are complete.<br/>
/// </summary>
internal sealed class Scalar8Scalar8RangePlan : IDisposable
{
    private const int DefaultCapacity = 8;

    internal byte[][] Shelves;
    internal long[] ShelfOffsets;
    internal int[] StartSlots;
    internal int[] EndSlots;
    internal byte[] TerminalShelfFlags;
    internal ulong[] TerminalScalarKeys;
    internal long[] TerminalRootOffsets;
    private bool disposed;

    internal Scalar8Scalar8RangePlan(LibraDexFileSession session, Scalar8Scalar8Profile profile)
    {
        Session = session;
        Profile = profile;
        Shelves = ArrayPool<byte[]>.Shared.Rent(DefaultCapacity);
        ShelfOffsets = ArrayPool<long>.Shared.Rent(DefaultCapacity);
        StartSlots = ArrayPool<int>.Shared.Rent(DefaultCapacity);
        EndSlots = ArrayPool<int>.Shared.Rent(DefaultCapacity);
        TerminalShelfFlags = ArrayPool<byte>.Shared.Rent(DefaultCapacity);
        TerminalScalarKeys = ArrayPool<ulong>.Shared.Rent(DefaultCapacity);
        TerminalRootOffsets = ArrayPool<long>.Shared.Rent(DefaultCapacity);
    }

    /// <summary>
    /// Gets the owning file session used to stage shelf-local mutations against this retained plan.<br/>
    /// Cursor-local mutation needs the same durable shelf offsets used by the plan so it can publish one compacted shelf rewrite without rewalking the route.<br/>
    /// </summary>
    internal LibraDexFileSession Session { get; }

    /// <summary>
    /// Gets the fixed shelf profile used to interpret every retained shelf image.<br/>
    /// Consumers use this value to recreate stack-only shelf projections over retained bytes without extra metadata lookups.<br/>
    /// </summary>
    internal Scalar8Scalar8Profile Profile { get; }

    /// <summary>
    /// Gets the number of shelf extents retained by this plan.<br/>
    /// Each extent maps one retained shelf buffer to an inclusive/exclusive slot interval in <see cref="StartSlots"/> and <see cref="EndSlots"/>.<br/>
    /// </summary>
    internal int ShelfCount { get; private set; }

    /// <summary>
    /// Gets the total number of rows represented by the planned shelf extents.<br/>
    /// The value is computed while shelves are added and does not require per-row iteration.<br/>
    /// </summary>
    internal int RowCount { get; private set; }

    /// <summary>
    /// Adds a retained shelf image when it contains at least one key in the inclusive encoded range.<br/>
    /// Empty shelf intervals return the provided buffer to the shared pool immediately because no consumer will reference it.<br/>
    /// </summary>
    /// <param name="shelfOffset">The durable offset of the retained shelf image.</param>
    /// <param name="shelfBytes">The retained shelf image, rented from the shared byte array pool.</param>
    /// <param name="lowerEncodedKey">The inclusive lower encoded sortable key.</param>
    /// <param name="upperEncodedKey">The inclusive upper encoded sortable key.</param>
    /// <exception cref="InvalidDataException">Thrown when the supplied shelf image is not a valid `SS8-8` shelf.</exception>
    internal void AddShelfRange(long shelfOffset, byte[] shelfBytes, ulong lowerEncodedKey, ulong upperEncodedKey)
    {
        ThrowIfDisposed();

        Scalar8Scalar8ReadOnly shelf = new(shelfBytes, Profile);
        if (!shelf.IsValid)
        {
            ArrayPool<byte>.Shared.Return(shelfBytes, clearArray: false);
            throw new InvalidDataException("The routed SS8-8 range plan target shelf is invalid.");
        }

        int startSlot = shelf.LowerBoundKey(shelf.IsDescending ? upperEncodedKey : lowerEncodedKey);
        int endSlot = startSlot;
        while (endSlot < shelf.ItemCount && (shelf.IsDescending
            ? shelf.ReadKeyAt(endSlot) >= lowerEncodedKey
            : shelf.ReadKeyAt(endSlot) <= upperEncodedKey))
        {
            endSlot++;
        }

        if (endSlot <= startSlot)
        {
            ArrayPool<byte>.Shared.Return(shelfBytes, clearArray: false);
            return;
        }

        if (ShelfCount == Shelves.Length)
        {
            Grow();
        }

        Shelves[ShelfCount] = shelfBytes;
        ShelfOffsets[ShelfCount] = shelfOffset;
        StartSlots[ShelfCount] = startSlot;
        EndSlots[ShelfCount] = endSlot;
        TerminalShelfFlags[ShelfCount] = 0;
        ShelfCount++;
        RowCount = checked(RowCount + endSlot - startSlot);
    }

    /// <summary>
    /// Adds a retained terminal identity shelf image for an exhausted `SS8-8` key route.<br/>
    /// The key is carried by the terminal root once, so the retained shelf contributes identity slots only and uses <paramref name="encodedKey"/> for cursor key projection.<br/>
    /// </summary>
    /// <param name="shelfOffset">The durable offset of the terminal identity shelf.</param>
    /// <param name="shelfBytes">The retained terminal identity shelf image, rented from the shared byte array pool.</param>
    /// <param name="encodedKey">The exhausted route key stored on the terminal identity root.</param>
    /// <param name="rootOffset">The owning terminal identity root offset retained for shelf traversal.<br/></param>
    internal void AddTerminalIdentityShelfRange(long rootOffset, long shelfOffset, byte[] shelfBytes, ulong encodedKey)
    {
        ThrowIfDisposed();

        int itemCount = TerminalIdentity8ShelfLayout.ReadItemCount(shelfBytes);
        if (itemCount <= 0)
        {
            ArrayPool<byte>.Shared.Return(shelfBytes, clearArray: false);
            return;
        }

        if (ShelfCount == Shelves.Length)
        {
            Grow();
        }

        Shelves[ShelfCount] = shelfBytes;
        ShelfOffsets[ShelfCount] = shelfOffset;
        StartSlots[ShelfCount] = 0;
        EndSlots[ShelfCount] = itemCount;
        TerminalShelfFlags[ShelfCount] = 1;
        TerminalScalarKeys[ShelfCount] = encodedKey;
        TerminalRootOffsets[ShelfCount] = rootOffset;
        ShelfCount++;
        RowCount = checked(RowCount + itemCount);
    }

    /// <summary>
    /// Copies encoded identities from the planned shelf extents into a caller-owned destination span.<br/>
    /// The traversal work is already complete, so this method only walks retained shelf-local slot intervals and performs identity lane copies.<br/>
    /// </summary>
    /// <param name="destination">The destination span that receives encoded identities.</param>
    /// <returns>The number of encoded identities copied.</returns>
    /// <exception cref="ArgumentException">Thrown when the destination span is shorter than <see cref="RowCount"/>.</exception>
    internal int CopyEncodedIdentities(Span<ulong> destination)
    {
        ThrowIfDisposed();
        if (destination.Length < RowCount)
        {
            throw new ArgumentException("The destination span is smaller than the planned SS8-8 range row count.", nameof(destination));
        }

        int copied = 0;
        for (int shelfIndex = 0; shelfIndex < ShelfCount; shelfIndex++)
        {
            if (TerminalShelfFlags[shelfIndex] != 0)
            {
                for (int slot = StartSlots[shelfIndex]; slot < EndSlots[shelfIndex]; slot++)
                {
                    destination[copied] = TerminalIdentity8ShelfLayout.ReadIdentity(Shelves[shelfIndex], slot);
                    copied++;
                }

                continue;
            }

            Scalar8Scalar8ReadOnly shelf = new(Shelves[shelfIndex], Profile);
            for (int slot = StartSlots[shelfIndex]; slot < EndSlots[shelfIndex]; slot++)
            {
                destination[copied] = shelf.ReadIdentityAt(slot);
                copied++;
            }
        }

        return copied;
    }

    /// <summary>
    /// Releases all retained shelf buffers and extent arrays back to their shared pools.<br/>
    /// The plan must not be used after disposal; disposal is idempotent to simplify reader cleanup paths.<br/>
    /// </summary>
    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        for (int i = 0; i < ShelfCount; i++)
        {
            byte[]? shelfBytes = Shelves[i];
            if (shelfBytes is not null)
            {
                ArrayPool<byte>.Shared.Return(shelfBytes, clearArray: false);
                Shelves[i] = null!;
            }
        }

        ArrayPool<byte[]>.Shared.Return(Shelves, clearArray: true);
        ArrayPool<long>.Shared.Return(ShelfOffsets, clearArray: false);
        ArrayPool<int>.Shared.Return(StartSlots, clearArray: false);
        ArrayPool<int>.Shared.Return(EndSlots, clearArray: false);
        ArrayPool<byte>.Shared.Return(TerminalShelfFlags, clearArray: false);
        ArrayPool<ulong>.Shared.Return(TerminalScalarKeys, clearArray: false);
        ArrayPool<long>.Shared.Return(TerminalRootOffsets, clearArray: false);
        Shelves = Array.Empty<byte[]>();
        ShelfOffsets = Array.Empty<long>();
        StartSlots = Array.Empty<int>();
        EndSlots = Array.Empty<int>();
        TerminalShelfFlags = Array.Empty<byte>();
        TerminalScalarKeys = Array.Empty<ulong>();
        TerminalRootOffsets = Array.Empty<long>();
        ShelfCount = 0;
        RowCount = 0;
    }

    private void Grow()
    {
        int newLength = checked(Shelves.Length * 2);
        byte[][] newShelves = ArrayPool<byte[]>.Shared.Rent(newLength);
        long[] newShelfOffsets = ArrayPool<long>.Shared.Rent(newLength);
        int[] newStartSlots = ArrayPool<int>.Shared.Rent(newLength);
        int[] newEndSlots = ArrayPool<int>.Shared.Rent(newLength);
        byte[] newTerminalShelfFlags = ArrayPool<byte>.Shared.Rent(newLength);
        ulong[] newTerminalScalarKeys = ArrayPool<ulong>.Shared.Rent(newLength);
        long[] newTerminalRootOffsets = ArrayPool<long>.Shared.Rent(newLength);
        Array.Copy(Shelves, newShelves, ShelfCount);
        ShelfOffsets.AsSpan(0, ShelfCount).CopyTo(newShelfOffsets);
        StartSlots.AsSpan(0, ShelfCount).CopyTo(newStartSlots);
        EndSlots.AsSpan(0, ShelfCount).CopyTo(newEndSlots);
        TerminalShelfFlags.AsSpan(0, ShelfCount).CopyTo(newTerminalShelfFlags);
        TerminalScalarKeys.AsSpan(0, ShelfCount).CopyTo(newTerminalScalarKeys);
        TerminalRootOffsets.AsSpan(0, ShelfCount).CopyTo(newTerminalRootOffsets);
        ArrayPool<byte[]>.Shared.Return(Shelves, clearArray: true);
        ArrayPool<long>.Shared.Return(ShelfOffsets, clearArray: false);
        ArrayPool<int>.Shared.Return(StartSlots, clearArray: false);
        ArrayPool<int>.Shared.Return(EndSlots, clearArray: false);
        ArrayPool<byte>.Shared.Return(TerminalShelfFlags, clearArray: false);
        ArrayPool<ulong>.Shared.Return(TerminalScalarKeys, clearArray: false);
        ArrayPool<long>.Shared.Return(TerminalRootOffsets, clearArray: false);
        Shelves = newShelves;
        ShelfOffsets = newShelfOffsets;
        StartSlots = newStartSlots;
        EndSlots = newEndSlots;
        TerminalShelfFlags = newTerminalShelfFlags;
        TerminalScalarKeys = newTerminalScalarKeys;
        TerminalRootOffsets = newTerminalRootOffsets;
    }

    private void ThrowIfDisposed()
    {
        if (disposed)
        {
            throw new ObjectDisposedException(nameof(Scalar8Scalar8RangePlan));
        }
    }
}
