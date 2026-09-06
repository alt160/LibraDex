using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace LibraDex;

/// <summary>
/// Provides non-generic key operations for an index obtained through <see cref="IIndex"/>.<br/>
/// </summary>
public readonly partial struct LibraDexIndexKeys
{
    private readonly IIndex index;

    internal LibraDexIndexKeys(IIndex index)
    {
        this.index = index;
    }

    /// <summary>
    /// Determines whether at least one entry is stored under the supplied runtime key.<br/>
    /// </summary>
    public bool Exists(object? key)
        => LibraDexExistenceExecution.KeyExists(index, key);

    /// <summary>
    /// Materializes distinct raw key bytes in physical index traversal order.<br/>
    /// Returned buffers are caller-owned; duplicate identities under the same key do not repeat that key in the collection.<br/>
    /// </summary>
    public IReadOnlyList<byte[]?> Get()
        => LibraDexDuplicateExecution.DistinctRawKeys(index);

    /// <summary>
    /// Projects exact key bytes as strings before collection or duplicate analysis.<br/>
    /// A declared string index uses its LibraDex string marker contract; arbitrary binary keys use UTF-8 over the selected bytes.<br/>
    /// </summary>
    public LibraDexProjectedIndexKeys<string?> AsString()
        => new(index, LibraDexKeyProjection.String());

    /// <summary>
    /// Projects a maintained folded string subindex before collection or duplicate analysis.<br/>
    /// Sort-key bytes are not reversible strings; use <see cref="AsSortKey"/> when binary sort-key values are required.<br/>
    /// </summary>
    /// <param name="subIndex">The maintained Folded or Normalized string subindex; SortKey is binary and is selected through <see cref="AsSortKey"/>.<br/></param>
    /// <returns>A projected string-key stage.<br/></returns>
    public LibraDexProjectedIndexKeys<string?> AsString(SubIndexType subIndex)
    {
        if (subIndex is not (SubIndexType.Folded or SubIndexType.Normalized))
            throw new ArgumentOutOfRangeException(nameof(subIndex), subIndex, "Sort-key bytes are not reversible strings. Use Keys.AsSortKey for binary sort-key values.");
        return new LibraDexProjectedIndexKeys<string?>(index, LibraDexKeyProjection.String(subIndex));
    }

    /// <summary>
    /// Projects a maintained string sort-key subindex as its natural binary values before collection or duplicate analysis.<br/>
    /// </summary>
    public LibraDexProjectedIndexKeys<byte[]> AsSortKey => new(index, LibraDexKeyProjection.SortKey());

    /// <summary>
    /// Projects complete developer-owned key bytes as a little-endian Int32.<br/>
    /// Use the overload accepting <see cref="Coercion.Numeric"/> when the bytes deliberately use LibraDex canonical numeric encoding.<br/>
    /// </summary>
    /// <returns>A projected Int32-key stage.<br/></returns>
    public LibraDexProjectedIndexKeys<int> AsInt32()
        => AsInt32(Coercion.Numeric.DotNet);

    /// <summary>
    /// Projects complete key bytes as an Int32 using the selected numeric coercion.<br/>
    /// Typed Int32 indexes already imply canonical encoding; this overload is intended for untyped or developer-owned binary keys.<br/>
    /// </summary>
    /// <param name="coercion">The numeric byte interpretation to apply.<br/></param>
    /// <returns>A projected Int32-key stage.<br/></returns>
    public LibraDexProjectedIndexKeys<int> AsInt32(Coercion.Numeric coercion)
        => new(index, LibraDexKeyProjection.Numeric<int>(coercion));

    /// <summary>Projects complete key bytes as Byte using ordinary .NET layout.<br/></summary>
    public LibraDexProjectedIndexKeys<byte> AsByte() => AsByte(Coercion.Numeric.DotNet);
    /// <summary>Projects complete key bytes as Byte using the selected numeric coercion.<br/></summary>
    public LibraDexProjectedIndexKeys<byte> AsByte(Coercion.Numeric coercion) => Numeric<byte>(coercion);
    /// <summary>Projects complete key bytes as SByte using ordinary .NET layout.<br/></summary>
    public LibraDexProjectedIndexKeys<sbyte> AsSByte() => AsSByte(Coercion.Numeric.DotNet);
    /// <summary>Projects complete key bytes as SByte using the selected numeric coercion.<br/></summary>
    public LibraDexProjectedIndexKeys<sbyte> AsSByte(Coercion.Numeric coercion) => Numeric<sbyte>(coercion);
    /// <summary>Projects complete key bytes as Int16 using ordinary .NET layout.<br/></summary>
    public LibraDexProjectedIndexKeys<short> AsInt16() => AsInt16(Coercion.Numeric.DotNet);
    /// <summary>Projects complete key bytes as Int16 using the selected numeric coercion.<br/></summary>
    public LibraDexProjectedIndexKeys<short> AsInt16(Coercion.Numeric coercion) => Numeric<short>(coercion);
    /// <summary>Projects complete key bytes as UInt16 using ordinary .NET layout.<br/></summary>
    public LibraDexProjectedIndexKeys<ushort> AsUInt16() => AsUInt16(Coercion.Numeric.DotNet);
    /// <summary>Projects complete key bytes as UInt16 using the selected numeric coercion.<br/></summary>
    public LibraDexProjectedIndexKeys<ushort> AsUInt16(Coercion.Numeric coercion) => Numeric<ushort>(coercion);
    /// <summary>Projects complete key bytes as UInt32 using ordinary .NET layout.<br/></summary>
    public LibraDexProjectedIndexKeys<uint> AsUInt32() => AsUInt32(Coercion.Numeric.DotNet);
    /// <summary>Projects complete key bytes as UInt32 using the selected numeric coercion.<br/></summary>
    public LibraDexProjectedIndexKeys<uint> AsUInt32(Coercion.Numeric coercion) => Numeric<uint>(coercion);
    /// <summary>Projects complete key bytes as Int64 using ordinary .NET layout.<br/></summary>
    public LibraDexProjectedIndexKeys<long> AsInt64() => AsInt64(Coercion.Numeric.DotNet);
    /// <summary>Projects complete key bytes as Int64 using the selected numeric coercion.<br/></summary>
    public LibraDexProjectedIndexKeys<long> AsInt64(Coercion.Numeric coercion) => Numeric<long>(coercion);
    /// <summary>Projects complete key bytes as UInt64 using ordinary .NET layout.<br/></summary>
    public LibraDexProjectedIndexKeys<ulong> AsUInt64() => AsUInt64(Coercion.Numeric.DotNet);
    /// <summary>Projects complete key bytes as UInt64 using the selected numeric coercion.<br/></summary>
    public LibraDexProjectedIndexKeys<ulong> AsUInt64(Coercion.Numeric coercion) => Numeric<ulong>(coercion);
    /// <summary>Projects complete key bytes as Int128 using ordinary .NET layout and permits checked widening from shorter integral sources.<br/></summary>
    public LibraDexProjectedIndexKeys<Int128> AsInt128() => AsInt128(Coercion.Numeric.DotNet);
    /// <summary>Projects complete key bytes as Int128 using the selected numeric coercion and checked width conversion.<br/></summary>
    public LibraDexProjectedIndexKeys<Int128> AsInt128(Coercion.Numeric coercion) => Numeric<Int128>(coercion);
    /// <summary>Projects complete key bytes as UInt128 using ordinary .NET layout and permits checked widening from shorter integral sources.<br/></summary>
    public LibraDexProjectedIndexKeys<UInt128> AsUInt128() => AsUInt128(Coercion.Numeric.DotNet);
    /// <summary>Projects complete key bytes as UInt128 using the selected numeric coercion and checked width conversion.<br/></summary>
    public LibraDexProjectedIndexKeys<UInt128> AsUInt128(Coercion.Numeric coercion) => Numeric<UInt128>(coercion);
    /// <summary>Projects complete key bytes as Single using ordinary .NET layout.<br/></summary>
    public LibraDexProjectedIndexKeys<float> AsSingle() => AsSingle(Coercion.Numeric.DotNet);
    /// <summary>Projects complete key bytes as Single using the selected numeric coercion.<br/></summary>
    public LibraDexProjectedIndexKeys<float> AsSingle(Coercion.Numeric coercion) => Numeric<float>(coercion);
    /// <summary>Projects complete key bytes as Double using ordinary .NET layout.<br/></summary>
    public LibraDexProjectedIndexKeys<double> AsDouble() => AsDouble(Coercion.Numeric.DotNet);
    /// <summary>Projects complete key bytes as Double using the selected numeric coercion.<br/></summary>
    public LibraDexProjectedIndexKeys<double> AsDouble(Coercion.Numeric coercion) => Numeric<double>(coercion);
    /// <summary>Projects complete key bytes as Decimal using ordinary .NET layout.<br/></summary>
    public LibraDexProjectedIndexKeys<decimal> AsDecimal() => AsDecimal(Coercion.Numeric.DotNet);
    /// <summary>Projects complete key bytes as Decimal using the selected numeric coercion.<br/></summary>
    public LibraDexProjectedIndexKeys<decimal> AsDecimal(Coercion.Numeric coercion) => Numeric<decimal>(coercion);
    /// <summary>Projects complete key bytes as BigInteger using ordinary .NET layout.<br/></summary>
    public LibraDexProjectedIndexKeys<BigInteger> AsBigInt() => AsBigInt(Coercion.Numeric.DotNet);
    /// <summary>Projects complete key bytes as BigInteger using the selected numeric coercion.<br/></summary>
    public LibraDexProjectedIndexKeys<BigInteger> AsBigInt(Coercion.Numeric coercion) => Numeric<BigInteger>(coercion);
    /// <summary>Projects complete key bytes as Char using ordinary .NET integral layout.<br/></summary>
    public LibraDexProjectedIndexKeys<char> AsChar() => AsChar(Coercion.Numeric.DotNet);
    /// <summary>Projects complete key bytes as Char using the selected numeric coercion.<br/></summary>
    public LibraDexProjectedIndexKeys<char> AsChar(Coercion.Numeric coercion) => Numeric<char>(coercion);

    private LibraDexProjectedIndexKeys<TValue> Numeric<TValue>(Coercion.Numeric coercion)
        => new(index, LibraDexKeyProjection.Numeric<TValue>(coercion));

    /// <summary>Projects complete key bytes as DateTime using the explicitly selected physical representation.<br/></summary>
    public LibraDexProjectedIndexKeys<DateTime> AsDateTime(Coercion.DateTime coercion) => Temporal<DateTime>(coercion);
    /// <summary>Projects complete key bytes as DateTimeOffset using the explicitly selected physical representation.<br/></summary>
    public LibraDexProjectedIndexKeys<DateTimeOffset> AsDateTimeOffset(Coercion.DateTimeOffset coercion) => Temporal<DateTimeOffset>(coercion);
    /// <summary>Projects complete key bytes as DateOnly using the explicitly selected physical representation.<br/></summary>
    public LibraDexProjectedIndexKeys<DateOnly> AsDateOnly(Coercion.DateOnly coercion) => Temporal<DateOnly>(coercion);
    /// <summary>Projects complete key bytes as TimeOnly using the explicitly selected physical representation.<br/></summary>
    public LibraDexProjectedIndexKeys<TimeOnly> AsTimeOnly(Coercion.TimeOnly coercion) => Temporal<TimeOnly>(coercion);
    /// <summary>Projects complete key bytes as TimeSpan using the explicitly selected physical representation.<br/></summary>
    public LibraDexProjectedIndexKeys<TimeSpan> AsTimeSpan(Coercion.TimeSpan coercion) => Temporal<TimeSpan>(coercion);

    private LibraDexProjectedIndexKeys<TValue> Temporal<TValue>(object coercion)
        => new(index, LibraDexKeyProjection.Temporal<TValue>(coercion));

    /// <summary>
    /// Selects a bounded byte slice before choosing its returned or duplicate-equality projection.<br/>
    /// </summary>
    /// <param name="offset">The zero-based byte offset inside each raw key.<br/></param>
    /// <param name="length">The required slice length in bytes.<br/></param>
    /// <returns>A slice projection selector.<br/></returns>
    public LibraDexIndexKeySlice Slice(int offset, int length)
        => new(index, offset, length);

    /// <summary>
    /// Gets duplicate-key collection and reader operations using exact physical key bytes.<br/>
    /// </summary>
    public LibraDexUntypedKeyDuplicates Duplicates => new(index);

    /// <summary>
    /// Gets singleton-key collection and reader operations using exact physical key bytes.<br/>
    /// A singleton key is associated with exactly one distinct identity; repeated storage of the same exact tuple does not change that cardinality.<br/>
    /// </summary>
    public LibraDexUntypedKeySingletons Singletons => new(index);
}

