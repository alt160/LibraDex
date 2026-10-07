using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace LibraDex.Layouts;

/// <summary>
/// Defines the fixed root descriptor used by the harness-gated UInt64 ordered-set prototype.<br/>
/// The descriptor is deliberately separate from index-directory metadata because a set has no identity axis, projection catalog, or source-completeness contract.<br/>
/// </summary>
internal static class UInt64SetPrototypeRootLayout
{
    public const uint Magic = 0x54455355U;
    public const ushort FormatVersion = 1;
    public const ushort HeaderSize = 64;
    public const int Size = 4096;
    public const int PageSize = 4096;

    public const int MagicOffset = 0;
    public const int FormatVersionOffset = 4;
    public const int HeaderSizeOffset = 6;
    public const int CollectionKindOffset = 8;
    public const int CounterBitsOffset = 9;
    public const int FlagsOffset = 10;
    public const int PageSizeOffset = 12;
    public const int RootRouterOffsetOffset = 16;
    public const int AllocationDirectoryOffsetOffset = 24;
    public const int DistinctCountOffset = 32;
    public const int ExactOccurrenceCountOffset = 40;
    public const int SaturationCeilingOffset = 48;
    public const int GenerationOffset = 56;

    /// <summary>
    /// Initializes one complete UInt64 ordered-set root descriptor.<br/>
    /// Reserved bytes are cleared so later format additions cannot accidentally depend on pooled-buffer contents.<br/>
    /// </summary>
    /// <param name="target">The complete writable root page.<br/></param>
    /// <param name="kind">The persisted collection behavior.<br/></param>
    /// <param name="counterBits">The persisted counter width, or zero for a presence set.<br/></param>
    /// <param name="allocationDirectoryOffset">The backing allocator-directory offset.<br/></param>
    /// <param name="saturationCeiling">The caller-selected saturation ceiling, or zero when not applicable.<br/></param>
    public static void Initialize(
        Span<byte> target,
        UInt64SetPrototypeKind kind,
        byte counterBits,
        long allocationDirectoryOffset,
        ulong saturationCeiling)
    {
        if (target.Length < Size)
            throw new ArgumentException("The UInt64 set root page is smaller than the persisted root layout.", nameof(target));

        target.Slice(0, Size).Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(target.Slice(MagicOffset, sizeof(uint)), Magic);
        BinaryPrimitives.WriteUInt16LittleEndian(target.Slice(FormatVersionOffset, sizeof(ushort)), FormatVersion);
        BinaryPrimitives.WriteUInt16LittleEndian(target.Slice(HeaderSizeOffset, sizeof(ushort)), HeaderSize);
        target[CollectionKindOffset] = (byte)kind;
        target[CounterBitsOffset] = counterBits;
        BinaryPrimitives.WriteInt32LittleEndian(target.Slice(PageSizeOffset, sizeof(int)), PageSize);
        BinaryPrimitives.WriteInt64LittleEndian(target.Slice(AllocationDirectoryOffsetOffset, sizeof(long)), allocationDirectoryOffset);
        BinaryPrimitives.WriteUInt64LittleEndian(target.Slice(SaturationCeilingOffset, sizeof(ulong)), saturationCeiling);
    }

    /// <summary>
    /// Validates the fixed root fields required by the first UInt64 presence-set prototype.<br/>
    /// Collection-specific counter validation remains outside this helper so later counted formats can reuse the root contract without weakening presence checks.<br/>
    /// </summary>
    /// <param name="source">The complete persisted root page.<br/></param>
    /// <returns><see langword="true"/> when the fixed format fields are valid.<br/></returns>
    public static bool IsValid(ReadOnlySpan<byte> source)
    {
        return source.Length >= Size &&
               BinaryPrimitives.ReadUInt32LittleEndian(source.Slice(MagicOffset, sizeof(uint))) == Magic &&
               BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(FormatVersionOffset, sizeof(ushort))) == FormatVersion &&
               BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(HeaderSizeOffset, sizeof(ushort))) == HeaderSize &&
               BinaryPrimitives.ReadInt32LittleEndian(source.Slice(PageSizeOffset, sizeof(int))) == PageSize;
    }

    /// <summary>
    /// Reads the persisted collection behavior from a validated root page.<br/>
    /// </summary>
    /// <param name="source">The persisted root bytes.<br/></param>
    /// <returns>The collection behavior recorded at creation.<br/></returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static UInt64SetPrototypeKind ReadKind(ReadOnlySpan<byte> source) =>
        (UInt64SetPrototypeKind)source[CollectionKindOffset];

    /// <summary>
    /// Reads the authoritative root-router offset.<br/>
    /// </summary>
    /// <param name="source">The persisted root bytes.<br/></param>
    /// <returns>The nonzero root-router offset after initialization completes.<br/></returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long ReadRootRouterOffset(ReadOnlySpan<byte> source) =>
        BinaryPrimitives.ReadInt64LittleEndian(source.Slice(RootRouterOffsetOffset, sizeof(long)));

    /// <summary>
    /// Writes the authoritative root-router offset during initial topology publication.<br/>
    /// </summary>
    /// <param name="target">The writable root bytes.<br/></param>
    /// <param name="value">The allocated root-router offset.<br/></param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteRootRouterOffset(Span<byte> target, long value) =>
        BinaryPrimitives.WriteInt64LittleEndian(target.Slice(RootRouterOffsetOffset, sizeof(long)), value);

    /// <summary>
    /// Reads the file allocation-directory offset used to restore reusable extent classes after reopen.<br/>
    /// </summary>
    /// <param name="source">The persisted root bytes.<br/></param>
    /// <returns>The allocation-directory offset.<br/></returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long ReadAllocationDirectoryOffset(ReadOnlySpan<byte> source) =>
        BinaryPrimitives.ReadInt64LittleEndian(source.Slice(AllocationDirectoryOffsetOffset, sizeof(long)));

    /// <summary>
    /// Reads the exact number of distinct keys in the set.<br/>
    /// </summary>
    /// <param name="source">The persisted root bytes.<br/></param>
    /// <returns>The exact distinct-key count.<br/></returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong ReadDistinctCount(ReadOnlySpan<byte> source) =>
        BinaryPrimitives.ReadUInt64LittleEndian(source.Slice(DistinctCountOffset, sizeof(ulong)));

    /// <summary>
    /// Reads the logical mutation generation used by readers and diagnostics.<br/>
    /// </summary>
    /// <param name="source">The persisted root bytes.<br/></param>
    /// <returns>The persisted generation.<br/></returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong ReadGeneration(ReadOnlySpan<byte> source) =>
        BinaryPrimitives.ReadUInt64LittleEndian(source.Slice(GenerationOffset, sizeof(ulong)));

    /// <summary>
    /// Writes the distinct count and generation into the compact mutable root range.<br/>
    /// Exact-occurrence and saturation fields remain zero for the presence-set proof.<br/>
    /// </summary>
    /// <param name="target">The writable 32-byte root mutation range beginning at <see cref="DistinctCountOffset"/>.<br/></param>
    /// <param name="distinctCount">The new exact distinct-key count.<br/></param>
    /// <param name="generation">The new mutation generation.<br/></param>
    public static void WritePresenceMutation(Span<byte> target, ulong distinctCount, ulong generation)
    {
        if (target.Length < 32)
            throw new ArgumentException("The UInt64 set root mutation range must contain 32 bytes.", nameof(target));

        target.Slice(0, 32).Clear();
        BinaryPrimitives.WriteUInt64LittleEndian(target.Slice(0, sizeof(ulong)), distinctCount);
        BinaryPrimitives.WriteUInt64LittleEndian(target.Slice(24, sizeof(ulong)), generation);
    }

    /// <summary>
    /// Reads the persisted counter width for a counted set root.<br/>
    /// Presence roots store zero; counted roots store one of 2, 4, 8, 16, 32, or 64 bits.<br/>
    /// </summary>
    /// <param name="source">The persisted root bytes.<br/></param>
    /// <returns>The persisted counter width in bits.<br/></returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static byte ReadCounterBits(ReadOnlySpan<byte> source) => source[CounterBitsOffset];

    /// <summary>
    /// Reads the exact total for an exact counted set or the retained capped total for a saturating counted set.<br/>
    /// </summary>
    /// <param name="source">The persisted root bytes.<br/></param>
    /// <returns>The authoritative aggregate of the stored per-key counters.<br/></returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong ReadOccurrenceCount(ReadOnlySpan<byte> source) =>
        BinaryPrimitives.ReadUInt64LittleEndian(source.Slice(ExactOccurrenceCountOffset, sizeof(ulong)));

    /// <summary>
    /// Reads the caller-selected saturation ceiling.<br/>
    /// Exact counted sets store zero because their maximum is the UInt64 numeric limit rather than a saturation policy.<br/>
    /// </summary>
    /// <param name="source">The persisted root bytes.<br/></param>
    /// <returns>The saturation ceiling, or zero for a non-saturating root.<br/></returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong ReadSaturationCeiling(ReadOnlySpan<byte> source) =>
        BinaryPrimitives.ReadUInt64LittleEndian(source.Slice(SaturationCeilingOffset, sizeof(ulong)));

    /// <summary>
    /// Writes the complete mutable counted-set root range after one successful logical mutation.<br/>
    /// Exact mode records the exact occurrence total; saturating mode records the sum of retained capped counters and preserves its creation-time ceiling.<br/>
    /// </summary>
    /// <param name="target">The writable 32-byte root mutation range beginning at <see cref="DistinctCountOffset"/>.<br/></param>
    /// <param name="distinctCount">The new exact distinct-key count.<br/></param>
    /// <param name="occurrenceCount">The new aggregate of stored per-key counters.<br/></param>
    /// <param name="saturationCeiling">The persisted saturation ceiling, or zero for exact mode.<br/></param>
    /// <param name="generation">The new mutation generation.<br/></param>
    public static void WriteCountedMutation(
        Span<byte> target,
        ulong distinctCount,
        ulong occurrenceCount,
        ulong saturationCeiling,
        ulong generation)
    {
        if (target.Length < 32)
            throw new ArgumentException("The UInt64 counted-set root mutation range must contain 32 bytes.", nameof(target));

        BinaryPrimitives.WriteUInt64LittleEndian(target.Slice(0, sizeof(ulong)), distinctCount);
        BinaryPrimitives.WriteUInt64LittleEndian(target.Slice(8, sizeof(ulong)), occurrenceCount);
        BinaryPrimitives.WriteUInt64LittleEndian(target.Slice(16, sizeof(ulong)), saturationCeiling);
        BinaryPrimitives.WriteUInt64LittleEndian(target.Slice(24, sizeof(ulong)), generation);
    }
}