/// <summary>
/// Provides non-generic identity operations for an index obtained through <see cref="IIndex"/>.<br/>
/// </summary>
public readonly partial struct LibraDexIndexIdentities
{
    private readonly IIndex index;

    internal LibraDexIndexIdentities(IIndex index)
    {
        this.index = index;
    }

    /// <summary>
    /// Determines whether the supplied runtime identity has at least one entry in this index.<br/>
    /// </summary>
    public bool Exists(object identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        return LibraDexExistenceExecution.IdentityExists(index, identity);
    }

    /// <summary>
    /// Gets duplicate-identity collection and reader operations using runtime identity values.<br/>
    /// </summary>
    public LibraDexUntypedIdentityDuplicates Duplicates => new(index);

    /// <summary>
    /// Gets singleton-identity collection and reader operations using runtime identity values.<br/>
    /// A singleton identity is associated with exactly one distinct key.<br/>
    /// </summary>
    public LibraDexUntypedIdentitySingletons Singletons => new(index);
}

/// <summary>
/// Provides non-generic exact-entry operations for an index obtained through <see cref="IIndex"/>.<br/>
/// </summary>
public readonly partial struct LibraDexIndexEntries
{
    private readonly IIndex index;

    internal LibraDexIndexEntries(IIndex index)
    {
        this.index = index;
    }

    /// <summary>
    /// Determines whether the supplied runtime key/identity association exists.<br/>
    /// </summary>
    public bool Exists(object? key, object identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        return LibraDexExistenceExecution.EntryExists(index, key, identity);
    }

    /// <summary>
    /// Gets complete runtime entries selected by duplicated key.<br/>
    /// </summary>
    public LibraDexUntypedEntriesByDuplicateKey DuplicateKeys => new(index);

    /// <summary>
    /// Gets complete runtime entries selected by duplicated identity.<br/>
    /// </summary>
    public LibraDexUntypedEntriesByDuplicateIdentity DuplicateIdentities => new(index);

    /// <summary>
    /// Gets complete runtime entries selected by keys associated with exactly one distinct identity.<br/>
    /// </summary>
    public LibraDexUntypedEntriesBySingletonKey SingletonKeys => new(index);

    /// <summary>
    /// Gets complete runtime entries selected by identities associated with exactly one distinct key.<br/>
    /// </summary>
    public LibraDexUntypedEntriesBySingletonIdentity SingletonIdentities => new(index);
}

/// <summary>
/// Provides non-generic duplicate-key results for an <see cref="IIndex"/>.<br/>
/// </summary>
public readonly struct LibraDexUntypedKeyDuplicates
{
    private readonly IIndex index;

    internal LibraDexUntypedKeyDuplicates(IIndex index)
    {
        this.index = index;
    }

    /// <summary>
    /// Materializes distinct raw key bytes associated with at least two distinct identities.<br/>
    /// Detection uses exact physical key equality and does not decode every candidate into its catalog-declared CLR key type.<br/>
    /// </summary>
    public IReadOnlyList<byte[]?> Get()
        => LibraDexDuplicateExecution.IterateDuplicateRawKeys(index).ToList();

    /// <summary>
    /// Opens a forward-only reader over distinct raw keys associated with multiple identities.<br/>
    /// </summary>
    public LibraDexDuplicateReader<byte[]?> OpenReader()
        => new(LibraDexDuplicateExecution.IterateDuplicateRawKeys(index));

    /// <summary>
    /// Projects exact duplicate key bytes as strings after duplicate selection.<br/>
    /// The projection changes only the returned representation; duplicate equality remains exact physical byte equality.<br/>
    /// </summary>
    public LibraDexProjectedDuplicateKeys<string?> AsString
        => new(index, LibraDexKeyProjection.String(), duplicatesFirst: true);

    /// <summary>
    /// Projects exact duplicate key bytes as a little-endian Int32 after duplicate selection.<br/>
    /// </summary>
    /// <returns>A projected duplicate-result stage.<br/></returns>
    public LibraDexProjectedDuplicateKeys<int> AsInt32()
        => AsInt32(Coercion.Numeric.DotNet);

    /// <summary>
    /// Projects exact duplicate key bytes as an Int32 using the selected numeric coercion after duplicate selection.<br/>
    /// </summary>
    /// <param name="coercion">The numeric byte interpretation to apply.<br/></param>
    /// <returns>A projected duplicate-result stage.<br/></returns>
    public LibraDexProjectedDuplicateKeys<int> AsInt32(Coercion.Numeric coercion)
        => new(index, LibraDexKeyProjection.Numeric<int>(coercion), duplicatesFirst: true);

    /// <summary>Projects duplicate key bytes as Byte using ordinary .NET layout.<br/></summary>
    public LibraDexProjectedDuplicateKeys<byte> AsByte() => AsByte(Coercion.Numeric.DotNet);
    /// <summary>Projects duplicate key bytes as Byte using the selected numeric coercion.<br/></summary>
    public LibraDexProjectedDuplicateKeys<byte> AsByte(Coercion.Numeric coercion) => Numeric<byte>(coercion);
    /// <summary>Projects duplicate key bytes as SByte using ordinary .NET layout.<br/></summary>
    public LibraDexProjectedDuplicateKeys<sbyte> AsSByte() => AsSByte(Coercion.Numeric.DotNet);
    /// <summary>Projects duplicate key bytes as SByte using the selected numeric coercion.<br/></summary>
    public LibraDexProjectedDuplicateKeys<sbyte> AsSByte(Coercion.Numeric coercion) => Numeric<sbyte>(coercion);
    /// <summary>Projects duplicate key bytes as Int16 using ordinary .NET layout.<br/></summary>
    public LibraDexProjectedDuplicateKeys<short> AsInt16() => AsInt16(Coercion.Numeric.DotNet);
    /// <summary>Projects duplicate key bytes as Int16 using the selected numeric coercion.<br/></summary>
    public LibraDexProjectedDuplicateKeys<short> AsInt16(Coercion.Numeric coercion) => Numeric<short>(coercion);
    /// <summary>Projects duplicate key bytes as UInt16 using ordinary .NET layout.<br/></summary>
    public LibraDexProjectedDuplicateKeys<ushort> AsUInt16() => AsUInt16(Coercion.Numeric.DotNet);
    /// <summary>Projects duplicate key bytes as UInt16 using the selected numeric coercion.<br/></summary>
    public LibraDexProjectedDuplicateKeys<ushort> AsUInt16(Coercion.Numeric coercion) => Numeric<ushort>(coercion);
    /// <summary>Projects duplicate key bytes as UInt32 using ordinary .NET layout.<br/></summary>
    public LibraDexProjectedDuplicateKeys<uint> AsUInt32() => AsUInt32(Coercion.Numeric.DotNet);
    /// <summary>Projects duplicate key bytes as UInt32 using the selected numeric coercion.<br/></summary>
    public LibraDexProjectedDuplicateKeys<uint> AsUInt32(Coercion.Numeric coercion) => Numeric<uint>(coercion);
    /// <summary>Projects duplicate key bytes as Int64 using ordinary .NET layout.<br/></summary>
    public LibraDexProjectedDuplicateKeys<long> AsInt64() => AsInt64(Coercion.Numeric.DotNet);
    /// <summary>Projects duplicate key bytes as Int64 using the selected numeric coercion.<br/></summary>
    public LibraDexProjectedDuplicateKeys<long> AsInt64(Coercion.Numeric coercion) => Numeric<long>(coercion);
    /// <summary>Projects duplicate key bytes as UInt64 using ordinary .NET layout.<br/></summary>
    public LibraDexProjectedDuplicateKeys<ulong> AsUInt64() => AsUInt64(Coercion.Numeric.DotNet);
    /// <summary>Projects duplicate key bytes as UInt64 using the selected numeric coercion.<br/></summary>
    public LibraDexProjectedDuplicateKeys<ulong> AsUInt64(Coercion.Numeric coercion) => Numeric<ulong>(coercion);
    /// <summary>Projects duplicate key bytes as Int128 and permits checked widening from shorter integral sources.<br/></summary>
    public LibraDexProjectedDuplicateKeys<Int128> AsInt128() => AsInt128(Coercion.Numeric.DotNet);
    /// <summary>Projects duplicate key bytes as Int128 using the selected numeric coercion.<br/></summary>
    public LibraDexProjectedDuplicateKeys<Int128> AsInt128(Coercion.Numeric coercion) => Numeric<Int128>(coercion);
    /// <summary>Projects duplicate key bytes as UInt128 and permits checked widening from shorter integral sources.<br/></summary>
    public LibraDexProjectedDuplicateKeys<UInt128> AsUInt128() => AsUInt128(Coercion.Numeric.DotNet);
    /// <summary>Projects duplicate key bytes as UInt128 using the selected numeric coercion.<br/></summary>
    public LibraDexProjectedDuplicateKeys<UInt128> AsUInt128(Coercion.Numeric coercion) => Numeric<UInt128>(coercion);
    /// <summary>Projects duplicate key bytes as Single using ordinary .NET layout.<br/></summary>
    public LibraDexProjectedDuplicateKeys<float> AsSingle() => AsSingle(Coercion.Numeric.DotNet);
    /// <summary>Projects duplicate key bytes as Single using the selected numeric coercion.<br/></summary>
    public LibraDexProjectedDuplicateKeys<float> AsSingle(Coercion.Numeric coercion) => Numeric<float>(coercion);
    /// <summary>Projects duplicate key bytes as Double using ordinary .NET layout.<br/></summary>
    public LibraDexProjectedDuplicateKeys<double> AsDouble() => AsDouble(Coercion.Numeric.DotNet);
    /// <summary>Projects duplicate key bytes as Double using the selected numeric coercion.<br/></summary>
    public LibraDexProjectedDuplicateKeys<double> AsDouble(Coercion.Numeric coercion) => Numeric<double>(coercion);
    /// <summary>Projects duplicate key bytes as Decimal using ordinary .NET layout.<br/></summary>
    public LibraDexProjectedDuplicateKeys<decimal> AsDecimal() => AsDecimal(Coercion.Numeric.DotNet);
    /// <summary>Projects duplicate key bytes as Decimal using the selected numeric coercion.<br/></summary>
    public LibraDexProjectedDuplicateKeys<decimal> AsDecimal(Coercion.Numeric coercion) => Numeric<decimal>(coercion);
    /// <summary>Projects duplicate key bytes as BigInteger using ordinary .NET layout.<br/></summary>
    public LibraDexProjectedDuplicateKeys<BigInteger> AsBigInt() => AsBigInt(Coercion.Numeric.DotNet);
    /// <summary>Projects duplicate key bytes as BigInteger using the selected numeric coercion.<br/></summary>
    public LibraDexProjectedDuplicateKeys<BigInteger> AsBigInt(Coercion.Numeric coercion) => Numeric<BigInteger>(coercion);
    /// <summary>Projects duplicate key bytes as Char using ordinary .NET integral layout.<br/></summary>
    public LibraDexProjectedDuplicateKeys<char> AsChar() => AsChar(Coercion.Numeric.DotNet);
    /// <summary>Projects duplicate key bytes as Char using the selected numeric coercion.<br/></summary>
    public LibraDexProjectedDuplicateKeys<char> AsChar(Coercion.Numeric coercion) => Numeric<char>(coercion);

    private LibraDexProjectedDuplicateKeys<TValue> Numeric<TValue>(Coercion.Numeric coercion)
        => new(index, LibraDexKeyProjection.Numeric<TValue>(coercion), duplicatesFirst: true);

    /// <summary>Projects duplicate key bytes as DateTime using the explicitly selected physical representation.<br/></summary>
    public LibraDexProjectedDuplicateKeys<DateTime> AsDateTime(Coercion.DateTime coercion) => Temporal<DateTime>(coercion);
    /// <summary>Projects duplicate key bytes as DateTimeOffset using the explicitly selected physical representation.<br/></summary>
    public LibraDexProjectedDuplicateKeys<DateTimeOffset> AsDateTimeOffset(Coercion.DateTimeOffset coercion) => Temporal<DateTimeOffset>(coercion);
    /// <summary>Projects duplicate key bytes as DateOnly using the explicitly selected physical representation.<br/></summary>
    public LibraDexProjectedDuplicateKeys<DateOnly> AsDateOnly(Coercion.DateOnly coercion) => Temporal<DateOnly>(coercion);
    /// <summary>Projects duplicate key bytes as TimeOnly using the explicitly selected physical representation.<br/></summary>
    public LibraDexProjectedDuplicateKeys<TimeOnly> AsTimeOnly(Coercion.TimeOnly coercion) => Temporal<TimeOnly>(coercion);
    /// <summary>Projects duplicate key bytes as TimeSpan using the explicitly selected physical representation.<br/></summary>
    public LibraDexProjectedDuplicateKeys<TimeSpan> AsTimeSpan(Coercion.TimeSpan coercion) => Temporal<TimeSpan>(coercion);

    private LibraDexProjectedDuplicateKeys<TValue> Temporal<TValue>(object coercion)
        => new(index, LibraDexKeyProjection.Temporal<TValue>(coercion), duplicatesFirst: true);

    /// <summary>
    /// Selects a bounded byte slice from each exact duplicate key before choosing its returned projection.<br/>
    /// Duplicate equality remains over the complete physical key because selection has already occurred.<br/>
    /// </summary>
    /// <param name="offset">The zero-based byte offset inside each duplicate key.<br/></param>
    /// <param name="length">The required slice length in bytes.<br/></param>
    /// <returns>A post-duplicate slice projection selector.<br/></returns>
    public LibraDexDuplicateKeySlice Slice(int offset, int length)
        => new(index, offset, length);
}