/// <summary>
/// Defines the packed key-only sparse shelf used by the first UInt64 ordered-set proof.<br/>
/// Keys are stored in strict ascending order so membership is a binary search and ordered traversal is a contiguous scan.<br/>
/// </summary>
internal static class UInt64SetPrototypeSparseLayout
{
    public const uint Magic = 0x46534B55U;
    public const ushort FormatVersion = 1;
    public const ushort HeaderSize = 32;
    public const int Size = UInt64SetPrototypeRootLayout.PageSize;
    public const int KeySize = sizeof(ulong);
    public const ushort Capacity = (Size - HeaderSize) / KeySize;

    public const int MagicOffset = 0;
    public const int FormatVersionOffset = 4;
    public const int HeaderSizeOffset = 6;
    public const int ItemCountOffset = 8;
    public const int CapacityOffset = 10;
    public const int FlagsOffset = 12;
    public const int MinimumKeyOffset = 16;
    public const int MaximumKeyOffset = 24;
    public const int KeysOffset = HeaderSize;

    /// <summary>
    /// Initializes a complete sparse shelf from caller-supplied sorted distinct keys.<br/>
    /// The caller retains ownership of the key span; values are encoded directly into the target page without intermediate arrays.<br/>
    /// </summary>
    /// <param name="target">The complete writable sparse shelf page.<br/></param>
    /// <param name="keys">Strictly ascending distinct UInt64 keys.<br/></param>
    public static void Initialize(Span<byte> target, ReadOnlySpan<ulong> keys)
    {
        if (target.Length < Size)
            throw new ArgumentException("The UInt64 sparse set shelf is smaller than the persisted page layout.", nameof(target));
        if (keys.Length > Capacity)
            throw new ArgumentOutOfRangeException(nameof(keys), keys.Length, "The UInt64 sparse set shelf cannot contain more keys than its fixed capacity.");

        target.Slice(0, Size).Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(target.Slice(MagicOffset, sizeof(uint)), Magic);
        BinaryPrimitives.WriteUInt16LittleEndian(target.Slice(FormatVersionOffset, sizeof(ushort)), FormatVersion);
        BinaryPrimitives.WriteUInt16LittleEndian(target.Slice(HeaderSizeOffset, sizeof(ushort)), HeaderSize);
        BinaryPrimitives.WriteUInt16LittleEndian(target.Slice(ItemCountOffset, sizeof(ushort)), checked((ushort)keys.Length));
        BinaryPrimitives.WriteUInt16LittleEndian(target.Slice(CapacityOffset, sizeof(ushort)), Capacity);

        for (int i = 0; i < keys.Length; i++)
        {
            if (i != 0 && keys[i - 1] >= keys[i])
                throw new ArgumentException("UInt64 sparse set shelf keys must be strictly ascending.", nameof(keys));
            WriteKeyAt(target, i, keys[i]);
        }

        WriteBounds(target, keys.Length);
    }