/// <summary>
/// Selects a byte range from untyped index keys before choosing a CLR projection.<br/>
/// The selected projection can define later duplicate equality because this stage is reached before `Duplicates`.<br/>
/// </summary>
public readonly struct LibraDexIndexKeySlice
{
    private readonly IIndex index;
    private readonly int offset;
    private readonly int length;

    internal LibraDexIndexKeySlice(IIndex index, int offset, int length)
    {
        if (offset < 0)
            throw new ArgumentOutOfRangeException(nameof(offset), offset, "A key-slice offset cannot be negative.");
        if (length <= 0)
            throw new ArgumentOutOfRangeException(nameof(length), length, "A key-slice length must be positive.");
        this.index = index;
        this.offset = offset;
        this.length = length;
    }

    /// <summary>
    /// Projects the selected bytes as UTF-8 text.<br/>
    /// </summary>
    public LibraDexProjectedIndexKeys<string?> AsString
        => new(index, LibraDexKeyProjection.String(offset, length));

    /// <summary>
    /// Projects the selected bytes as a little-endian Int32.<br/>
    /// </summary>
    /// <returns>A projected Int32-key stage.<br/></returns>
    public LibraDexProjectedIndexKeys<int> AsInt32()
        => AsInt32(Coercion.Numeric.DotNet);

    /// <summary>
    /// Projects the selected bytes as an Int32 using the selected numeric coercion.<br/>
    /// </summary>
    /// <param name="coercion">The numeric byte interpretation to apply.<br/></param>
    /// <returns>A projected Int32-key stage.<br/></returns>
    public LibraDexProjectedIndexKeys<int> AsInt32(Coercion.Numeric coercion)
        => new(index, LibraDexKeyProjection.Numeric<int>(coercion, offset, length));

    /// <summary>Projects the selected bytes as Byte using ordinary .NET layout.<br/></summary>
    public LibraDexProjectedIndexKeys<byte> AsByte() => AsByte(Coercion.Numeric.DotNet);
    /// <summary>Projects the selected bytes as Byte using the selected numeric coercion.<br/></summary>
    public LibraDexProjectedIndexKeys<byte> AsByte(Coercion.Numeric coercion) => Numeric<byte>(coercion);
    /// <summary>Projects the selected bytes as SByte using ordinary .NET layout.<br/></summary>
    public LibraDexProjectedIndexKeys<sbyte> AsSByte() => AsSByte(Coercion.Numeric.DotNet);
    /// <summary>Projects the selected bytes as SByte using the selected numeric coercion.<br/></summary>
    public LibraDexProjectedIndexKeys<sbyte> AsSByte(Coercion.Numeric coercion) => Numeric<sbyte>(coercion);
    /// <summary>Projects the selected bytes as Int16 using ordinary .NET layout.<br/></summary>
    public LibraDexProjectedIndexKeys<short> AsInt16() => AsInt16(Coercion.Numeric.DotNet);
    /// <summary>Projects the selected bytes as Int16 using the selected numeric coercion.<br/></summary>
    public LibraDexProjectedIndexKeys<short> AsInt16(Coercion.Numeric coercion) => Numeric<short>(coercion);
    /// <summary>Projects the selected bytes as UInt16 using ordinary .NET layout.<br/></summary>
    public LibraDexProjectedIndexKeys<ushort> AsUInt16() => AsUInt16(Coercion.Numeric.DotNet);
    /// <summary>Projects the selected bytes as UInt16 using the selected numeric coercion.<br/></summary>
    public LibraDexProjectedIndexKeys<ushort> AsUInt16(Coercion.Numeric coercion) => Numeric<ushort>(coercion);
    /// <summary>Projects the selected bytes as UInt32 using ordinary .NET layout.<br/></summary>
    public LibraDexProjectedIndexKeys<uint> AsUInt32() => AsUInt32(Coercion.Numeric.DotNet);
    /// <summary>Projects the selected bytes as UInt32 using the selected numeric coercion.<br/></summary>
    public LibraDexProjectedIndexKeys<uint> AsUInt32(Coercion.Numeric coercion) => Numeric<uint>(coercion);
    /// <summary>Projects the selected bytes as Int64 using ordinary .NET layout.<br/></summary>
    public LibraDexProjectedIndexKeys<long> AsInt64() => AsInt64(Coercion.Numeric.DotNet);
    /// <summary>Projects the selected bytes as Int64 using the selected numeric coercion.<br/></summary>
    public LibraDexProjectedIndexKeys<long> AsInt64(Coercion.Numeric coercion) => Numeric<long>(coercion);
    /// <summary>Projects the selected bytes as UInt64 using ordinary .NET layout.<br/></summary>
    public LibraDexProjectedIndexKeys<ulong> AsUInt64() => AsUInt64(Coercion.Numeric.DotNet);
    /// <summary>Projects the selected bytes as UInt64 using the selected numeric coercion.<br/></summary>
    public LibraDexProjectedIndexKeys<ulong> AsUInt64(Coercion.Numeric coercion) => Numeric<ulong>(coercion);
    /// <summary>Projects the selected bytes as Int128 with checked widening or narrowing.<br/></summary>
    public LibraDexProjectedIndexKeys<Int128> AsInt128() => AsInt128(Coercion.Numeric.DotNet);
    /// <summary>Projects the selected bytes as Int128 using the selected numeric coercion.<br/></summary>
    public LibraDexProjectedIndexKeys<Int128> AsInt128(Coercion.Numeric coercion) => Numeric<Int128>(coercion);
    /// <summary>Projects the selected bytes as UInt128 with checked widening or narrowing.<br/></summary>
    public LibraDexProjectedIndexKeys<UInt128> AsUInt128() => AsUInt128(Coercion.Numeric.DotNet);
    /// <summary>Projects the selected bytes as UInt128 using the selected numeric coercion.<br/></summary>
    public LibraDexProjectedIndexKeys<UInt128> AsUInt128(Coercion.Numeric coercion) => Numeric<UInt128>(coercion);
    /// <summary>Projects the selected bytes as Single using ordinary .NET layout.<br/></summary>
    public LibraDexProjectedIndexKeys<float> AsSingle() => AsSingle(Coercion.Numeric.DotNet);
    /// <summary>Projects the selected bytes as Single using the selected numeric coercion.<br/></summary>
    public LibraDexProjectedIndexKeys<float> AsSingle(Coercion.Numeric coercion) => Numeric<float>(coercion);
    /// <summary>Projects the selected bytes as Double using ordinary .NET layout.<br/></summary>
    public LibraDexProjectedIndexKeys<double> AsDouble() => AsDouble(Coercion.Numeric.DotNet);
    /// <summary>Projects the selected bytes as Double using the selected numeric coercion.<br/></summary>
    public LibraDexProjectedIndexKeys<double> AsDouble(Coercion.Numeric coercion) => Numeric<double>(coercion);
    /// <summary>Projects the selected bytes as Decimal using ordinary .NET layout.<br/></summary>
    public LibraDexProjectedIndexKeys<decimal> AsDecimal() => AsDecimal(Coercion.Numeric.DotNet);
    /// <summary>Projects the selected bytes as Decimal using the selected numeric coercion.<br/></summary>
    public LibraDexProjectedIndexKeys<decimal> AsDecimal(Coercion.Numeric coercion) => Numeric<decimal>(coercion);
    /// <summary>Projects the selected bytes as BigInteger using ordinary .NET layout.<br/></summary>
    public LibraDexProjectedIndexKeys<BigInteger> AsBigInt() => AsBigInt(Coercion.Numeric.DotNet);
    /// <summary>Projects the selected bytes as BigInteger using the selected numeric coercion.<br/></summary>
    public LibraDexProjectedIndexKeys<BigInteger> AsBigInt(Coercion.Numeric coercion) => Numeric<BigInteger>(coercion);
    /// <summary>Projects the selected bytes as Char using ordinary .NET integral layout.<br/></summary>
    public LibraDexProjectedIndexKeys<char> AsChar() => AsChar(Coercion.Numeric.DotNet);
    /// <summary>Projects the selected bytes as Char using the selected numeric coercion.<br/></summary>
    public LibraDexProjectedIndexKeys<char> AsChar(Coercion.Numeric coercion) => Numeric<char>(coercion);

    private LibraDexProjectedIndexKeys<TValue> Numeric<TValue>(Coercion.Numeric coercion)
        => new(index, LibraDexKeyProjection.Numeric<TValue>(coercion, offset, length));

    /// <summary>Projects the selected bytes as DateTime using the explicitly selected physical representation.<br/></summary>
    public LibraDexProjectedIndexKeys<DateTime> AsDateTime(Coercion.DateTime coercion) => Temporal<DateTime>(coercion);
    /// <summary>Projects the selected bytes as DateTimeOffset using the explicitly selected physical representation.<br/></summary>
    public LibraDexProjectedIndexKeys<DateTimeOffset> AsDateTimeOffset(Coercion.DateTimeOffset coercion) => Temporal<DateTimeOffset>(coercion);
    /// <summary>Projects the selected bytes as DateOnly using the explicitly selected physical representation.<br/></summary>
    public LibraDexProjectedIndexKeys<DateOnly> AsDateOnly(Coercion.DateOnly coercion) => Temporal<DateOnly>(coercion);
    /// <summary>Projects the selected bytes as TimeOnly using the explicitly selected physical representation.<br/></summary>
    public LibraDexProjectedIndexKeys<TimeOnly> AsTimeOnly(Coercion.TimeOnly coercion) => Temporal<TimeOnly>(coercion);
    /// <summary>Projects the selected bytes as TimeSpan using the explicitly selected physical representation.<br/></summary>
    public LibraDexProjectedIndexKeys<TimeSpan> AsTimeSpan(Coercion.TimeSpan coercion) => Temporal<TimeSpan>(coercion);

    private LibraDexProjectedIndexKeys<TValue> Temporal<TValue>(object coercion)
        => new(index, LibraDexKeyProjection.Temporal<TValue>(coercion, offset, length));
}