    /// <summary>
    /// Validates the sparse shelf's fixed header and bounded live-item count.<br/>
    /// Full ordering validation is deliberately performed only by structural diagnostics rather than every hot lookup.<br/>
    /// </summary>
    /// <param name="source">The complete persisted shelf page.<br/></param>
    /// <returns><see langword="true"/> when the fixed fields and live count are valid.<br/></returns>
    public static bool IsValid(ReadOnlySpan<byte> source)
    {
        return source.Length >= Size &&
               BinaryPrimitives.ReadUInt32LittleEndian(source.Slice(MagicOffset, sizeof(uint))) == Magic &&
               BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(FormatVersionOffset, sizeof(ushort))) == FormatVersion &&
               BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(HeaderSizeOffset, sizeof(ushort))) == HeaderSize &&
               BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(CapacityOffset, sizeof(ushort))) == Capacity &&
               ReadItemCount(source) <= Capacity;
    }

    /// <summary>
    /// Reads the live number of packed keys in a validated sparse shelf.<br/>
    /// </summary>
    /// <param name="source">The persisted shelf bytes.<br/></param>
    /// <returns>The live key count.<br/></returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ushort ReadItemCount(ReadOnlySpan<byte> source) =>
        BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(ItemCountOffset, sizeof(ushort)));

    /// <summary>
    /// Reads one sortable UInt64 key from its packed ordinal.<br/>
    /// </summary>
    /// <param name="source">The persisted shelf bytes.<br/></param>
    /// <param name="index">The zero-based live key ordinal.<br/></param>
    /// <returns>The decoded UInt64 key.<br/></returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong ReadKeyAt(ReadOnlySpan<byte> source, int index) =>
        BinaryPrimitives.ReadUInt64BigEndian(source.Slice(KeysOffset + (index * KeySize), KeySize));

    /// <summary>
    /// Writes one UInt64 key in sortable big-endian form at its packed ordinal.<br/>
    /// </summary>
    /// <param name="target">The writable shelf bytes.<br/></param>
    /// <param name="index">The zero-based key ordinal.<br/></param>
    /// <param name="key">The UInt64 key to encode.<br/></param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteKeyAt(Span<byte> target, int index, ulong key) =>
        BinaryPrimitives.WriteUInt64BigEndian(target.Slice(KeysOffset + (index * KeySize), KeySize), key);

    /// <summary>
    /// Finds the first packed ordinal whose key is greater than or equal to the requested key.<br/>
    /// The method performs no allocation and is shared by membership, insertion, and removal.<br/>
    /// </summary>
    /// <param name="source">The validated shelf bytes.<br/></param>
    /// <param name="key">The key to locate.<br/></param>
    /// <returns>The lower-bound ordinal in the range zero through item count.<br/></returns>
    public static int LowerBound(ReadOnlySpan<byte> source, ulong key)
    {
        int low = 0;
        int high = ReadItemCount(source);
        while (low < high)
        {
            int middle = low + ((high - low) >> 1);
            if (ReadKeyAt(source, middle) < key)
                low = middle + 1;
            else
                high = middle;
        }

        return low;
    }

    /// <summary>
    /// Inserts one absent key into a non-full sparse shelf while preserving packed ascending order.<br/>
    /// The caller must have performed the duplicate check and capacity guard through <see cref="LowerBound"/>.<br/>
    /// </summary>
    /// <param name="target">The complete writable shelf image.<br/></param>
    /// <param name="insertIndex">The lower-bound ordinal for the absent key.<br/></param>
    /// <param name="key">The key to insert.<br/></param>
    public static void InsertAbsent(Span<byte> target, int insertIndex, ulong key)
    {
        int count = ReadItemCount(target);
        if (count >= Capacity)
            throw new InvalidOperationException("The UInt64 sparse set shelf is full.");
        if ((uint)insertIndex > (uint)count)
            throw new ArgumentOutOfRangeException(nameof(insertIndex));

        int moveBytes = (count - insertIndex) * KeySize;
        if (moveBytes != 0)
        {
            target.Slice(KeysOffset + (insertIndex * KeySize), moveBytes)
                .CopyTo(target.Slice(KeysOffset + ((insertIndex + 1) * KeySize), moveBytes));
        }

        WriteKeyAt(target, insertIndex, key);
        BinaryPrimitives.WriteUInt16LittleEndian(target.Slice(ItemCountOffset, sizeof(ushort)), checked((ushort)(count + 1)));
        WriteBounds(target, count + 1);
    }

    /// <summary>
    /// Removes one known key ordinal from a sparse shelf and closes the packed gap.<br/>
    /// The trailing physical key bytes are cleared so validation and diagnostics cannot mistake stale payload for live state.<br/>
    /// </summary>
    /// <param name="target">The complete writable shelf image.<br/></param>
    /// <param name="removeIndex">The zero-based live key ordinal to remove.<br/></param>
    public static void RemoveAt(Span<byte> target, int removeIndex)
    {
        int count = ReadItemCount(target);
        if ((uint)removeIndex >= (uint)count)
            throw new ArgumentOutOfRangeException(nameof(removeIndex));

        int moveBytes = (count - removeIndex - 1) * KeySize;
        if (moveBytes != 0)
        {
            target.Slice(KeysOffset + ((removeIndex + 1) * KeySize), moveBytes)
                .CopyTo(target.Slice(KeysOffset + (removeIndex * KeySize), moveBytes));
        }

        target.Slice(KeysOffset + ((count - 1) * KeySize), KeySize).Clear();
        BinaryPrimitives.WriteUInt16LittleEndian(target.Slice(ItemCountOffset, sizeof(ushort)), checked((ushort)(count - 1)));
        WriteBounds(target, count - 1);
    }

    /// <summary>
    /// Writes cached minimum/maximum keys after a local shelf mutation.<br/>
    /// Empty shelves clear both fields; non-empty shelves derive them from the first and last packed keys.<br/>
    /// </summary>
    /// <param name="target">The complete writable shelf image.<br/></param>
    /// <param name="count">The post-mutation live key count.<br/></param>
    private static void WriteBounds(Span<byte> target, int count)
    {
        ulong minimum = count == 0 ? 0 : ReadKeyAt(target, 0);
        ulong maximum = count == 0 ? 0 : ReadKeyAt(target, count - 1);
        BinaryPrimitives.WriteUInt64BigEndian(target.Slice(MinimumKeyOffset, sizeof(ulong)), minimum);
        BinaryPrimitives.WriteUInt64BigEndian(target.Slice(MaximumKeyOffset, sizeof(ulong)), maximum);
    }
}

/// <summary>
/// Identifies the persisted behavior of one UInt64 ordered-set prototype root.<br/>
/// Distinct values intentionally map to distinct future leaf formats so presence and saturation do not pay exact-count costs.<br/>
/// </summary>
internal enum UInt64SetPrototypeKind : byte
{
    Presence = 1,
    Saturating = 2,
    Exact = 3,
    RoutedPresence = 4
}

/// <summary>
/// Defines the key-plus-counter sparse leaf shared by exact and saturating UInt64 counted sets.<br/>
/// Keys remain packed in ascending big-endian order; counters occupy a parallel fixed-width region so key binary search never steps across value bytes.<br/>
/// Saturating leaves select the smallest promoted 2, 4, 8, 16, 32, or 64-bit width that can represent their creation-time ceiling, while exact leaves use 64-bit counters.<br/>
/// </summary>
internal static class UInt64CountedSetSparseLayout
{
    public const uint SaturatingMagic = 0x53434B55U;
    public const uint ExactMagic = 0x45434B55U;
    public const ushort FormatVersion = 1;
    public const ushort HeaderSize = 32;
    public const int Size = UInt64SetPrototypeRootLayout.PageSize;
    public const int KeySize = sizeof(ulong);

    public const ushort Capacity2 = 492;
    public const ushort Capacity4 = 478;
    public const ushort Capacity8 = 451;
    public const ushort Capacity16 = 406;
    public const ushort Capacity32 = 338;
    public const ushort Capacity64 = 254;

    public const int MagicOffset = 0;
    public const int FormatVersionOffset = 4;
    public const int HeaderSizeOffset = 6;
    public const int ItemCountOffset = 8;
    public const int CapacityOffset = 10;
    public const int CounterBitsOffset = 12;
    public const int CollectionKindOffset = 13;
    public const int FlagsOffset = 14;
    public const int MinimumKeyOffset = 16;
    public const int MaximumKeyOffset = 24;
    public const int KeysOffset = HeaderSize;

    /// <summary>
    /// Gets the exact fixed leaf capacity for one promoted counter width.<br/>
    /// Each capacity accounts for both the packed key region and the parallel counter region inside one 4 KiB page.<br/>
    /// </summary>
    /// <param name="counterBits">The persisted counter width.<br/></param>
    /// <returns>The maximum number of key/counter pairs in one leaf.<br/></returns>
    public static ushort GetCapacity(byte counterBits) => counterBits switch
    {
        2 => Capacity2,
        4 => Capacity4,
        8 => Capacity8,
        16 => Capacity16,
        32 => Capacity32,
        64 => Capacity64,
        _ => throw new ArgumentOutOfRangeException(nameof(counterBits), counterBits, "Counted-set counter width must be 2, 4, 8, 16, 32, or 64 bits.")
    };

    /// <summary>
    /// Initializes one complete counted sparse leaf from sorted distinct keys and positive stored counters.<br/>
    /// The caller supplies already-capped values for saturating mode and exact values for exact mode; no intermediate entry array is created.<br/>
    /// </summary>
    /// <param name="target">The complete writable counted leaf page.<br/></param>
    /// <param name="kind">The exact or saturating persisted collection kind.<br/></param>
    /// <param name="counterBits">The fixed counter width selected at creation.<br/></param>
    /// <param name="keys">Strictly ascending distinct UInt64 keys.<br/></param>
    /// <param name="counts">Positive counters corresponding ordinally to <paramref name="keys"/>.<br/></param>
    public static void Initialize(
        Span<byte> target,
        UInt64SetPrototypeKind kind,
        byte counterBits,
        ReadOnlySpan<ulong> keys,
        ReadOnlySpan<ulong> counts)
    {
        if (target.Length < Size)
            throw new ArgumentException("The UInt64 counted-set shelf is smaller than the persisted page layout.", nameof(target));
        if (kind is not UInt64SetPrototypeKind.Saturating and not UInt64SetPrototypeKind.Exact)
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "A counted shelf must be saturating or exact.");
        if (kind == UInt64SetPrototypeKind.Exact && counterBits != 64)
            throw new ArgumentException("An exact counted shelf must use 64-bit counters.", nameof(counterBits));
        if (keys.Length != counts.Length)
            throw new ArgumentException("Counted-set keys and counters must have identical lengths.", nameof(counts));

        ushort capacity = GetCapacity(counterBits);
        if (keys.Length > capacity)
            throw new ArgumentOutOfRangeException(nameof(keys), keys.Length, "The UInt64 counted-set shelf cannot contain more pairs than its fixed counter-width capacity.");

        target.Slice(0, Size).Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(target.Slice(MagicOffset, sizeof(uint)), GetMagic(kind));
        BinaryPrimitives.WriteUInt16LittleEndian(target.Slice(FormatVersionOffset, sizeof(ushort)), FormatVersion);
        BinaryPrimitives.WriteUInt16LittleEndian(target.Slice(HeaderSizeOffset, sizeof(ushort)), HeaderSize);
        BinaryPrimitives.WriteUInt16LittleEndian(target.Slice(ItemCountOffset, sizeof(ushort)), checked((ushort)keys.Length));
        BinaryPrimitives.WriteUInt16LittleEndian(target.Slice(CapacityOffset, sizeof(ushort)), capacity);
        target[CounterBitsOffset] = counterBits;
        target[CollectionKindOffset] = (byte)kind;

        ulong maximumCounter = GetMaximumCounter(counterBits);
        for (int i = 0; i < keys.Length; i++)
        {
            if (i != 0 && keys[i - 1] >= keys[i])
                throw new ArgumentException("UInt64 counted-set shelf keys must be strictly ascending.", nameof(keys));
            if (counts[i] == 0 || counts[i] > maximumCounter)
                throw new ArgumentOutOfRangeException(nameof(counts), counts[i], "A stored counted-set counter must be positive and fit its selected width.");

            WriteKeyAt(target, i, keys[i]);
            WriteCountAt(target, i, counts[i]);
        }