/// <summary>
/// Selects a byte range after exact duplicate physical keys have already been identified.<br/>
/// Projection changes only the returned representation and cannot alter which complete keys qualified as duplicates.<br/>
/// </summary>
public readonly struct LibraDexDuplicateKeySlice
{
    private readonly IIndex index;
    private readonly int offset;
    private readonly int length;

    internal LibraDexDuplicateKeySlice(IIndex index, int offset, int length)
    {
        if (offset < 0)
            throw new ArgumentOutOfRangeException(nameof(offset), offset, "A duplicate-key slice offset cannot be negative.");
        if (length <= 0)
            throw new ArgumentOutOfRangeException(nameof(length), length, "A duplicate-key slice length must be positive.");
        this.index = index;
        this.offset = offset;
        this.length = length;
    }

    /// <summary>
    /// Projects the selected bytes from each exact duplicate key as UTF-8 text.<br/>
    /// </summary>
    public LibraDexProjectedDuplicateKeys<string?> AsString
        => new(index, LibraDexKeyProjection.String(offset, length), duplicatesFirst: true);

    /// <summary>
    /// Projects the selected bytes from each exact duplicate key as a little-endian Int32.<br/>
    /// </summary>
    /// <returns>A projected duplicate-result stage.<br/></returns>
    public LibraDexProjectedDuplicateKeys<int> AsInt32()
        => AsInt32(Coercion.Numeric.DotNet);

    /// <summary>
    /// Projects the selected bytes from each exact duplicate key as an Int32 using the selected numeric coercion.<br/>
    /// </summary>
    /// <param name="coercion">The numeric byte interpretation to apply.<br/></param>
    /// <returns>A projected duplicate-result stage.<br/></returns>
    public LibraDexProjectedDuplicateKeys<int> AsInt32(Coercion.Numeric coercion)
        => new(index, LibraDexKeyProjection.Numeric<int>(coercion, offset, length), duplicatesFirst: true);

    /// <summary>Projects the selected duplicate bytes as Byte using ordinary .NET layout.<br/></summary>
    public LibraDexProjectedDuplicateKeys<byte> AsByte() => AsByte(Coercion.Numeric.DotNet);
    /// <summary>Projects the selected duplicate bytes as Byte using the selected numeric coercion.<br/></summary>
    public LibraDexProjectedDuplicateKeys<byte> AsByte(Coercion.Numeric coercion) => Numeric<byte>(coercion);
    /// <summary>Projects the selected duplicate bytes as SByte using ordinary .NET layout.<br/></summary>
    public LibraDexProjectedDuplicateKeys<sbyte> AsSByte() => AsSByte(Coercion.Numeric.DotNet);
    /// <summary>Projects the selected duplicate bytes as SByte using the selected numeric coercion.<br/></summary>
    public LibraDexProjectedDuplicateKeys<sbyte> AsSByte(Coercion.Numeric coercion) => Numeric<sbyte>(coercion);
    /// <summary>Projects the selected duplicate bytes as Int16 using ordinary .NET layout.<br/></summary>
    public LibraDexProjectedDuplicateKeys<short> AsInt16() => AsInt16(Coercion.Numeric.DotNet);
    /// <summary>Projects the selected duplicate bytes as Int16 using the selected numeric coercion.<br/></summary>
    public LibraDexProjectedDuplicateKeys<short> AsInt16(Coercion.Numeric coercion) => Numeric<short>(coercion);
    /// <summary>Projects the selected duplicate bytes as UInt16 using ordinary .NET layout.<br/></summary>
    public LibraDexProjectedDuplicateKeys<ushort> AsUInt16() => AsUInt16(Coercion.Numeric.DotNet);
    /// <summary>Projects the selected duplicate bytes as UInt16 using the selected numeric coercion.<br/></summary>
    public LibraDexProjectedDuplicateKeys<ushort> AsUInt16(Coercion.Numeric coercion) => Numeric<ushort>(coercion);
    /// <summary>Projects the selected duplicate bytes as UInt32 using ordinary .NET layout.<br/></summary>
    public LibraDexProjectedDuplicateKeys<uint> AsUInt32() => AsUInt32(Coercion.Numeric.DotNet);
    /// <summary>Projects the selected duplicate bytes as UInt32 using the selected numeric coercion.<br/></summary>
    public LibraDexProjectedDuplicateKeys<uint> AsUInt32(Coercion.Numeric coercion) => Numeric<uint>(coercion);
    /// <summary>Projects the selected duplicate bytes as Int64 using ordinary .NET layout.<br/></summary>
    public LibraDexProjectedDuplicateKeys<long> AsInt64() => AsInt64(Coercion.Numeric.DotNet);
    /// <summary>Projects the selected duplicate bytes as Int64 using the selected numeric coercion.<br/></summary>
    public LibraDexProjectedDuplicateKeys<long> AsInt64(Coercion.Numeric coercion) => Numeric<long>(coercion);
    /// <summary>Projects the selected duplicate bytes as UInt64 using ordinary .NET layout.<br/></summary>
    public LibraDexProjectedDuplicateKeys<ulong> AsUInt64() => AsUInt64(Coercion.Numeric.DotNet);
    /// <summary>Projects the selected duplicate bytes as UInt64 using the selected numeric coercion.<br/></summary>
    public LibraDexProjectedDuplicateKeys<ulong> AsUInt64(Coercion.Numeric coercion) => Numeric<ulong>(coercion);
    /// <summary>Projects the selected duplicate bytes as Int128 with checked widening or narrowing.<br/></summary>
    public LibraDexProjectedDuplicateKeys<Int128> AsInt128() => AsInt128(Coercion.Numeric.DotNet);
    /// <summary>Projects the selected duplicate bytes as Int128 using the selected numeric coercion.<br/></summary>
    public LibraDexProjectedDuplicateKeys<Int128> AsInt128(Coercion.Numeric coercion) => Numeric<Int128>(coercion);
    /// <summary>Projects the selected duplicate bytes as UInt128 with checked widening or narrowing.<br/></summary>
    public LibraDexProjectedDuplicateKeys<UInt128> AsUInt128() => AsUInt128(Coercion.Numeric.DotNet);
    /// <summary>Projects the selected duplicate bytes as UInt128 using the selected numeric coercion.<br/></summary>
    public LibraDexProjectedDuplicateKeys<UInt128> AsUInt128(Coercion.Numeric coercion) => Numeric<UInt128>(coercion);
    /// <summary>Projects the selected duplicate bytes as Single using ordinary .NET layout.<br/></summary>
    public LibraDexProjectedDuplicateKeys<float> AsSingle() => AsSingle(Coercion.Numeric.DotNet);
    /// <summary>Projects the selected duplicate bytes as Single using the selected numeric coercion.<br/></summary>
    public LibraDexProjectedDuplicateKeys<float> AsSingle(Coercion.Numeric coercion) => Numeric<float>(coercion);
    /// <summary>Projects the selected duplicate bytes as Double using ordinary .NET layout.<br/></summary>
    public LibraDexProjectedDuplicateKeys<double> AsDouble() => AsDouble(Coercion.Numeric.DotNet);
    /// <summary>Projects the selected duplicate bytes as Double using the selected numeric coercion.<br/></summary>
    public LibraDexProjectedDuplicateKeys<double> AsDouble(Coercion.Numeric coercion) => Numeric<double>(coercion);
    /// <summary>Projects the selected duplicate bytes as Decimal using ordinary .NET layout.<br/></summary>
    public LibraDexProjectedDuplicateKeys<decimal> AsDecimal() => AsDecimal(Coercion.Numeric.DotNet);
    /// <summary>Projects the selected duplicate bytes as Decimal using the selected numeric coercion.<br/></summary>
    public LibraDexProjectedDuplicateKeys<decimal> AsDecimal(Coercion.Numeric coercion) => Numeric<decimal>(coercion);
    /// <summary>Projects the selected duplicate bytes as BigInteger using ordinary .NET layout.<br/></summary>
    public LibraDexProjectedDuplicateKeys<BigInteger> AsBigInt() => AsBigInt(Coercion.Numeric.DotNet);
    /// <summary>Projects the selected duplicate bytes as BigInteger using the selected numeric coercion.<br/></summary>
    public LibraDexProjectedDuplicateKeys<BigInteger> AsBigInt(Coercion.Numeric coercion) => Numeric<BigInteger>(coercion);
    /// <summary>Projects the selected duplicate bytes as Char using ordinary .NET integral layout.<br/></summary>
    public LibraDexProjectedDuplicateKeys<char> AsChar() => AsChar(Coercion.Numeric.DotNet);
    /// <summary>Projects the selected duplicate bytes as Char using the selected numeric coercion.<br/></summary>
    public LibraDexProjectedDuplicateKeys<char> AsChar(Coercion.Numeric coercion) => Numeric<char>(coercion);

    private LibraDexProjectedDuplicateKeys<TValue> Numeric<TValue>(Coercion.Numeric coercion)
        => new(index, LibraDexKeyProjection.Numeric<TValue>(coercion, offset, length), duplicatesFirst: true);

    /// <summary>Projects the selected duplicate bytes as DateTime using the explicitly selected physical representation.<br/></summary>
    public LibraDexProjectedDuplicateKeys<DateTime> AsDateTime(Coercion.DateTime coercion) => Temporal<DateTime>(coercion);
    /// <summary>Projects the selected duplicate bytes as DateTimeOffset using the explicitly selected physical representation.<br/></summary>
    public LibraDexProjectedDuplicateKeys<DateTimeOffset> AsDateTimeOffset(Coercion.DateTimeOffset coercion) => Temporal<DateTimeOffset>(coercion);
    /// <summary>Projects the selected duplicate bytes as DateOnly using the explicitly selected physical representation.<br/></summary>
    public LibraDexProjectedDuplicateKeys<DateOnly> AsDateOnly(Coercion.DateOnly coercion) => Temporal<DateOnly>(coercion);
    /// <summary>Projects the selected duplicate bytes as TimeOnly using the explicitly selected physical representation.<br/></summary>
    public LibraDexProjectedDuplicateKeys<TimeOnly> AsTimeOnly(Coercion.TimeOnly coercion) => Temporal<TimeOnly>(coercion);
    /// <summary>Projects the selected duplicate bytes as TimeSpan using the explicitly selected physical representation.<br/></summary>
    public LibraDexProjectedDuplicateKeys<TimeSpan> AsTimeSpan(Coercion.TimeSpan coercion) => Temporal<TimeSpan>(coercion);

    private LibraDexProjectedDuplicateKeys<TValue> Temporal<TValue>(object coercion)
        => new(index, LibraDexKeyProjection.Temporal<TValue>(coercion, offset, length), duplicatesFirst: true);
}