        WriteBounds(target, keys.Length);
    }

    /// <summary>
    /// Validates the counted leaf's fixed kind, width, capacity, and live-count fields.<br/>
    /// Full ordering and positive-counter validation remain explicit structural diagnostics rather than hot-read work.<br/>
    /// </summary>
    /// <param name="source">The complete persisted leaf bytes.<br/></param>
    /// <param name="expectedKind">The collection kind required by the owning root.<br/></param>
    /// <param name="expectedCounterBits">The counter width required by the owning root.<br/></param>
    /// <returns><see langword="true"/> when the fixed leaf contract matches the owning root.<br/></returns>
    public static bool IsValid(
        ReadOnlySpan<byte> source,
        UInt64SetPrototypeKind expectedKind,
        byte expectedCounterBits)
    {
        if (source.Length < Size ||
            expectedKind is not UInt64SetPrototypeKind.Saturating and not UInt64SetPrototypeKind.Exact ||
            BinaryPrimitives.ReadUInt32LittleEndian(source.Slice(MagicOffset, sizeof(uint))) != GetMagic(expectedKind) ||
            BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(FormatVersionOffset, sizeof(ushort))) != FormatVersion ||
            BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(HeaderSizeOffset, sizeof(ushort))) != HeaderSize ||
            source[CollectionKindOffset] != (byte)expectedKind ||
            source[CounterBitsOffset] != expectedCounterBits)
        {
            return false;
        }

        ushort capacity = GetCapacity(expectedCounterBits);
        return BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(CapacityOffset, sizeof(ushort))) == capacity &&
               ReadItemCount(source) <= capacity;
    }

    /// <summary>
    /// Reads the live pair count from a validated counted leaf.<br/>
    /// </summary>
    /// <param name="source">The persisted counted leaf bytes.<br/></param>
    /// <returns>The live pair count.<br/></returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ushort ReadItemCount(ReadOnlySpan<byte> source) =>
        BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(ItemCountOffset, sizeof(ushort)));

    /// <summary>
    /// Reads one sortable UInt64 key from its packed ordinal.<br/>
    /// </summary>
    /// <param name="source">The persisted counted leaf bytes.<br/></param>
    /// <param name="index">The zero-based live key ordinal.<br/></param>
    /// <returns>The decoded UInt64 key.<br/></returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong ReadKeyAt(ReadOnlySpan<byte> source, int index) =>
        BinaryPrimitives.ReadUInt64BigEndian(source.Slice(KeysOffset + (index * KeySize), KeySize));

    /// <summary>
    /// Writes one UInt64 key in sortable big-endian form at its packed ordinal.<br/>
    /// </summary>
    /// <param name="target">The writable counted leaf bytes.<br/></param>
    /// <param name="index">The zero-based key ordinal.<br/></param>
    /// <param name="key">The UInt64 key to encode.<br/></param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteKeyAt(Span<byte> target, int index, ulong key) =>
        BinaryPrimitives.WriteUInt64BigEndian(target.Slice(KeysOffset + (index * KeySize), KeySize), key);

    /// <summary>
    /// Reads one stored counter without widening allocations or entry materialization.<br/>
    /// </summary>
    /// <param name="source">The persisted counted leaf bytes.<br/></param>
    /// <param name="index">The zero-based live key ordinal.<br/></param>
    /// <returns>The widened UInt64 counter value.<br/></returns>
    public static ulong ReadCountAt(ReadOnlySpan<byte> source, int index)
    {
        byte bits = source[CounterBitsOffset];
        int counterOffset = GetCountersOffset(bits);
        return bits switch
        {
            2 => (ulong)((source[counterOffset + (index >> 2)] >> ((index & 3) * 2)) & 0x03),
            4 => (ulong)((source[counterOffset + (index >> 1)] >> ((index & 1) * 4)) & 0x0F),
            8 => source[counterOffset + index],
            16 => BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(counterOffset + (index * 2), 2)),
            32 => BinaryPrimitives.ReadUInt32LittleEndian(source.Slice(counterOffset + (index * 4), 4)),
            64 => BinaryPrimitives.ReadUInt64LittleEndian(source.Slice(counterOffset + (index * 8), 8)),
            _ => throw new InvalidDataException($"The counted-set leaf uses unsupported {bits}-bit counters.")
        };
    }

    /// <summary>
    /// Writes one positive counter into its fixed-width parallel ordinal.<br/>
    /// The caller is responsible for exact overflow or saturation semantics before invoking this physical operation.<br/>
    /// </summary>
    /// <param name="target">The writable counted leaf bytes.<br/></param>
    /// <param name="index">The zero-based key ordinal.<br/></param>
    /// <param name="value">The positive stored counter value.<br/></param>
    public static void WriteCountAt(Span<byte> target, int index, ulong value)
    {
        byte bits = target[CounterBitsOffset];
        if (value > GetMaximumCounter(bits))
            throw new ArgumentOutOfRangeException(nameof(value), value, "The counted-set counter does not fit its selected width.");

        int counterOffset = GetCountersOffset(bits);
        switch (bits)
        {
            case 2:
            {
                int byteIndex = counterOffset + (index >> 2);
                int shift = (index & 3) * 2;
                byte mask = checked((byte)(0x03 << shift));
                target[byteIndex] = checked((byte)((target[byteIndex] & ~mask) | ((byte)value << shift)));
                break;
            }
            case 4:
            {
                int byteIndex = counterOffset + (index >> 1);
                int shift = (index & 1) * 4;
                byte mask = checked((byte)(0x0F << shift));
                target[byteIndex] = checked((byte)((target[byteIndex] & ~mask) | ((byte)value << shift)));
                break;
            }
            case 8:
                target[counterOffset + index] = checked((byte)value);
                break;
            case 16:
                BinaryPrimitives.WriteUInt16LittleEndian(target.Slice(counterOffset + (index * 2), 2), checked((ushort)value));
                break;
            case 32:
                BinaryPrimitives.WriteUInt32LittleEndian(target.Slice(counterOffset + (index * 4), 4), checked((uint)value));
                break;
            case 64:
                BinaryPrimitives.WriteUInt64LittleEndian(target.Slice(counterOffset + (index * 8), 8), value);
                break;
            default:
                throw new InvalidDataException($"The counted-set leaf uses unsupported {bits}-bit counters.");
        }
    }

    /// <summary>
    /// Finds the first packed ordinal whose key is greater than or equal to the requested key.<br/>
    /// </summary>
    /// <param name="source">The validated counted leaf bytes.<br/></param>
    /// <param name="key">The key to locate.<br/></param>
    /// <returns>The lower-bound ordinal from zero through live count.<br/></returns>
    public static int LowerBound(ReadOnlySpan<byte> source, ulong key)
    {
        int low = 0;
        int high = ReadItemCount(source);
        while (low < high)
        {
            int middle = low + ((high - low) >> 1);
            if (ReadKeyAt(source, middle) < key)
                low = middle + 1;
            else
                high = middle;
        }

        return low;
    }

    /// <summary>
    /// Inserts one absent key with its positive initial counter while preserving packed ascending order.<br/>
    /// Key bytes move as one overlapping span and counter bytes move as one width-aware packed range, avoiding a per-entry decode/encode loop.<br/>
    /// </summary>
    /// <param name="target">The complete writable counted leaf image.<br/></param>
    /// <param name="insertIndex">The lower-bound ordinal for the absent key.<br/></param>
    /// <param name="key">The absent key.<br/></param>
    /// <param name="count">The positive initial stored counter.<br/></param>
    public static void InsertAbsent(Span<byte> target, int insertIndex, ulong key, ulong count)
    {
        int itemCount = ReadItemCount(target);
        int capacity = BinaryPrimitives.ReadUInt16LittleEndian(target.Slice(CapacityOffset, sizeof(ushort)));
        if (itemCount >= capacity)
            throw new InvalidOperationException("The UInt64 counted-set shelf is full.");
        if ((uint)insertIndex > (uint)itemCount)
            throw new ArgumentOutOfRangeException(nameof(insertIndex));

        int keyMoveBytes = (itemCount - insertIndex) * KeySize;
        if (keyMoveBytes != 0)
        {
            target.Slice(KeysOffset + (insertIndex * KeySize), keyMoveBytes)
                .CopyTo(target.Slice(KeysOffset + ((insertIndex + 1) * KeySize), keyMoveBytes));
            ShiftCountersRightForInsert(target, insertIndex, itemCount);
        }

        WriteKeyAt(target, insertIndex, key);
        WriteCountAt(target, insertIndex, count);
        BinaryPrimitives.WriteUInt16LittleEndian(target.Slice(ItemCountOffset, sizeof(ushort)), checked((ushort)(itemCount + 1)));
        WriteBounds(target, itemCount + 1);
    }

    /// <summary>
    /// Removes one known key/counter ordinal and closes both packed regions.<br/>
    /// The trailing physical key and counter are cleared so stale payload cannot resemble live state in diagnostics.<br/>
    /// </summary>
    /// <param name="target">The complete writable counted leaf image.<br/></param>
    /// <param name="removeIndex">The zero-based live ordinal to remove.<br/></param>
    public static void RemoveAt(Span<byte> target, int removeIndex)
    {
        int itemCount = ReadItemCount(target);
        if ((uint)removeIndex >= (uint)itemCount)
            throw new ArgumentOutOfRangeException(nameof(removeIndex));

        int keyMoveBytes = (itemCount - removeIndex - 1) * KeySize;
        if (keyMoveBytes != 0)
        {
            target.Slice(KeysOffset + ((removeIndex + 1) * KeySize), keyMoveBytes)
                .CopyTo(target.Slice(KeysOffset + (removeIndex * KeySize), keyMoveBytes));
            ShiftCountersLeftForRemove(target, removeIndex, itemCount);
        }

        target.Slice(KeysOffset + ((itemCount - 1) * KeySize), KeySize).Clear();
        WriteCountAt(target, itemCount - 1, 0);
        BinaryPrimitives.WriteUInt16LittleEndian(target.Slice(ItemCountOffset, sizeof(ushort)), checked((ushort)(itemCount - 1)));
        WriteBounds(target, itemCount - 1);
    }

    /// <summary>
    /// Returns whether one page magic denotes either counted sparse-leaf format.<br/>
    /// </summary>
    /// <param name="magic">The first four page bytes interpreted as little-endian UInt32.<br/></param>
    /// <returns><see langword="true"/> for exact or saturating counted leaves.<br/></returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsCountedMagic(uint magic) => magic is SaturatingMagic or ExactMagic;

    /// <summary>
    /// Gets the maximum unsigned value representable by one promoted counter width.<br/>
    /// </summary>
    /// <param name="counterBits">The promoted counter width.<br/></param>
    /// <returns>The inclusive representable maximum.<br/></returns>
    public static ulong GetMaximumCounter(byte counterBits) => counterBits switch
    {
        2 => 0x03UL,
        4 => 0x0FUL,
        8 => byte.MaxValue,
        16 => ushort.MaxValue,
        32 => uint.MaxValue,
        64 => ulong.MaxValue,
        _ => throw new ArgumentOutOfRangeException(nameof(counterBits), counterBits, "Counted-set counter width must be 2, 4, 8, 16, 32, or 64 bits.")
    };

    /// <summary>
    /// Resolves the fixed counter-region offset following the maximum-width key region for one leaf profile.<br/>
    /// </summary>
    /// <param name="counterBits">The promoted counter width.<br/></param>
    /// <returns>The first byte of the parallel counter region.<br/></returns>
    private static int GetCountersOffset(byte counterBits) => KeysOffset + (GetCapacity(counterBits) * KeySize);

    /// <summary>
    /// Opens one absent counter slot by moving all following counters one ordinal toward the physical tail.<br/>
    /// Byte-aligned widths use one overlapping span copy; two- and four-bit widths shift the affected byte window from high to low while preserving counters preceding the insertion bit.<br/>
    /// </summary>
    /// <param name="target">The complete writable counted leaf.<br/></param>
    /// <param name="insertIndex">The new counter ordinal to open.<br/></param>
    /// <param name="itemCount">The pre-insertion live counter count.<br/></param>
    private static void ShiftCountersRightForInsert(Span<byte> target, int insertIndex, int itemCount)
    {
        int bits = target[CounterBitsOffset];
        int countersOffset = GetCountersOffset(checked((byte)bits));
        if (bits >= 8)
        {
            int bytesPerCounter = bits >> 3;
            int moveBytes = (itemCount - insertIndex) * bytesPerCounter;
            target.Slice(countersOffset + (insertIndex * bytesPerCounter), moveBytes)
                .CopyTo(target.Slice(countersOffset + ((insertIndex + 1) * bytesPerCounter), moveBytes));
            return;
        }

        int startBit = insertIndex * bits;
        int firstByte = countersOffset + (startBit >> 3);
        int prefixBitCount = startBit & 7;
        int prefixMask = (1 << prefixBitCount) - 1;
        byte preservedPrefix = checked((byte)(target[firstByte] & prefixMask));
        int lastSourceByte = countersOffset + (((itemCount * bits) - 1) >> 3);
        int lastDestinationByte = countersOffset + ((((itemCount + 1) * bits) - 1) >> 3);
        for (int destinationByte = lastDestinationByte; destinationByte >= firstByte; destinationByte--)
        {
            int shifted = destinationByte <= lastSourceByte ? target[destinationByte] << bits : 0;
            if (destinationByte - 1 >= firstByte)
                shifted |= target[destinationByte - 1] >> (8 - bits);
            target[destinationByte] = checked((byte)(shifted & byte.MaxValue));
        }

        target[firstByte] = checked((byte)((target[firstByte] & ~prefixMask) | preservedPrefix));
    }

    /// <summary>
    /// Closes one removed counter slot by moving all following counters one ordinal toward the physical head.<br/>
    /// Byte-aligned widths use one overlapping span copy; packed widths shift the affected byte window from low to high and restore counters preceding the removed bit.<br/>
    /// </summary>
    /// <param name="target">The complete writable counted leaf.<br/></param>
    /// <param name="removeIndex">The counter ordinal being removed.<br/></param>
    /// <param name="itemCount">The pre-removal live counter count.<br/></param>
    private static void ShiftCountersLeftForRemove(Span<byte> target, int removeIndex, int itemCount)
    {
        int bits = target[CounterBitsOffset];
        int countersOffset = GetCountersOffset(checked((byte)bits));
        if (bits >= 8)
        {
            int bytesPerCounter = bits >> 3;
            int moveBytes = (itemCount - removeIndex - 1) * bytesPerCounter;
            target.Slice(countersOffset + ((removeIndex + 1) * bytesPerCounter), moveBytes)
                .CopyTo(target.Slice(countersOffset + (removeIndex * bytesPerCounter), moveBytes));
            return;
        }

        int startBit = removeIndex * bits;
        int firstByte = countersOffset + (startBit >> 3);
        int prefixBitCount = startBit & 7;
        int prefixMask = (1 << prefixBitCount) - 1;
        byte preservedPrefix = checked((byte)(target[firstByte] & prefixMask));
        int lastSourceByte = countersOffset + (((itemCount * bits) - 1) >> 3);
        int lastDestinationBitExclusive = (itemCount - 1) * bits;
        int lastDestinationByte = lastDestinationBitExclusive == 0
            ? firstByte - 1
            : countersOffset + ((lastDestinationBitExclusive - 1) >> 3);
        for (int destinationByte = firstByte; destinationByte <= lastDestinationByte; destinationByte++)
        {
            int shifted = target[destinationByte] >> bits;
            if (destinationByte + 1 <= lastSourceByte)
                shifted |= target[destinationByte + 1] << (8 - bits);
            target[destinationByte] = checked((byte)(shifted & byte.MaxValue));
        }

        target[firstByte] = checked((byte)((target[firstByte] & ~prefixMask) | preservedPrefix));
    }

    /// <summary>
    /// Resolves the distinct page magic for an exact or saturating counted leaf.<br/>
    /// </summary>
    /// <param name="kind">The counted collection kind.<br/></param>
    /// <returns>The persisted leaf magic.<br/></returns>
    private static uint GetMagic(UInt64SetPrototypeKind kind) => kind switch
    {
        UInt64SetPrototypeKind.Saturating => SaturatingMagic,
        UInt64SetPrototypeKind.Exact => ExactMagic,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "A counted shelf must be saturating or exact.")
    };

    /// <summary>
    /// Refreshes cached minimum and maximum keys after a local counted-leaf mutation.<br/>
    /// </summary>
    /// <param name="target">The complete writable counted leaf image.<br/></param>
    /// <param name="count">The post-mutation live pair count.<br/></param>
    private static void WriteBounds(Span<byte> target, int count)
    {
        ulong minimum = count == 0 ? 0 : ReadKeyAt(target, 0);
        ulong maximum = count == 0 ? 0 : ReadKeyAt(target, count - 1);
        BinaryPrimitives.WriteUInt64BigEndian(target.Slice(MinimumKeyOffset, sizeof(ulong)), minimum);
        BinaryPrimitives.WriteUInt64BigEndian(target.Slice(MaximumKeyOffset, sizeof(ulong)), maximum);
    }
}

/// <summary>
/// Defines the compact physical leaf used by a UInt64 routed presence set.<br/>
/// Radix routers preserve ordered regions, while keys inside one leaf retain physical end-fill order so insertion never shifts an existing suffix and removal can close a hole with the final live key.<br/>
/// A parallel packed fingerprint lane rejects most non-candidates before the exact full-key comparison while preserving end-fill mutation.<br/>
/// The format intentionally omits minimum, maximum, slot-array, identity, and ordering metadata that routed membership does not require.<br/>
/// </summary>
internal static class UInt64RoutedSetSparseLayout
{
    public const uint Magic = 0x52534B55U;
    public const ushort FormatVersion = 3;
    public const ushort HeaderSize = 16;
    public const int Size = UInt64SetPrototypeRootLayout.PageSize;
    public const int KeySize = sizeof(ulong);
    public const int FingerprintSize = sizeof(byte);
    public const int FingerprintBits = 4;
    public const int FingerprintsPerByte = 8 / FingerprintBits;
    public const byte FingerprintMask = (1 << FingerprintBits) - 1;
    public const ushort Capacity = 480;
    public const int FingerprintByteCount = (Capacity + FingerprintsPerByte - 1) / FingerprintsPerByte;

    public const int MagicOffset = 0;
    public const int FormatVersionOffset = 4;
    public const int HeaderSizeOffset = 6;
    public const int ItemCountOffset = 8;
    public const int CapacityOffset = 10;
    public const int FlagsOffset = 12;
    public const int FingerprintsOffset = HeaderSize;
    public const int KeysOffset = 256;