/// <summary>
/// Provides collection, reader, and projected-equality duplicate operations after untyped key projection.<br/>
/// Projection occurs before duplicate analysis, so multiple physical keys that coerce to one value share the projected equality bucket.<br/>
/// </summary>
/// <typeparam name="TValue">The projected key value type.<br/></typeparam>
public readonly partial struct LibraDexProjectedIndexKeys<TValue>
{
    private readonly IIndex index;
    private readonly LibraDexKeyProjection projection;

    internal LibraDexProjectedIndexKeys(IIndex index, LibraDexKeyProjection projection)
    {
        this.index = index;
        this.projection = projection;
    }

    /// <summary>
    /// Materializes distinct projected key values in first-seen traversal order.<br/>
    /// </summary>
    public IReadOnlyList<TValue> Get()
        => LibraDexDuplicateExecution.DistinctProjectedKeys<TValue>(index, projection);

    /// <summary>
    /// Opens a reader over distinct projected key values.<br/>
    /// </summary>
    public LibraDexDuplicateReader<TValue> OpenReader()
        => new(Get());

    /// <summary>
    /// Gets duplicate analysis using projected-value equality rather than complete physical-key equality.<br/>
    /// </summary>
    public LibraDexProjectedDuplicateKeys<TValue> Duplicates
        => new(index, projection, duplicatesFirst: false);

    /// <summary>
    /// Gets singleton analysis using projected-value equality rather than complete physical-key equality.<br/>
    /// Multiple physical keys that project to the same value share one cardinality bucket.<br/>
    /// </summary>
    public LibraDexProjectedSingletonKeys<TValue> Singletons
        => new(index, projection, singletonsFirst: false);
}

/// <summary>
/// Provides projected duplicate-key collection and reader operations.<br/>
/// The fluent stage remembers whether projection occurred before or after duplicate selection so equality and returned representation cannot be silently conflated.<br/>
/// </summary>
/// <typeparam name="TValue">The projected duplicate-key result type.<br/></typeparam>
public readonly struct LibraDexProjectedDuplicateKeys<TValue>
{
    private readonly IIndex index;
    private readonly LibraDexKeyProjection projection;
    private readonly bool duplicatesFirst;

    internal LibraDexProjectedDuplicateKeys(IIndex index, LibraDexKeyProjection projection, bool duplicatesFirst)
    {
        this.index = index;
        this.projection = projection;
        this.duplicatesFirst = duplicatesFirst;
    }

    /// <summary>
    /// Materializes projected duplicate keys using the equality order expressed by the fluent chain.<br/>
    /// </summary>
    public IReadOnlyList<TValue> Get()
        => duplicatesFirst
            ? LibraDexDuplicateExecution.ProjectRawDuplicateKeys<TValue>(index, projection)
            : LibraDexDuplicateExecution.DuplicateProjectedKeys<TValue>(index, projection);

    /// <summary>
    /// Opens a reader over projected duplicate keys using the equality order expressed by the fluent chain.<br/>
    /// </summary>
    public LibraDexDuplicateReader<TValue> OpenReader()
        => new(Get());
}

/// <summary>
/// Provides non-generic duplicate-identity results for an <see cref="IIndex"/>.<br/>
/// </summary>
public readonly struct LibraDexUntypedIdentityDuplicates
{
    private readonly IIndex index;

    internal LibraDexUntypedIdentityDuplicates(IIndex index)
    {
        this.index = index;
    }

    /// <summary>
    /// Materializes distinct runtime identities associated with at least two distinct keys.<br/>
    /// </summary>
    public IReadOnlyList<object> Get()
        => LibraDexDuplicateExecution.DuplicateIdentities<object>(index).ToList();

    /// <summary>
    /// Opens a forward-only reader over distinct runtime identities associated with multiple keys.<br/>
    /// </summary>
    public LibraDexDuplicateReader<object> OpenReader()
        => new(LibraDexDuplicateExecution.DuplicateIdentities<object>(index));
}

/// <summary>
/// Provides non-generic complete entries selected by duplicated runtime key.<br/>
/// </summary>
public readonly struct LibraDexUntypedEntriesByDuplicateKey
{
    private readonly IIndex index;

    internal LibraDexUntypedEntriesByDuplicateKey(IIndex index)
    {
        this.index = index;
    }

    /// <summary>
    /// Materializes every runtime key/identity entry belonging to a duplicated key.<br/>
    /// </summary>
    public IReadOnlyList<LibraDexIndexEntry<byte[]?, object>> Get()
        => LibraDexDuplicateExecution.RawEntriesByDuplicateKey(index).ToList();

    /// <summary>
    /// Opens a forward-only reader over every runtime entry belonging to a duplicated key.<br/>
    /// </summary>
    public LibraDexDuplicateReader<LibraDexIndexEntry<byte[]?, object>> OpenReader()
        => new(LibraDexDuplicateExecution.RawEntriesByDuplicateKey(index));
}

/// <summary>
/// Provides non-generic complete entries selected by duplicated runtime identity.<br/>
/// </summary>
public readonly struct LibraDexUntypedEntriesByDuplicateIdentity
{
    private readonly IIndex index;

    internal LibraDexUntypedEntriesByDuplicateIdentity(IIndex index)
    {
        this.index = index;
    }

    /// <summary>
    /// Materializes every runtime key/identity entry belonging to a duplicated identity.<br/>
    /// </summary>
    public IReadOnlyList<LibraDexIndexEntry<byte[]?, object>> Get()
        => LibraDexDuplicateExecution.RawEntriesByDuplicateIdentity(index).ToList();

    /// <summary>
    /// Opens a forward-only reader over every runtime entry belonging to a duplicated identity.<br/>
    /// </summary>
    public LibraDexDuplicateReader<LibraDexIndexEntry<byte[]?, object>> OpenReader()
        => new(LibraDexDuplicateExecution.RawEntriesByDuplicateIdentity(index));
}

/// <summary>
/// Provides a forward-only reader over duplicate-analysis results.<br/>
/// The reader does not materialize the returned result collection, although duplicate discovery may retain compact key or identity sets until the scan completes.<br/>
/// </summary>
/// <typeparam name="TValue">The value returned for each duplicate result.<br/></typeparam>
public sealed class LibraDexDuplicateReader<TValue> : IDisposable
{
    private IEnumerator<TValue>? reader;

    internal LibraDexDuplicateReader(IEnumerable<TValue> source)
    {
        ArgumentNullException.ThrowIfNull(source);
        reader = source.GetEnumerator();
    }

    /// <summary>
    /// Gets the current duplicate-analysis result.<br/>
    /// The value is valid only after <see cref="Read"/> returns <see langword="true"/> and before the next read or disposal.<br/>
    /// </summary>
    public TValue Current { get; private set; } = default!;

    /// <summary>
    /// Advances to the next duplicate-analysis result.<br/>
    /// The underlying forward index may be scanned before the first result can be proven, especially for identity-oriented duplicate discovery.<br/>
    /// </summary>
    /// <returns><see langword="true"/> when another result is available.<br/></returns>
    public bool Read()
    {
        IEnumerator<TValue>? currentReader = reader;
        if (currentReader is null || !currentReader.MoveNext())
        {
            Current = default!;
            return false;
        }

        Current = currentReader.Current;
        return true;
    }

    /// <summary>
    /// Advances to the next duplicate-analysis result.<br/>
    /// This is an alias for <see cref="Read"/> for callers that prefer iterator terminology.<br/>
    /// </summary>
    /// <returns><see langword="true"/> when another result is available.<br/></returns>
    public bool MoveNext() => Read();

    /// <summary>
    /// Releases the active forward-index enumerator and any discovery state owned by it.<br/>
    /// </summary>
    public void Dispose()
    {
        reader?.Dispose();
        reader = null;
        Current = default!;
    }
}

/// <summary>
/// Provides duplicate-key collection and reader access for one typed index.<br/>
/// A key is duplicated when it is associated with at least two distinct identities; repeated insertion of the same exact tuple does not create a duplicate.<br/>
/// </summary>
public readonly struct LibraDexIndexKeyDuplicates<TKey, TIdentity>
{
    private readonly IIndex index;

    internal LibraDexIndexKeyDuplicates(IIndex index)
    {
        this.index = index;
    }

    /// <summary>
    /// Materializes distinct keys associated with at least two distinct identities.<br/>
    /// Keys are returned once in first-seen index traversal order.<br/>
    /// </summary>
    public IReadOnlyList<TKey> Get()
        => LibraDexDuplicateExecution.DuplicateKeys<TKey>(index).ToList();

    /// <summary>
    /// Opens a forward-only duplicate-key reader.<br/>
    /// Duplicate discovery retains only grouping state needed to prove multiplicity and does not materialize the returned key list.<br/>
    /// </summary>
    public LibraDexDuplicateReader<TKey> OpenReader()
        => new(LibraDexDuplicateExecution.DuplicateKeys<TKey>(index));
}

/// <summary>
/// Provides duplicate-identity collection and reader access for one typed index.<br/>
/// An identity is duplicated when it is associated with at least two distinct keys.<br/>
/// </summary>
public readonly struct LibraDexIndexIdentityDuplicates<TKey, TIdentity>
{
    private readonly IIndex index;

    internal LibraDexIndexIdentityDuplicates(IIndex index)
    {
        this.index = index;
    }

    /// <summary>
    /// Materializes distinct identities associated with at least two distinct keys.<br/>
    /// <see cref="IdentityKeyMultiplicity.SingleKeyPerIdentity"/> indexes return an empty collection without scanning.<br/>
    /// </summary>
    public IReadOnlyList<TIdentity> Get()
        => LibraDexDuplicateExecution.DuplicateIdentities<TIdentity>(index).ToList();

    /// <summary>
    /// Opens a forward-only duplicate-identity reader.<br/>
    /// <see cref="IdentityKeyMultiplicity.SingleKeyPerIdentity"/> indexes produce no rows without scanning.<br/>
    /// </summary>
    public LibraDexDuplicateReader<TIdentity> OpenReader()
        => new(LibraDexDuplicateExecution.DuplicateIdentities<TIdentity>(index));
}

/// <summary>
/// Provides complete entry tuples for keys associated with multiple identities.<br/>
/// </summary>
public readonly struct LibraDexEntriesByDuplicateKey<TKey, TIdentity>
{
    private readonly IIndex index;

    internal LibraDexEntriesByDuplicateKey(IIndex index)
    {
        this.index = index;
    }

    /// <summary>
    /// Materializes every key/identity entry belonging to a duplicated key.<br/>
    /// </summary>
    public IReadOnlyList<LibraDexIndexEntry<TKey, TIdentity>> Get()
        => LibraDexDuplicateExecution.EntriesByDuplicateKey<TKey, TIdentity>(index).ToList();

    /// <summary>
    /// Opens a forward-only reader over every key/identity entry belonging to a duplicated key.<br/>
    /// </summary>
    public LibraDexDuplicateReader<LibraDexIndexEntry<TKey, TIdentity>> OpenReader()
        => new(LibraDexDuplicateExecution.EntriesByDuplicateKey<TKey, TIdentity>(index));
}