    /// <summary>
    /// Initializes one complete routed shelf from caller-owned distinct keys in their supplied physical order.<br/>
    /// No result-side ordering structure is created; the caller is responsible for ensuring every supplied key belongs to the router region that will own this shelf.<br/>
    /// </summary>
    /// <param name="target">The complete writable routed-shelf page.<br/></param>
    /// <param name="keys">The distinct UInt64 keys to retain in physical end-fill order.<br/></param>
    public static void Initialize(Span<byte> target, ReadOnlySpan<ulong> keys)
    {
        if (target.Length < Size)
            throw new ArgumentException("The UInt64 routed-set shelf is smaller than the persisted page layout.", nameof(target));
        if (keys.Length > Capacity)
            throw new ArgumentOutOfRangeException(nameof(keys), keys.Length, "The UInt64 routed-set shelf cannot contain more keys than its fixed capacity.");

        target.Slice(0, Size).Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(target.Slice(MagicOffset, sizeof(uint)), Magic);
        BinaryPrimitives.WriteUInt16LittleEndian(target.Slice(FormatVersionOffset, sizeof(ushort)), FormatVersion);
        BinaryPrimitives.WriteUInt16LittleEndian(target.Slice(HeaderSizeOffset, sizeof(ushort)), HeaderSize);
        BinaryPrimitives.WriteUInt16LittleEndian(target.Slice(ItemCountOffset, sizeof(ushort)), checked((ushort)keys.Length));
        BinaryPrimitives.WriteUInt16LittleEndian(target.Slice(CapacityOffset, sizeof(ushort)), Capacity);
        for (int i = 0; i < keys.Length; i++)
        {
            WriteFingerprintAt(target, i, ComputeFingerprint(keys[i]));
            WriteKeyAt(target, i, keys[i]);
        }
    }

    /// <summary>
    /// Validates the routed shelf's fixed descriptor and bounded live-key count.<br/>
    /// Membership uniqueness and router ownership are topology diagnostics rather than repeated hot-path header work.<br/>
    /// </summary>
    /// <param name="source">The complete persisted shelf page.<br/></param>
    /// <returns><see langword="true"/> when the fixed fields and live count are valid.<br/></returns>
    public static bool IsValid(ReadOnlySpan<byte> source) =>
        source.Length >= Size &&
        BinaryPrimitives.ReadUInt32LittleEndian(source.Slice(MagicOffset, sizeof(uint))) == Magic &&
        BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(FormatVersionOffset, sizeof(ushort))) == FormatVersion &&
        BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(HeaderSizeOffset, sizeof(ushort))) == HeaderSize &&
        BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(CapacityOffset, sizeof(ushort))) == Capacity &&
        ReadItemCount(source) <= Capacity;

    /// <summary>
    /// Reads the number of live end-filled keys in a validated routed shelf.<br/>
    /// </summary>
    /// <param name="source">The persisted routed-shelf bytes.<br/></param>
    /// <returns>The live key count.<br/></returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ushort ReadItemCount(ReadOnlySpan<byte> source) =>
        BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(ItemCountOffset, sizeof(ushort)));

    /// <summary>
    /// Reads one UInt64 key from its physical routed-shelf ordinal.<br/>
    /// The encoding is explicitly little-endian because leaf bytes are equality payload rather than lexicographically ordered key bytes.<br/>
    /// </summary>
    /// <param name="source">The persisted routed-shelf bytes.<br/></param>
    /// <param name="index">The zero-based live physical ordinal.<br/></param>
    /// <returns>The decoded UInt64 key.<br/></returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong ReadKeyAt(ReadOnlySpan<byte> source, int index) =>
        BinaryPrimitives.ReadUInt64LittleEndian(source.Slice(KeysOffset + (index * KeySize), KeySize));

    /// <summary>
    /// Locates one exact key in a routed shelf without allocating or constructing an ordering side structure.<br/>
    /// A packed four-bit fingerprint pass rejects fifteen sixteenths of uniformly distributed candidates, after which the persisted UInt64 payload supplies the required exact equality proof.<br/>
    /// </summary>
    /// <param name="source">The validated routed-shelf bytes.<br/></param>
    /// <param name="key">The exact key to locate.<br/></param>
    /// <returns>The physical live ordinal, or minus one when absent.<br/></returns>
    public static int IndexOf(ReadOnlySpan<byte> source, ulong key)
    {
        int count = ReadItemCount(source);
        byte fingerprint = ComputeFingerprint(key);
        ReadOnlySpan<byte> fingerprints = source.Slice(FingerprintsOffset, FingerprintByteCount);
        byte repeatedFingerprint = checked((byte)(fingerprint | (fingerprint << FingerprintBits)));
        int completeFingerprintBytes = count / FingerprintsPerByte;
        int byteIndex = 0;
        if (Avx2.IsSupported && completeFingerprintBytes >= Vector256<byte>.Count)
        {
            Vector256<byte> expected = Vector256.Create(repeatedFingerprint);
            Vector256<byte> lowMask = Vector256.Create(FingerprintMask);
            Vector256<byte> highMask = Vector256.Create(checked((byte)(FingerprintMask << FingerprintBits)));
            ref byte firstFingerprint = ref MemoryMarshal.GetReference(fingerprints);
            int vectorEnd = completeFingerprintBytes - (completeFingerprintBytes % Vector256<byte>.Count);
            for (; byteIndex < vectorEnd; byteIndex += Vector256<byte>.Count)
            {
                Vector256<byte> differences = Avx2.Xor(
                    Vector256.LoadUnsafe(ref firstFingerprint, checked((nuint)byteIndex)),
                    expected);
                uint lowCandidates = unchecked((uint)Avx2.MoveMask(
                    Avx2.CompareEqual(Avx2.And(differences, lowMask), Vector256<byte>.Zero).AsSByte()));
                while (lowCandidates != 0)
                {
                    int lane = BitOperations.TrailingZeroCount(lowCandidates);
                    int candidateIndex = (byteIndex + lane) * FingerprintsPerByte;
                    if (ReadKeyAt(source, candidateIndex) == key)
                        return candidateIndex;
                    lowCandidates &= lowCandidates - 1;
                }

                uint highCandidates = unchecked((uint)Avx2.MoveMask(
                    Avx2.CompareEqual(Avx2.And(differences, highMask), Vector256<byte>.Zero).AsSByte()));
                while (highCandidates != 0)
                {
                    int lane = BitOperations.TrailingZeroCount(highCandidates);
                    int candidateIndex = ((byteIndex + lane) * FingerprintsPerByte) + 1;
                    if (ReadKeyAt(source, candidateIndex) == key)
                        return candidateIndex;
                    highCandidates &= highCandidates - 1;
                }
            }
        }

        for (; byteIndex < completeFingerprintBytes; byteIndex++)
        {
            int differences = fingerprints[byteIndex] ^ repeatedFingerprint;
            int candidateIndex = byteIndex * FingerprintsPerByte;
            if ((differences & FingerprintMask) == 0 && ReadKeyAt(source, candidateIndex) == key)
                return candidateIndex;
            if ((differences >> FingerprintBits) == 0 && ReadKeyAt(source, candidateIndex + 1) == key)
                return candidateIndex + 1;
        }

        for (int index = completeFingerprintBytes * FingerprintsPerByte; index < count; index++)
        {
            if (ReadFingerprintAt(source, index) == fingerprint && ReadKeyAt(source, index) == key)
                return index;
        }
        return -1;
    }

    /// <summary>
    /// Appends one already-proven-absent key after the final live physical entry.<br/>
    /// This is the defining routed-leaf mutation: no existing key or slot ordinal moves.<br/>
    /// </summary>
    /// <param name="target">The complete writable routed-shelf image.<br/></param>
    /// <param name="key">The absent key to append.<br/></param>
    public static void AppendAbsent(Span<byte> target, ulong key)
    {
        int count = ReadItemCount(target);
        if (count >= Capacity)
            throw new InvalidOperationException("The UInt64 routed-set shelf is full.");
        WriteFingerprintAt(target, count, ComputeFingerprint(key));
        WriteKeyAt(target, count, key);
        BinaryPrimitives.WriteUInt16LittleEndian(target.Slice(ItemCountOffset, sizeof(ushort)), checked((ushort)(count + 1)));
    }

    /// <summary>
    /// Removes one known routed-shelf ordinal by moving the final live key into the vacated position.<br/>
    /// The released trailing bytes are cleared so diagnostics cannot interpret stale payload as live state.<br/>
    /// </summary>
    /// <param name="target">The complete writable routed-shelf image.<br/></param>
    /// <param name="removeIndex">The zero-based live physical ordinal to remove.<br/></param>
    public static void RemoveAt(Span<byte> target, int removeIndex)
    {
        int count = ReadItemCount(target);
        if ((uint)removeIndex >= (uint)count)
            throw new ArgumentOutOfRangeException(nameof(removeIndex));
        int finalIndex = count - 1;
        if (removeIndex != finalIndex)
        {
            WriteFingerprintAt(target, removeIndex, ReadFingerprintAt(target, finalIndex));
            WriteKeyAt(target, removeIndex, ReadKeyAt(target, finalIndex));
        }
        WriteFingerprintAt(target, finalIndex, 0);
        target.Slice(KeysOffset + (finalIndex * KeySize), KeySize).Clear();
        BinaryPrimitives.WriteUInt16LittleEndian(target.Slice(ItemCountOffset, sizeof(ushort)), checked((ushort)finalIndex));
    }

    /// <summary>
    /// Resolves the persisted byte offset containing one key ordinal's packed four-bit fingerprint.<br/>
    /// Mutation publication uses this result to stage only the shared fingerprint byte affected by an append, move, or release.<br/>
    /// </summary>
    /// <param name="index">The zero-based physical key ordinal.<br/></param>
    /// <returns>The page-relative byte offset containing the requested fingerprint.<br/></returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetFingerprintStorageOffset(int index) =>
        FingerprintsOffset + (index / FingerprintsPerByte);

    /// <summary>
    /// Writes one UInt64 equality payload at a physical routed-shelf ordinal.<br/>
    /// </summary>
    /// <param name="target">The writable routed-shelf bytes.<br/></param>
    /// <param name="index">The zero-based physical ordinal.<br/></param>
    /// <param name="key">The UInt64 key to encode.<br/></param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WriteKeyAt(Span<byte> target, int index, ulong key) =>
        BinaryPrimitives.WriteUInt64LittleEndian(target.Slice(KeysOffset + (index * KeySize), KeySize), key);

    /// <summary>
    /// Writes one precomputed equality fingerprint into the packed four-bit lane paired with a routed key.<br/>
    /// Neighboring fingerprints sharing the same physical byte are preserved.<br/>
    /// </summary>
    /// <param name="target">The writable routed-shelf bytes.<br/></param>
    /// <param name="index">The zero-based physical ordinal.<br/></param>
    /// <param name="fingerprint">The stable four-bit key fingerprint.<br/></param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WriteFingerprintAt(Span<byte> target, int index, byte fingerprint)
    {
        int byteOffset = GetFingerprintStorageOffset(index);
        int shift = (index % FingerprintsPerByte) * FingerprintBits;
        int shiftedMask = FingerprintMask << shift;
        target[byteOffset] = checked((byte)((target[byteOffset] & ~shiftedMask) | ((fingerprint & FingerprintMask) << shift)));
    }

    /// <summary>
    /// Reads one packed four-bit candidate fingerprint from its physical key ordinal.<br/>
    /// The value is only a rejection hint; exact membership always compares the corresponding UInt64 payload.<br/>
    /// </summary>
    /// <param name="source">The persisted routed-shelf bytes.<br/></param>
    /// <param name="index">The zero-based physical key ordinal.<br/></param>
    /// <returns>The four-bit candidate fingerprint.<br/></returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte ReadFingerprintAt(ReadOnlySpan<byte> source, int index)
    {
        int shift = (index % FingerprintsPerByte) * FingerprintBits;
        return checked((byte)((source[GetFingerprintStorageOffset(index)] >> shift) & FingerprintMask));
    }

    /// <summary>
    /// Computes the stable four-bit routed-leaf candidate fingerprint for a UInt64 key.<br/>
    /// SplitMix finalization diffuses every input bit before truncation so sequential, prefixed, and full-domain keys distribute comparably across the sixteen candidate values.<br/>
    /// The fingerprint is never accepted as membership proof; an equal fingerprint only permits an exact full-key comparison.<br/>
    /// </summary>
    /// <param name="key">The UInt64 key bits.<br/></param>
    /// <returns>The persisted four-bit candidate fingerprint.<br/></returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte ComputeFingerprint(ulong key)
    {
        key ^= key >> 30;
        key *= 0xBF58_476D_1CE4_E5B9UL;
        key ^= key >> 27;
        key *= 0x94D0_49BB_1331_11EBUL;
        key ^= key >> 31;
        return checked((byte)(unchecked((byte)key) & FingerprintMask));
    }
}