/// <summary>
/// Provides complete entry tuples for identities associated with multiple keys.<br/>
/// </summary>
public readonly struct LibraDexEntriesByDuplicateIdentity<TKey, TIdentity>
{
    private readonly IIndex index;

    internal LibraDexEntriesByDuplicateIdentity(IIndex index)
    {
        this.index = index;
    }

    /// <summary>
    /// Materializes every key/identity entry belonging to a duplicated identity.<br/>
    /// This is the direct way to retrieve all keys associated with each identity that occurs under multiple distinct keys.<br/>
    /// </summary>
    public IReadOnlyList<LibraDexIndexEntry<TKey, TIdentity>> Get()
        => LibraDexDuplicateExecution.EntriesByDuplicateIdentity<TKey, TIdentity>(index).ToList();

    /// <summary>
    /// Opens a forward-only reader over every key/identity entry belonging to a duplicated identity.<br/>
    /// </summary>
    public LibraDexDuplicateReader<LibraDexIndexEntry<TKey, TIdentity>> OpenReader()
        => new(LibraDexDuplicateExecution.EntriesByDuplicateIdentity<TKey, TIdentity>(index));
}

internal enum LibraDexKeyProjectionKind
{
    String = 0,
    Numeric = 1,
    SortKey = 2,
    Temporal = 3
}

internal readonly record struct LibraDexKeyProjection(
    LibraDexKeyProjectionKind Kind,
    int Offset,
    int Length,
    Coercion.Numeric NumericCoercion,
    SubIndexType? StringSubIndex,
    Type? NumericType,
    object? TemporalCoercion)
{
    internal const int WholeKey = -1;

    internal static LibraDexKeyProjection String(
        int offset = 0,
        int length = WholeKey)
        => new(LibraDexKeyProjectionKind.String, offset, length, Coercion.Numeric.DotNet, null, null, null);

    internal static LibraDexKeyProjection String(SubIndexType subIndex)
        => new(LibraDexKeyProjectionKind.String, 0, WholeKey, Coercion.Numeric.DotNet, subIndex, null, null);

    internal static LibraDexKeyProjection SortKey()
        => new(LibraDexKeyProjectionKind.SortKey, 0, WholeKey, Coercion.Numeric.DotNet, SubIndexType.SortKey, null, null);

    internal static LibraDexKeyProjection Numeric<TValue>(
        Coercion.Numeric coercion,
        int offset = 0,
        int length = WholeKey)
    {
        if (coercion is not (Coercion.Numeric.DotNet or Coercion.Numeric.LibraDex))
            throw new ArgumentOutOfRangeException(nameof(coercion), coercion, "Unknown numeric coercion.");
        return new LibraDexKeyProjection(LibraDexKeyProjectionKind.Numeric, offset, length, coercion, null, typeof(TValue), null);
    }

    internal static LibraDexKeyProjection Temporal<TValue>(
        object coercion,
        int offset = 0,
        int length = WholeKey)
    {
        ArgumentNullException.ThrowIfNull(coercion);
        return new LibraDexKeyProjection(
            LibraDexKeyProjectionKind.Temporal,
            offset,
            length,
            Coercion.Numeric.DotNet,
            null,
            typeof(TValue),
            coercion);
    }
}

internal static partial class LibraDexDuplicateExecution
{
    internal static IReadOnlyList<byte[]?> DistinctRawKeys(IIndex index)
    {
        ArgumentNullException.ThrowIfNull(index);
        if (index is LibraDexStringScalar8Index stringIndex)
            return stringIndex.GetRawKeys(duplicatesOnly: false);

        List<byte[]?> results = new();
        HashSet<LibraDexDuplicateValue> seen = new(LibraDexDuplicateValueComparer.Instance);
        foreach (LibraDexRawTuple tuple in IterateRaw(index, null))
        {
            LibraDexDuplicateValue key = new(tuple.Key);
            if (seen.Add(key))
                results.Add(tuple.Key);
        }

        return results;
    }

    internal static IEnumerable<byte[]?> IterateDuplicateRawKeys(IIndex index)
    {
        ArgumentNullException.ThrowIfNull(index);
        if (index is LibraDexStringScalar8Index stringIndex)
        {
            foreach (byte[]? key in stringIndex.IterateRawDuplicateKeys())
                yield return key;
            yield break;
        }

        Dictionary<LibraDexDuplicateValue, HashSet<LibraDexDuplicateValue>> groups =
            new(LibraDexDuplicateValueComparer.Instance);
        foreach (LibraDexRawTuple tuple in IterateRaw(index, null))
        {
            LibraDexDuplicateValue key = new(tuple.Key);
            if (!groups.TryGetValue(key, out HashSet<LibraDexDuplicateValue>? identities))
            {
                identities = new HashSet<LibraDexDuplicateValue>(LibraDexDuplicateValueComparer.Instance);
                groups.Add(key, identities);
            }

            identities.Add(new LibraDexDuplicateValue(tuple.Identity));
        }

        foreach ((LibraDexDuplicateValue key, HashSet<LibraDexDuplicateValue> identities) in groups)
        {
            if (identities.Count > 1)
                yield return (byte[]?)key.Value;
        }
    }

    internal static IReadOnlyList<TValue> DistinctProjectedKeys<TValue>(
        IIndex index,
        LibraDexKeyProjection projection)
    {
        List<TValue> results = new();
        HashSet<TValue> seen = new();
        foreach (LibraDexRawTuple tuple in IterateRaw(index, projection.StringSubIndex))
        {
            TValue value = Project<TValue>(index, tuple.Key, projection);
            if (seen.Add(value))
                results.Add(value);
        }

        return results;
    }

    internal static IReadOnlyList<TValue> ProjectRawDuplicateKeys<TValue>(
        IIndex index,
        LibraDexKeyProjection projection)
    {
        List<TValue> results = new();
        foreach (byte[]? duplicate in IterateDuplicateRawKeys(index))
            results.Add(Project<TValue>(index, duplicate, projection));
        return results;
    }

    internal static IReadOnlyList<TValue> DuplicateProjectedKeys<TValue>(
        IIndex index,
        LibraDexKeyProjection projection)
    {
        Dictionary<LibraDexDuplicateValue, HashSet<LibraDexDuplicateValue>> groups =
            new(LibraDexDuplicateValueComparer.Instance);
        foreach (LibraDexRawTuple tuple in IterateRaw(index, projection.StringSubIndex))
        {
            TValue value = Project<TValue>(index, tuple.Key, projection);
            LibraDexDuplicateValue key = new(value);
            if (!groups.TryGetValue(key, out HashSet<LibraDexDuplicateValue>? identities))
            {
                identities = new HashSet<LibraDexDuplicateValue>(LibraDexDuplicateValueComparer.Instance);
                groups.Add(key, identities);
            }

            identities.Add(new LibraDexDuplicateValue(tuple.Identity));
        }

        List<TValue> results = new();
        foreach ((LibraDexDuplicateValue value, HashSet<LibraDexDuplicateValue> identities) in groups)
        {
            if (identities.Count > 1)
                results.Add(ConvertValue<TValue>(value.Value, "projected key"));
        }

        return results;
    }

    internal static IEnumerable<TKey> DuplicateKeys<TKey>(IIndex index)
    {
        Dictionary<LibraDexDuplicateValue, HashSet<LibraDexDuplicateValue>> groups = BuildGroups(index, byKey: true);
        foreach ((LibraDexDuplicateValue key, HashSet<LibraDexDuplicateValue> identities) in groups)
        {
            if (identities.Count > 1)
                yield return ConvertValue<TKey>(key.Value, "key");
        }
    }

    internal static IEnumerable<TIdentity> DuplicateIdentities<TIdentity>(IIndex index)
    {
        if (index.IdentityKeyMultiplicity == IdentityKeyMultiplicity.SingleKeyPerIdentity)
            yield break;

        if (index.Catalog.TryGetDuplicateIdentities(index, out object[] inverseIdentities))
        {
            for (int i = 0; i < inverseIdentities.Length; i++)
                yield return ConvertValue<TIdentity>(inverseIdentities[i], "identity");
            yield break;
        }

        Dictionary<LibraDexDuplicateValue, HashSet<LibraDexDuplicateValue>> groups = BuildGroups(index, byKey: false);
        foreach ((LibraDexDuplicateValue identity, HashSet<LibraDexDuplicateValue> keys) in groups)
        {
            if (keys.Count > 1)
                yield return ConvertValue<TIdentity>(identity.Value, "identity");
        }
    }

    internal static IEnumerable<LibraDexIndexEntry<TKey, TIdentity>> EntriesByDuplicateKey<TKey, TIdentity>(IIndex index)
    {
        HashSet<LibraDexDuplicateValue> duplicates = FindDuplicateValues(index, byKey: true);
        if (duplicates.Count == 0)
            yield break;

        foreach (LibraDexObjectTuple tuple in Iterate(index))
        {
            if (duplicates.Contains(new LibraDexDuplicateValue(tuple.Key)))
            {
                yield return new LibraDexIndexEntry<TKey, TIdentity>(
                    ConvertValue<TKey>(tuple.Key, "key"),
                    ConvertValue<TIdentity>(tuple.Identity, "identity"));
            }
        }
    }

    internal static IEnumerable<LibraDexIndexEntry<TKey, TIdentity>> EntriesByDuplicateIdentity<TKey, TIdentity>(IIndex index)
    {
        if (index.IdentityKeyMultiplicity == IdentityKeyMultiplicity.SingleKeyPerIdentity)
            yield break;

        if (index.Catalog.TryGetEntriesForDuplicateIdentities(index, out LibraDexObjectTuple[] inverseEntries))
        {
            for (int i = 0; i < inverseEntries.Length; i++)
            {
                yield return new LibraDexIndexEntry<TKey, TIdentity>(
                    ConvertValue<TKey>(inverseEntries[i].Key, "key"),
                    ConvertValue<TIdentity>(inverseEntries[i].Identity, "identity"));
            }

            yield break;
        }

        HashSet<LibraDexDuplicateValue> duplicates = FindDuplicateValues(index, byKey: false);
        if (duplicates.Count == 0)
            yield break;

        foreach (LibraDexObjectTuple tuple in Iterate(index))
        {
            if (duplicates.Contains(new LibraDexDuplicateValue(tuple.Identity)))
            {
                yield return new LibraDexIndexEntry<TKey, TIdentity>(
                    ConvertValue<TKey>(tuple.Key, "key"),
                    ConvertValue<TIdentity>(tuple.Identity, "identity"));
            }
        }
    }

    internal static IEnumerable<LibraDexIndexEntry<byte[]?, object>> RawEntriesByDuplicateKey(IIndex index)
    {
        HashSet<LibraDexDuplicateValue> duplicates =
            new(IterateDuplicateRawKeys(index).Select(static key => new LibraDexDuplicateValue(key)),
                LibraDexDuplicateValueComparer.Instance);
        if (duplicates.Count == 0)
            yield break;

        foreach (LibraDexRawTuple tuple in IterateRaw(index, null))
        {
            if (duplicates.Contains(new LibraDexDuplicateValue(tuple.Key)))
                yield return new LibraDexIndexEntry<byte[]?, object>(tuple.Key, tuple.Identity);
        }
    }

    internal static IEnumerable<LibraDexIndexEntry<byte[]?, object>> RawEntriesByDuplicateIdentity(IIndex index)
    {
        if (index.IdentityKeyMultiplicity == IdentityKeyMultiplicity.SingleKeyPerIdentity)
            yield break;

        if (index.Catalog.TryGetEntriesForDuplicateIdentities(index, out LibraDexObjectTuple[] inverseEntries))
        {
            for (int i = 0; i < inverseEntries.Length; i++)
            {
                yield return new LibraDexIndexEntry<byte[]?, object>(
                    EncodeRawKey(index, inverseEntries[i].Key),
                    inverseEntries[i].Identity);
            }

            yield break;
        }

        HashSet<LibraDexDuplicateValue> duplicates = FindDuplicateValues(index, byKey: false);
        if (duplicates.Count == 0)
            yield break;

        foreach (LibraDexRawTuple tuple in IterateRaw(index, null))
        {
            if (duplicates.Contains(new LibraDexDuplicateValue(tuple.Identity)))
                yield return new LibraDexIndexEntry<byte[]?, object>(tuple.Key, tuple.Identity);
        }
    }

    private static HashSet<LibraDexDuplicateValue> FindDuplicateValues(IIndex index, bool byKey)
    {
        Dictionary<LibraDexDuplicateValue, HashSet<LibraDexDuplicateValue>> groups = BuildGroups(index, byKey);
        HashSet<LibraDexDuplicateValue> duplicates = new(LibraDexDuplicateValueComparer.Instance);
        foreach ((LibraDexDuplicateValue value, HashSet<LibraDexDuplicateValue> related) in groups)
        {
            if (related.Count > 1)
                duplicates.Add(value);
        }

        return duplicates;
    }

    private static Dictionary<LibraDexDuplicateValue, HashSet<LibraDexDuplicateValue>> BuildGroups(
        IIndex index,
        bool byKey)
    {
        Dictionary<LibraDexDuplicateValue, HashSet<LibraDexDuplicateValue>> groups =
            new(LibraDexDuplicateValueComparer.Instance);
        foreach (LibraDexObjectTuple tuple in Iterate(index))
        {
            LibraDexDuplicateValue group = new(byKey ? tuple.Key : tuple.Identity);
            LibraDexDuplicateValue related = new(byKey ? tuple.Identity : tuple.Key);
            if (!groups.TryGetValue(group, out HashSet<LibraDexDuplicateValue>? values))
            {
                values = new HashSet<LibraDexDuplicateValue>(LibraDexDuplicateValueComparer.Instance);
                groups.Add(group, values);
            }

            values.Add(related);
        }

        return groups;
    }

    internal static IEnumerable<LibraDexObjectTuple> Iterate(IIndex index)
    {
        LibraDexIdentityPrimitiveRequest request = new(LibraDexCriteriaKind.All, Array.Empty<object?>());
        if (index is IIdentityPrimitiveTupleStreamer streamer)
            return streamer.IterateTuplePrimitive(request);
        if (index is IIdentityPrimitiveTupleExecutor executor)
            return executor.ExecuteTuplePrimitive(request);
        throw new NotSupportedException($"Index '{index.Name}' cannot stream key/identity entries for duplicate analysis.");
    }

    private static TValue ConvertValue<TValue>(object? value, string kind)
    {
        if (value is TValue typed)
            return typed;
        if (value is null && default(TValue) is null)
            return default!;
        throw new NotSupportedException(
            $"The {kind} value '{value?.GetType().Name ?? "null"}' cannot be represented by '{typeof(TValue).Name}'. Use the non-generic index entry surface when inspecting routed null or empty key states.");
    }

    private static IEnumerable<LibraDexRawTuple> IterateRaw(
        IIndex index,
        SubIndexType? stringSubIndex)
    {
        foreach (LibraDexGroupingTuple tuple in LibraDexAggregateExecutor.IterateGroupingTuples(index, stringSubIndex))
        {
            byte[]? key = tuple.RawGroupKey switch
            {
                null => null,
                byte[] bytes => bytes,
                _ => EncodeRawKey(index, tuple.RawGroupKey)
            };
            yield return new LibraDexRawTuple(key, tuple.Identity);
        }
    }

    private static TValue Project<TValue>(
        IIndex index,
        byte[]? raw,
        LibraDexKeyProjection projection)
    {
        if (raw is null)
        {
            if (default(TValue) is null)
                return default!;
            throw new InvalidDataException($"Null key state cannot be projected as non-nullable {typeof(TValue).Name}.");
        }

        ReadOnlySpan<byte> bytes = raw;
        if (projection.Length != LibraDexKeyProjection.WholeKey)
        {
            if (projection.Offset > bytes.Length ||
                projection.Length > bytes.Length - projection.Offset)
            {
                throw new InvalidDataException(
                    $"Key projection slice [{projection.Offset}..{projection.Offset + projection.Length}) exceeds the {bytes.Length}-byte stored key.");
            }

            bytes = bytes.Slice(projection.Offset, projection.Length);
        }

        object value = projection.Kind switch
        {
            LibraDexKeyProjectionKind.String when
                projection.Length == LibraDexKeyProjection.WholeKey &&
                (index.KeyFamily == CatalogIndexKeyFamily.String || projection.StringSubIndex is SubIndexType.Folded or SubIndexType.Normalized)
                => LibraDexStringScalar8Index.DecodeEncodedGroupingKey(bytes)!,
            LibraDexKeyProjectionKind.String => Encoding.UTF8.GetString(bytes),
            LibraDexKeyProjectionKind.Numeric
                => ProjectNumeric(bytes, projection.NumericType ??
                    throw new InvalidOperationException("A numeric projection is missing its destination type."),
                    projection.NumericCoercion),
            LibraDexKeyProjectionKind.Temporal
                => ProjectTemporal(bytes,
                    projection.NumericType ??
                    throw new InvalidOperationException("A temporal projection is missing its destination type."),
                    projection.TemporalCoercion ??
                    throw new InvalidOperationException("A temporal projection is missing its physical coercion.")),
            LibraDexKeyProjectionKind.SortKey => bytes.ToArray(),
            _ => throw new InvalidOperationException($"Unsupported key projection {projection.Kind}.")
        };

        return value is TValue typed
            ? typed
            : throw new InvalidDataException($"Projected key value '{value.GetType().Name}' cannot be returned as '{typeof(TValue).Name}'.");
    }

    private static object ProjectTemporal(
        ReadOnlySpan<byte> bytes,
        Type targetType,
        object coercion)
    {
        if (targetType == typeof(DateTime) && coercion is Coercion.DateTime dateTime)
        {
            return dateTime switch
            {
                Coercion.DateTime.DotNetTicks => new DateTime(ReadDotNetTicks(bytes, targetType), DateTimeKind.Unspecified),
                Coercion.DateTime.LibraDexCalendarSdt => LibraDexStructuredDateCodec.DecodeDateTime(ReadLibraDexScalar(bytes, targetType), DateTimeKeyEncoding.CalendarSdt),
                Coercion.DateTime.LibraDexPrecisionSdt => LibraDexStructuredDateCodec.DecodeDateTime(ReadLibraDexScalar(bytes, targetType), DateTimeKeyEncoding.PrecisionSdt),
                _ => throw new ArgumentOutOfRangeException(nameof(coercion), coercion, "Unknown DateTime coercion.")
            };
        }

        if (targetType == typeof(DateOnly) && coercion is Coercion.DateOnly dateOnly)
        {
            return dateOnly switch
            {
                Coercion.DateOnly.DotNetDayNumber => DateOnly.FromDayNumber(ReadDotNetDayNumber(bytes)),
                Coercion.DateOnly.LibraDexCalendarSdt => LibraDexStructuredDateCodec.DecodeDateOnly(ReadLibraDexScalar(bytes, targetType)),
                Coercion.DateOnly.LibraDexPrecisionSdt => DateOnly.FromDateTime(
                    LibraDexStructuredDateCodec.DecodeDateTime(ReadLibraDexScalar(bytes, targetType), DateTimeKeyEncoding.PrecisionSdt)),
                _ => throw new ArgumentOutOfRangeException(nameof(coercion), coercion, "Unknown DateOnly coercion.")
            };
        }

        if (targetType == typeof(TimeOnly) && coercion is Coercion.TimeOnly timeOnly)
        {
            return timeOnly switch
            {
                Coercion.TimeOnly.DotNetTicks => new TimeOnly(ReadDotNetTicks(bytes, targetType)),
                Coercion.TimeOnly.LibraDexCalendarSdt => LibraDexStructuredDateCodec.DecodeTimeOnly(ReadLibraDexScalar(bytes, targetType), DateTimeKeyEncoding.CalendarSdt),
                Coercion.TimeOnly.LibraDexPrecisionSdt => LibraDexStructuredDateCodec.DecodeTimeOnly(ReadLibraDexScalar(bytes, targetType), DateTimeKeyEncoding.PrecisionSdt),
                _ => throw new ArgumentOutOfRangeException(nameof(coercion), coercion, "Unknown TimeOnly coercion.")
            };
        }

        if (targetType == typeof(TimeSpan) && coercion is Coercion.TimeSpan timeSpan)
        {
            return timeSpan switch
            {
                Coercion.TimeSpan.DotNetTicks => TimeSpan.FromTicks(ReadDotNetTicks(bytes, targetType)),
                Coercion.TimeSpan.LibraDexOrderedTicks => LibraDexGenericScalarCodec<TimeSpan>.Decode8(ReadLibraDexScalar(bytes, targetType)),
                _ => throw new ArgumentOutOfRangeException(nameof(coercion), coercion, "Unknown TimeSpan coercion.")
            };
        }

        if (targetType == typeof(DateTimeOffset) && coercion is Coercion.DateTimeOffset dateTimeOffset)
        {
            if (dateTimeOffset == Coercion.DateTimeOffset.DotNetTicksAndOffset)
            {
                RequireWidth(targetType, Coercion.Numeric.DotNet, bytes, sizeof(long) * 2);
                long ticks = BinaryPrimitives.ReadInt64LittleEndian(bytes);
                long offsetTicks = BinaryPrimitives.ReadInt64LittleEndian(bytes[sizeof(long)..]);
                return new DateTimeOffset(ticks, TimeSpan.FromTicks(offsetTicks));
            }

            DateTimeKeyEncoding encoding = dateTimeOffset switch
            {
                Coercion.DateTimeOffset.LibraDexCalendarSdtUtc => DateTimeKeyEncoding.CalendarSdt,
                Coercion.DateTimeOffset.LibraDexPrecisionSdtUtc => DateTimeKeyEncoding.PrecisionSdt,
                _ => throw new ArgumentOutOfRangeException(nameof(coercion), coercion, "Unknown DateTimeOffset coercion.")
            };
            DateTime utc = LibraDexStructuredDateCodec.DecodeDateTime(ReadLibraDexScalar(bytes, targetType), encoding);
            return new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc));
        }

        throw new NotSupportedException(
            $"Temporal projection '{targetType.FullName}' cannot use coercion '{coercion.GetType().FullName}.{coercion}'.");
    }

    private static long ReadDotNetTicks(ReadOnlySpan<byte> bytes, Type targetType)
    {
        if (bytes.Length != sizeof(long))
            throw new InvalidDataException($"DotNet {targetType.Name} coercion requires exactly {sizeof(long)} bytes, not {bytes.Length}.");
        return BinaryPrimitives.ReadInt64LittleEndian(bytes);
    }

    private static int ReadDotNetDayNumber(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != sizeof(int))
            throw new InvalidDataException($"DotNet DateOnly coercion requires exactly {sizeof(int)} bytes, not {bytes.Length}.");
        return BinaryPrimitives.ReadInt32LittleEndian(bytes);
    }

    private static ulong ReadLibraDexScalar(ReadOnlySpan<byte> bytes, Type targetType)
    {
        if (bytes.Length != sizeof(ulong))
            throw new InvalidDataException($"LibraDex {targetType.Name} coercion requires exactly {sizeof(ulong)} bytes, not {bytes.Length}.");
        return BinaryPrimitives.ReadUInt64BigEndian(bytes);
    }

    internal static object ProjectNumeric(
        ReadOnlySpan<byte> bytes,
        Type targetType,
        Coercion.Numeric coercion)
    {
        if (targetType == typeof(BigInteger))
        {
            if (coercion == Coercion.Numeric.DotNet)
                return new BigInteger(bytes, isUnsigned: false, isBigEndian: false);

            if (bytes.Length == 1 && bytes[0] == 0x01)
                return BigInteger.Zero;
            if (bytes.Length < 3)
                throw new InvalidDataException("A LibraDex BigInt projection requires a complete sortable BigInt value.");

            bool negative = bytes[0] == 0x00;
            ushort storedLength = BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(1, 2));
            int magnitudeLength = negative ? unchecked((ushort)~storedLength) : storedLength;
            LibraDexBigIntKeyStorage storage = bytes.Length == magnitudeLength + 3
                ? LibraDexBigIntKeyStorage.VariableWidth
                : LibraDexBigIntKeyStorage.FixedWidth;
            int maxBytes = storage == LibraDexBigIntKeyStorage.VariableWidth
                ? magnitudeLength
                : checked(bytes.Length - 3);
            return LibraDexBigIntCodec.Decode(bytes, maxBytes, storage);
        }

        if (targetType == typeof(float))
        {
            if (coercion == Coercion.Numeric.DotNet)
            {
                if (bytes.Length != sizeof(float))
                    throw WidthException(targetType, coercion, bytes.Length, sizeof(float));
                return BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(bytes));
            }

            RequireWidth(targetType, coercion, bytes, sizeof(ulong));
            return LibraDexGenericScalarCodec<float>.Decode8(BinaryPrimitives.ReadUInt64BigEndian(bytes));
        }

        if (targetType == typeof(double))
        {
            RequireWidth(targetType, coercion, bytes, sizeof(double));
            return coercion == Coercion.Numeric.DotNet
                ? BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(bytes))
                : LibraDexGenericScalarCodec<double>.Decode8(BinaryPrimitives.ReadUInt64BigEndian(bytes));
        }

        if (targetType == typeof(decimal))
        {
            RequireWidth(targetType, coercion, bytes, 16);
            if (coercion == Coercion.Numeric.LibraDex)
            {
                return LibraDexGenericScalarCodec<decimal>.Decode16(
                    BinaryPrimitives.ReadUInt64BigEndian(bytes),
                    BinaryPrimitives.ReadUInt64BigEndian(bytes[sizeof(ulong)..]));
            }

            return System.Runtime.InteropServices.MemoryMarshal.Read<decimal>(bytes);
        }

        bool signed = targetType == typeof(sbyte) ||
            targetType == typeof(short) ||
            targetType == typeof(int) ||
            targetType == typeof(long) ||
            targetType == typeof(Int128);
        bool unsigned = targetType == typeof(byte) ||
            targetType == typeof(ushort) ||
            targetType == typeof(uint) ||
            targetType == typeof(ulong) ||
            targetType == typeof(UInt128) ||
            targetType == typeof(char);
        if (!signed && !unsigned)
            throw new NotSupportedException($"Numeric projection does not support '{targetType.FullName}'.");

        return signed
            ? ConvertSigned(ReadSigned(bytes, coercion, targetType), targetType)
            : ConvertUnsigned(ReadUnsigned(bytes, coercion, targetType), targetType);
    }

    private static Int128 ReadSigned(
        ReadOnlySpan<byte> bytes,
        Coercion.Numeric coercion,
        Type targetType)
    {
        if (coercion == Coercion.Numeric.LibraDex && bytes.Length == sizeof(ulong))
        {
            ulong encoded = BinaryPrimitives.ReadUInt64BigEndian(bytes);
            if (targetType == typeof(sbyte)) return LibraDexGenericScalarCodec<sbyte>.Decode8(encoded);
            if (targetType == typeof(short)) return LibraDexGenericScalarCodec<short>.Decode8(encoded);
            if (targetType == typeof(int)) return LibraDexGenericScalarCodec<int>.Decode8(encoded);
            return LibraDexGenericScalarCodec<long>.Decode8(encoded);
        }

        return (coercion, bytes.Length) switch
        {
            (Coercion.Numeric.DotNet, 1) => unchecked((sbyte)bytes[0]),
            (Coercion.Numeric.DotNet, 2) => BinaryPrimitives.ReadInt16LittleEndian(bytes),
            (Coercion.Numeric.DotNet, 4) => BinaryPrimitives.ReadInt32LittleEndian(bytes),
            (Coercion.Numeric.DotNet, 8) => BinaryPrimitives.ReadInt64LittleEndian(bytes),
            (Coercion.Numeric.DotNet, 16) => BinaryPrimitives.ReadInt128LittleEndian(bytes),
            (Coercion.Numeric.LibraDex, 16) => LibraDexGenericScalarCodec<Int128>.Decode16(
                BinaryPrimitives.ReadUInt64BigEndian(bytes),
                BinaryPrimitives.ReadUInt64BigEndian(bytes[sizeof(ulong)..])),
            _ => throw new InvalidDataException($"{coercion} signed numeric coercion does not support a {bytes.Length}-byte source.")
        };
    }

    private static UInt128 ReadUnsigned(
        ReadOnlySpan<byte> bytes,
        Coercion.Numeric coercion,
        Type targetType)
    {
        if (coercion == Coercion.Numeric.LibraDex && bytes.Length == sizeof(ulong))
        {
            ulong encoded = BinaryPrimitives.ReadUInt64BigEndian(bytes);
            if (targetType == typeof(byte)) return LibraDexGenericScalarCodec<byte>.Decode8(encoded);
            if (targetType == typeof(ushort)) return LibraDexGenericScalarCodec<ushort>.Decode8(encoded);
            if (targetType == typeof(uint)) return LibraDexGenericScalarCodec<uint>.Decode8(encoded);
            if (targetType == typeof(char)) return LibraDexGenericScalarCodec<char>.Decode8(encoded);
            return LibraDexGenericScalarCodec<ulong>.Decode8(encoded);
        }

        return (coercion, bytes.Length) switch
        {
            (Coercion.Numeric.DotNet, 1) => bytes[0],
            (Coercion.Numeric.DotNet, 2) => BinaryPrimitives.ReadUInt16LittleEndian(bytes),
            (Coercion.Numeric.DotNet, 4) => BinaryPrimitives.ReadUInt32LittleEndian(bytes),
            (Coercion.Numeric.DotNet, 8) => BinaryPrimitives.ReadUInt64LittleEndian(bytes),
            (Coercion.Numeric.DotNet, 16) => BinaryPrimitives.ReadUInt128LittleEndian(bytes),
            (Coercion.Numeric.LibraDex, 16) => LibraDexGenericScalarCodec<UInt128>.Decode16(
                BinaryPrimitives.ReadUInt64BigEndian(bytes),
                BinaryPrimitives.ReadUInt64BigEndian(bytes[sizeof(ulong)..])),
            _ => throw new InvalidDataException($"{coercion} unsigned numeric coercion does not support a {bytes.Length}-byte source.")
        };
    }

    private static object ConvertSigned(Int128 value, Type targetType)
    {
        checked
        {
            if (targetType == typeof(sbyte)) return (sbyte)value;
            if (targetType == typeof(short)) return (short)value;
            if (targetType == typeof(int)) return (int)value;
            if (targetType == typeof(long)) return (long)value;
            if (targetType == typeof(Int128)) return value;
        }

        throw new NotSupportedException($"Signed numeric projection does not support '{targetType.FullName}'.");
    }

    private static object ConvertUnsigned(UInt128 value, Type targetType)
    {
        checked
        {
            if (targetType == typeof(byte)) return (byte)value;
            if (targetType == typeof(ushort)) return (ushort)value;
            if (targetType == typeof(uint)) return (uint)value;
            if (targetType == typeof(ulong)) return (ulong)value;
            if (targetType == typeof(UInt128)) return value;
            if (targetType == typeof(char)) return (char)value;
        }

        throw new NotSupportedException($"Unsigned numeric projection does not support '{targetType.FullName}'.");
    }

    private static void RequireWidth(
        Type targetType,
        Coercion.Numeric coercion,
        ReadOnlySpan<byte> bytes,
        int required)
    {
        if (bytes.Length != required)
            throw WidthException(targetType, coercion, bytes.Length, required);
    }

    private static InvalidDataException WidthException(
        Type targetType,
        Coercion.Numeric coercion,
        int actual,
        int required)
        => new($"{coercion} {targetType.Name} coercion requires exactly {required} bytes, not {actual}.");

    private static byte[]? EncodeRawKey(IIndex index, object? value)
    {
        if (value is null)
        {
            return index.KeyFamily == CatalogIndexKeyFamily.String
                ? LibraDexStringScalar8Index.EncodeGroupingKey(null)
                : null;
        }
        if (value is string text)
            return LibraDexStringScalar8Index.EncodeGroupingKey(text);
        if (value is byte[] bytes)
            return bytes;
        if (value is byte u8)
            return Encode8(u8);
        if (value is sbyte i8)
            return Encode8(i8);
        if (value is short i16)
            return Encode8(i16);
        if (value is ushort u16)
            return Encode8(u16);
        if (value is int i32)
            return Encode8(i32);
        if (value is uint u32)
            return Encode8(u32);
        if (value is long i64)
            return Encode8(i64);
        if (value is ulong u64)
            return Encode8(u64);
        if (value is char character)
            return Encode8(character);
        if (value is bool boolean)
            return Encode8(boolean);
        if (value is float single)
            return Encode8(single);
        if (value is double @double)
            return Encode8(@double);
        if (value is DateTime dateTime)
            return Encode8(dateTime, index.DateTimeKeyEncoding);
        if (value is DateOnly dateOnly)
            return Encode8(dateOnly);
        if (value is TimeOnly timeOnly)
            return Encode8(timeOnly);
        if (value is TimeSpan timeSpan)
            return Encode8(timeSpan);
        if (value is Guid guid)
            return Encode16(guid);
        if (value is decimal @decimal)
            return Encode16(@decimal);
        if (value is Int128 i128)
            return Encode16(i128);
        if (value is UInt128 u128)
            return Encode16(u128);
        if (value is DateTimeOffset dateTimeOffset)
            return Encode16(dateTimeOffset);

        throw new NotSupportedException(
            $"Index '{index.Group}/{index.Name}' key type '{value.GetType().FullName}' does not yet expose raw duplicate-key encoding.");
    }

    private static byte[] Encode8<T>(T value, DateTimeKeyEncoding dateTimeKeyEncoding = DateTimeKeyEncoding.CalendarSdt)
    {
        byte[] bytes = GC.AllocateUninitializedArray<byte>(sizeof(ulong));
        BinaryPrimitives.WriteUInt64BigEndian(bytes, LibraDexGenericScalarCodec<T>.Encode8(value, dateTimeKeyEncoding));
        return bytes;
    }

    private static byte[] Encode16<T>(T value)
    {
        byte[] bytes = GC.AllocateUninitializedArray<byte>(sizeof(ulong) * 2);
        LibraDexGenericScalarCodec<T>.Encode16(value, out ulong high, out ulong low);
        BinaryPrimitives.WriteUInt64BigEndian(bytes, high);
        BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(sizeof(ulong)), low);
        return bytes;
    }
}

internal readonly record struct LibraDexRawTuple(byte[]? Key, object Identity);

internal readonly record struct LibraDexDuplicateValue(object? Value);

internal sealed class LibraDexDuplicateValueComparer : IEqualityComparer<LibraDexDuplicateValue>
{
    internal static readonly LibraDexDuplicateValueComparer Instance = new();

    public bool Equals(LibraDexDuplicateValue x, LibraDexDuplicateValue y)
        => LibraDexObjectTuple.ValueEquals(x.Value, y.Value);

    public int GetHashCode(LibraDexDuplicateValue obj)
    {
        if (obj.Value is null)
            return 0;
        return LibraDexObjectValueComparer.Instance.GetHashCode(obj.Value);
    }
}
