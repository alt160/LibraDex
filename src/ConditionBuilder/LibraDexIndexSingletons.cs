using System.Numerics;

namespace LibraDex;

/// <summary>
/// Provides non-generic singleton-key results for an <see cref="IIndex"/>.<br/>
/// A key qualifies when it is associated with exactly one distinct identity; repeated identical tuples do not change that cardinality.<br/>
/// </summary>
public readonly struct LibraDexUntypedKeySingletons
{
    private readonly IIndex index;

    internal LibraDexUntypedKeySingletons(IIndex index)
    {
        this.index = index;
    }

    /// <summary>
    /// Materializes distinct raw key bytes associated with exactly one distinct identity.<br/>
    /// Detection uses exact physical key equality and preserves dedicated null and empty key routes.<br/>
    /// </summary>
    public IReadOnlyList<byte[]?> Get()
        => LibraDexDuplicateExecution.IterateSingletonRawKeys(index).ToList();

    /// <summary>
    /// Opens a forward-only reader over distinct raw keys associated with exactly one distinct identity.<br/>
    /// </summary>
    public LibraDexSingletonReader<byte[]?> OpenReader()
        => new(LibraDexDuplicateExecution.IterateSingletonRawKeys(index));

    /// <summary>
    /// Projects exact singleton key bytes as strings after singleton selection.<br/>
    /// The projection changes only the returned representation; singleton equality remains exact physical byte equality.<br/>
    /// </summary>
    public LibraDexProjectedSingletonKeys<string?> AsString
        => new(index, LibraDexKeyProjection.String(), singletonsFirst: true);

    /// <summary>Projects singleton key bytes as Byte using ordinary .NET layout.<br/></summary>
    public LibraDexProjectedSingletonKeys<byte> AsByte() => AsByte(Coercion.Numeric.DotNet);
    /// <summary>Projects singleton key bytes as Byte using the selected numeric coercion.<br/></summary>
    public LibraDexProjectedSingletonKeys<byte> AsByte(Coercion.Numeric coercion) => Numeric<byte>(coercion);
    /// <summary>Projects singleton key bytes as SByte using ordinary .NET layout.<br/></summary>
    public LibraDexProjectedSingletonKeys<sbyte> AsSByte() => AsSByte(Coercion.Numeric.DotNet);
    /// <summary>Projects singleton key bytes as SByte using the selected numeric coercion.<br/></summary>
    public LibraDexProjectedSingletonKeys<sbyte> AsSByte(Coercion.Numeric coercion) => Numeric<sbyte>(coercion);
    /// <summary>Projects singleton key bytes as Int16 using ordinary .NET layout.<br/></summary>
    public LibraDexProjectedSingletonKeys<short> AsInt16() => AsInt16(Coercion.Numeric.DotNet);
    /// <summary>Projects singleton key bytes as Int16 using the selected numeric coercion.<br/></summary>
    public LibraDexProjectedSingletonKeys<short> AsInt16(Coercion.Numeric coercion) => Numeric<short>(coercion);
    /// <summary>Projects singleton key bytes as UInt16 using ordinary .NET layout.<br/></summary>
    public LibraDexProjectedSingletonKeys<ushort> AsUInt16() => AsUInt16(Coercion.Numeric.DotNet);
    /// <summary>Projects singleton key bytes as UInt16 using the selected numeric coercion.<br/></summary>
    public LibraDexProjectedSingletonKeys<ushort> AsUInt16(Coercion.Numeric coercion) => Numeric<ushort>(coercion);
    /// <summary>Projects singleton key bytes as Int32 using ordinary .NET layout.<br/></summary>
    public LibraDexProjectedSingletonKeys<int> AsInt32() => AsInt32(Coercion.Numeric.DotNet);
    /// <summary>Projects singleton key bytes as Int32 using the selected numeric coercion.<br/></summary>
    public LibraDexProjectedSingletonKeys<int> AsInt32(Coercion.Numeric coercion) => Numeric<int>(coercion);
    /// <summary>Projects singleton key bytes as UInt32 using ordinary .NET layout.<br/></summary>
    public LibraDexProjectedSingletonKeys<uint> AsUInt32() => AsUInt32(Coercion.Numeric.DotNet);
    /// <summary>Projects singleton key bytes as UInt32 using the selected numeric coercion.<br/></summary>
    public LibraDexProjectedSingletonKeys<uint> AsUInt32(Coercion.Numeric coercion) => Numeric<uint>(coercion);
    /// <summary>Projects singleton key bytes as Int64 using ordinary .NET layout.<br/></summary>
    public LibraDexProjectedSingletonKeys<long> AsInt64() => AsInt64(Coercion.Numeric.DotNet);
    /// <summary>Projects singleton key bytes as Int64 using the selected numeric coercion.<br/></summary>
    public LibraDexProjectedSingletonKeys<long> AsInt64(Coercion.Numeric coercion) => Numeric<long>(coercion);
    /// <summary>Projects singleton key bytes as UInt64 using ordinary .NET layout.<br/></summary>
    public LibraDexProjectedSingletonKeys<ulong> AsUInt64() => AsUInt64(Coercion.Numeric.DotNet);
    /// <summary>Projects singleton key bytes as UInt64 using the selected numeric coercion.<br/></summary>
    public LibraDexProjectedSingletonKeys<ulong> AsUInt64(Coercion.Numeric coercion) => Numeric<ulong>(coercion);
    /// <summary>Projects singleton key bytes as Int128 and permits checked widening from shorter integral sources.<br/></summary>
    public LibraDexProjectedSingletonKeys<Int128> AsInt128() => AsInt128(Coercion.Numeric.DotNet);
    /// <summary>Projects singleton key bytes as Int128 using the selected numeric coercion.<br/></summary>
    public LibraDexProjectedSingletonKeys<Int128> AsInt128(Coercion.Numeric coercion) => Numeric<Int128>(coercion);
    /// <summary>Projects singleton key bytes as UInt128 and permits checked widening from shorter integral sources.<br/></summary>
    public LibraDexProjectedSingletonKeys<UInt128> AsUInt128() => AsUInt128(Coercion.Numeric.DotNet);
    /// <summary>Projects singleton key bytes as UInt128 using the selected numeric coercion.<br/></summary>
    public LibraDexProjectedSingletonKeys<UInt128> AsUInt128(Coercion.Numeric coercion) => Numeric<UInt128>(coercion);
    /// <summary>Projects singleton key bytes as Single using ordinary .NET layout.<br/></summary>
    public LibraDexProjectedSingletonKeys<float> AsSingle() => AsSingle(Coercion.Numeric.DotNet);
    /// <summary>Projects singleton key bytes as Single using the selected numeric coercion.<br/></summary>
    public LibraDexProjectedSingletonKeys<float> AsSingle(Coercion.Numeric coercion) => Numeric<float>(coercion);
    /// <summary>Projects singleton key bytes as Double using ordinary .NET layout.<br/></summary>
    public LibraDexProjectedSingletonKeys<double> AsDouble() => AsDouble(Coercion.Numeric.DotNet);
    /// <summary>Projects singleton key bytes as Double using the selected numeric coercion.<br/></summary>
    public LibraDexProjectedSingletonKeys<double> AsDouble(Coercion.Numeric coercion) => Numeric<double>(coercion);
    /// <summary>Projects singleton key bytes as Decimal using ordinary .NET layout.<br/></summary>
    public LibraDexProjectedSingletonKeys<decimal> AsDecimal() => AsDecimal(Coercion.Numeric.DotNet);
    /// <summary>Projects singleton key bytes as Decimal using the selected numeric coercion.<br/></summary>
    public LibraDexProjectedSingletonKeys<decimal> AsDecimal(Coercion.Numeric coercion) => Numeric<decimal>(coercion);
    /// <summary>Projects singleton key bytes as BigInteger using ordinary .NET layout.<br/></summary>
    public LibraDexProjectedSingletonKeys<BigInteger> AsBigInt() => AsBigInt(Coercion.Numeric.DotNet);
    /// <summary>Projects singleton key bytes as BigInteger using the selected numeric coercion.<br/></summary>
    public LibraDexProjectedSingletonKeys<BigInteger> AsBigInt(Coercion.Numeric coercion) => Numeric<BigInteger>(coercion);
    /// <summary>Projects singleton key bytes as Char using ordinary .NET integral layout.<br/></summary>
    public LibraDexProjectedSingletonKeys<char> AsChar() => AsChar(Coercion.Numeric.DotNet);
    /// <summary>Projects singleton key bytes as Char using the selected numeric coercion.<br/></summary>
    public LibraDexProjectedSingletonKeys<char> AsChar(Coercion.Numeric coercion) => Numeric<char>(coercion);

    private LibraDexProjectedSingletonKeys<TValue> Numeric<TValue>(Coercion.Numeric coercion)
        => new(index, LibraDexKeyProjection.Numeric<TValue>(coercion), singletonsFirst: true);

    /// <summary>Projects singleton key bytes as DateTime using the explicitly selected physical representation.<br/></summary>
    public LibraDexProjectedSingletonKeys<DateTime> AsDateTime(Coercion.DateTime coercion) => Temporal<DateTime>(coercion);
    /// <summary>Projects singleton key bytes as DateTimeOffset using the explicitly selected physical representation.<br/></summary>
    public LibraDexProjectedSingletonKeys<DateTimeOffset> AsDateTimeOffset(Coercion.DateTimeOffset coercion) => Temporal<DateTimeOffset>(coercion);
    /// <summary>Projects singleton key bytes as DateOnly using the explicitly selected physical representation.<br/></summary>
    public LibraDexProjectedSingletonKeys<DateOnly> AsDateOnly(Coercion.DateOnly coercion) => Temporal<DateOnly>(coercion);
    /// <summary>Projects singleton key bytes as TimeOnly using the explicitly selected physical representation.<br/></summary>
    public LibraDexProjectedSingletonKeys<TimeOnly> AsTimeOnly(Coercion.TimeOnly coercion) => Temporal<TimeOnly>(coercion);
    /// <summary>Projects singleton key bytes as TimeSpan using the explicitly selected physical representation.<br/></summary>
    public LibraDexProjectedSingletonKeys<TimeSpan> AsTimeSpan(Coercion.TimeSpan coercion) => Temporal<TimeSpan>(coercion);

    private LibraDexProjectedSingletonKeys<TValue> Temporal<TValue>(object coercion)
        => new(index, LibraDexKeyProjection.Temporal<TValue>(coercion), singletonsFirst: true);

    /// <summary>
    /// Selects a bounded byte slice from each exact singleton key before choosing its returned projection.<br/>
    /// Singleton equality remains over the complete physical key because selection has already occurred.<br/>
    /// </summary>
    /// <param name="offset">The zero-based byte offset inside each singleton key.<br/></param>
    /// <param name="length">The required slice length in bytes.<br/></param>
    /// <returns>A post-singleton slice projection selector.<br/></returns>
    public LibraDexSingletonKeySlice Slice(int offset, int length)
        => new(index, offset, length);
}

/// <summary>
/// Selects a byte range from exact singleton keys before choosing the returned CLR projection.<br/>
/// Singleton equality has already been evaluated over each complete physical key.<br/>
/// </summary>
public readonly struct LibraDexSingletonKeySlice
{
    private readonly IIndex index;
    private readonly int offset;
    private readonly int length;

    internal LibraDexSingletonKeySlice(IIndex index, int offset, int length)
    {
        if (offset < 0)
            throw new ArgumentOutOfRangeException(nameof(offset), offset, "A key-slice offset cannot be negative.");
        if (length <= 0)
            throw new ArgumentOutOfRangeException(nameof(length), length, "A key-slice length must be positive.");
        this.index = index;
        this.offset = offset;
        this.length = length;
    }

    /// <summary>Projects the selected singleton bytes as UTF-8 text.<br/></summary>
    public LibraDexProjectedSingletonKeys<string?> AsString
        => new(index, LibraDexKeyProjection.String(offset, length), singletonsFirst: true);

    /// <summary>Projects the selected singleton bytes as Byte using ordinary .NET layout.<br/></summary>
    public LibraDexProjectedSingletonKeys<byte> AsByte() => AsByte(Coercion.Numeric.DotNet);
    /// <summary>Projects the selected singleton bytes as Byte using the selected numeric coercion.<br/></summary>
    public LibraDexProjectedSingletonKeys<byte> AsByte(Coercion.Numeric coercion) => Numeric<byte>(coercion);
    /// <summary>Projects the selected singleton bytes as SByte using ordinary .NET layout.<br/></summary>
    public LibraDexProjectedSingletonKeys<sbyte> AsSByte() => AsSByte(Coercion.Numeric.DotNet);
    /// <summary>Projects the selected singleton bytes as SByte using the selected numeric coercion.<br/></summary>
    public LibraDexProjectedSingletonKeys<sbyte> AsSByte(Coercion.Numeric coercion) => Numeric<sbyte>(coercion);
    /// <summary>Projects the selected singleton bytes as Int16 using ordinary .NET layout.<br/></summary>
    public LibraDexProjectedSingletonKeys<short> AsInt16() => AsInt16(Coercion.Numeric.DotNet);
    /// <summary>Projects the selected singleton bytes as Int16 using the selected numeric coercion.<br/></summary>
    public LibraDexProjectedSingletonKeys<short> AsInt16(Coercion.Numeric coercion) => Numeric<short>(coercion);
    /// <summary>Projects the selected singleton bytes as UInt16 using ordinary .NET layout.<br/></summary>
    public LibraDexProjectedSingletonKeys<ushort> AsUInt16() => AsUInt16(Coercion.Numeric.DotNet);
    /// <summary>Projects the selected singleton bytes as UInt16 using the selected numeric coercion.<br/></summary>
    public LibraDexProjectedSingletonKeys<ushort> AsUInt16(Coercion.Numeric coercion) => Numeric<ushort>(coercion);
    /// <summary>Projects the selected singleton bytes as Int32 using ordinary .NET layout.<br/></summary>
    public LibraDexProjectedSingletonKeys<int> AsInt32() => AsInt32(Coercion.Numeric.DotNet);
    /// <summary>Projects the selected singleton bytes as Int32 using the selected numeric coercion.<br/></summary>
    public LibraDexProjectedSingletonKeys<int> AsInt32(Coercion.Numeric coercion) => Numeric<int>(coercion);
    /// <summary>Projects the selected singleton bytes as UInt32 using ordinary .NET layout.<br/></summary>
    public LibraDexProjectedSingletonKeys<uint> AsUInt32() => AsUInt32(Coercion.Numeric.DotNet);
    /// <summary>Projects the selected singleton bytes as UInt32 using the selected numeric coercion.<br/></summary>
    public LibraDexProjectedSingletonKeys<uint> AsUInt32(Coercion.Numeric coercion) => Numeric<uint>(coercion);
    /// <summary>Projects the selected singleton bytes as Int64 using ordinary .NET layout.<br/></summary>
    public LibraDexProjectedSingletonKeys<long> AsInt64() => AsInt64(Coercion.Numeric.DotNet);
    /// <summary>Projects the selected singleton bytes as Int64 using the selected numeric coercion.<br/></summary>
    public LibraDexProjectedSingletonKeys<long> AsInt64(Coercion.Numeric coercion) => Numeric<long>(coercion);
    /// <summary>Projects the selected singleton bytes as UInt64 using ordinary .NET layout.<br/></summary>
    public LibraDexProjectedSingletonKeys<ulong> AsUInt64() => AsUInt64(Coercion.Numeric.DotNet);
    /// <summary>Projects the selected singleton bytes as UInt64 using the selected numeric coercion.<br/></summary>
    public LibraDexProjectedSingletonKeys<ulong> AsUInt64(Coercion.Numeric coercion) => Numeric<ulong>(coercion);
    /// <summary>Projects the selected singleton bytes as Int128 and permits checked widening from shorter integral sources.<br/></summary>
    public LibraDexProjectedSingletonKeys<Int128> AsInt128() => AsInt128(Coercion.Numeric.DotNet);
    /// <summary>Projects the selected singleton bytes as Int128 using the selected numeric coercion.<br/></summary>
    public LibraDexProjectedSingletonKeys<Int128> AsInt128(Coercion.Numeric coercion) => Numeric<Int128>(coercion);
    /// <summary>Projects the selected singleton bytes as UInt128 and permits checked widening from shorter integral sources.<br/></summary>
    public LibraDexProjectedSingletonKeys<UInt128> AsUInt128() => AsUInt128(Coercion.Numeric.DotNet);
    /// <summary>Projects the selected singleton bytes as UInt128 using the selected numeric coercion.<br/></summary>
    public LibraDexProjectedSingletonKeys<UInt128> AsUInt128(Coercion.Numeric coercion) => Numeric<UInt128>(coercion);
    /// <summary>Projects the selected singleton bytes as Single using ordinary .NET layout.<br/></summary>
    public LibraDexProjectedSingletonKeys<float> AsSingle() => AsSingle(Coercion.Numeric.DotNet);
    /// <summary>Projects the selected singleton bytes as Single using the selected numeric coercion.<br/></summary>
    public LibraDexProjectedSingletonKeys<float> AsSingle(Coercion.Numeric coercion) => Numeric<float>(coercion);
    /// <summary>Projects the selected singleton bytes as Double using ordinary .NET layout.<br/></summary>
    public LibraDexProjectedSingletonKeys<double> AsDouble() => AsDouble(Coercion.Numeric.DotNet);
    /// <summary>Projects the selected singleton bytes as Double using the selected numeric coercion.<br/></summary>
    public LibraDexProjectedSingletonKeys<double> AsDouble(Coercion.Numeric coercion) => Numeric<double>(coercion);
    /// <summary>Projects the selected singleton bytes as Decimal using ordinary .NET layout.<br/></summary>
    public LibraDexProjectedSingletonKeys<decimal> AsDecimal() => AsDecimal(Coercion.Numeric.DotNet);
    /// <summary>Projects the selected singleton bytes as Decimal using the selected numeric coercion.<br/></summary>
    public LibraDexProjectedSingletonKeys<decimal> AsDecimal(Coercion.Numeric coercion) => Numeric<decimal>(coercion);
    /// <summary>Projects the selected singleton bytes as BigInteger using ordinary .NET layout.<br/></summary>
    public LibraDexProjectedSingletonKeys<BigInteger> AsBigInt() => AsBigInt(Coercion.Numeric.DotNet);
    /// <summary>Projects the selected singleton bytes as BigInteger using the selected numeric coercion.<br/></summary>
    public LibraDexProjectedSingletonKeys<BigInteger> AsBigInt(Coercion.Numeric coercion) => Numeric<BigInteger>(coercion);
    /// <summary>Projects the selected singleton bytes as Char using ordinary .NET integral layout.<br/></summary>
    public LibraDexProjectedSingletonKeys<char> AsChar() => AsChar(Coercion.Numeric.DotNet);
    /// <summary>Projects the selected singleton bytes as Char using the selected numeric coercion.<br/></summary>
    public LibraDexProjectedSingletonKeys<char> AsChar(Coercion.Numeric coercion) => Numeric<char>(coercion);

    private LibraDexProjectedSingletonKeys<TValue> Numeric<TValue>(Coercion.Numeric coercion)
        => new(index, LibraDexKeyProjection.Numeric<TValue>(coercion, offset, length), singletonsFirst: true);

    /// <summary>Projects the selected singleton bytes as DateTime using the explicitly selected physical representation.<br/></summary>
    public LibraDexProjectedSingletonKeys<DateTime> AsDateTime(Coercion.DateTime coercion) => Temporal<DateTime>(coercion);
    /// <summary>Projects the selected singleton bytes as DateTimeOffset using the explicitly selected physical representation.<br/></summary>
    public LibraDexProjectedSingletonKeys<DateTimeOffset> AsDateTimeOffset(Coercion.DateTimeOffset coercion) => Temporal<DateTimeOffset>(coercion);
    /// <summary>Projects the selected singleton bytes as DateOnly using the explicitly selected physical representation.<br/></summary>
    public LibraDexProjectedSingletonKeys<DateOnly> AsDateOnly(Coercion.DateOnly coercion) => Temporal<DateOnly>(coercion);
    /// <summary>Projects the selected singleton bytes as TimeOnly using the explicitly selected physical representation.<br/></summary>
    public LibraDexProjectedSingletonKeys<TimeOnly> AsTimeOnly(Coercion.TimeOnly coercion) => Temporal<TimeOnly>(coercion);
    /// <summary>Projects the selected singleton bytes as TimeSpan using the explicitly selected physical representation.<br/></summary>
    public LibraDexProjectedSingletonKeys<TimeSpan> AsTimeSpan(Coercion.TimeSpan coercion) => Temporal<TimeSpan>(coercion);

    private LibraDexProjectedSingletonKeys<TValue> Temporal<TValue>(object coercion)
        => new(index, LibraDexKeyProjection.Temporal<TValue>(coercion, offset, length), singletonsFirst: true);
}

/// <summary>
/// Provides projected singleton-key collection and reader operations.<br/>
/// The fluent stage remembers whether projection occurred before or after singleton selection so equality and returned representation remain explicit.<br/>
/// </summary>
/// <typeparam name="TValue">The projected singleton-key result type.<br/></typeparam>
public readonly struct LibraDexProjectedSingletonKeys<TValue>
{
    private readonly IIndex index;
    private readonly LibraDexKeyProjection projection;
    private readonly bool singletonsFirst;

    internal LibraDexProjectedSingletonKeys(IIndex index, LibraDexKeyProjection projection, bool singletonsFirst)
    {
        this.index = index;
        this.projection = projection;
        this.singletonsFirst = singletonsFirst;
    }

    /// <summary>
    /// Materializes projected singleton keys using the equality order expressed by the fluent chain.<br/>
    /// </summary>
    public IReadOnlyList<TValue> Get()
        => singletonsFirst
            ? LibraDexDuplicateExecution.ProjectRawSingletonKeys<TValue>(index, projection)
            : LibraDexDuplicateExecution.SingletonProjectedKeys<TValue>(index, projection);

    /// <summary>
    /// Opens a forward-only reader over projected singleton keys using the equality order expressed by the fluent chain.<br/>
    /// </summary>
    public LibraDexSingletonReader<TValue> OpenReader()
        => new(Get());
}

/// <summary>
/// Provides non-generic singleton-identity results for an <see cref="IIndex"/>.<br/>
/// </summary>
public readonly struct LibraDexUntypedIdentitySingletons
{
    private readonly IIndex index;

    internal LibraDexUntypedIdentitySingletons(IIndex index)
    {
        this.index = index;
    }

    /// <summary>
    /// Materializes distinct runtime identities associated with exactly one distinct key.<br/>
    /// Configured identity inversion is used when available; otherwise the forward index is walked under the configured lookup policy.<br/>
    /// </summary>
    public IReadOnlyList<object> Get()
        => LibraDexDuplicateExecution.SingletonIdentities<object>(index).ToList();

    /// <summary>
    /// Opens a forward-only reader over distinct runtime identities associated with exactly one distinct key.<br/>
    /// </summary>
    public LibraDexSingletonReader<object> OpenReader()
        => new(LibraDexDuplicateExecution.SingletonIdentities<object>(index));
}

/// <summary>
/// Provides non-generic complete entries selected by singleton runtime key.<br/>
/// </summary>
public readonly struct LibraDexUntypedEntriesBySingletonKey
{
    private readonly IIndex index;

    internal LibraDexUntypedEntriesBySingletonKey(IIndex index)
    {
        this.index = index;
    }

    /// <summary>
    /// Materializes every runtime key/identity entry belonging to a key associated with exactly one distinct identity.<br/>
    /// </summary>
    public IReadOnlyList<LibraDexIndexEntry<byte[]?, object>> Get()
        => LibraDexDuplicateExecution.RawEntriesBySingletonKey(index).ToList();

    /// <summary>
    /// Opens a forward-only reader over every runtime entry belonging to a singleton key.<br/>
    /// </summary>
    public LibraDexSingletonReader<LibraDexIndexEntry<byte[]?, object>> OpenReader()
        => new(LibraDexDuplicateExecution.RawEntriesBySingletonKey(index));
}

/// <summary>
/// Provides non-generic complete entries selected by singleton runtime identity.<br/>
/// </summary>
public readonly struct LibraDexUntypedEntriesBySingletonIdentity
{
    private readonly IIndex index;

    internal LibraDexUntypedEntriesBySingletonIdentity(IIndex index)
    {
        this.index = index;
    }

    /// <summary>
    /// Materializes every runtime key/identity entry belonging to an identity associated with exactly one distinct key.<br/>
    /// </summary>
    public IReadOnlyList<LibraDexIndexEntry<byte[]?, object>> Get()
        => LibraDexDuplicateExecution.RawEntriesBySingletonIdentity(index).ToList();

    /// <summary>
    /// Opens a forward-only reader over every runtime entry belonging to a singleton identity.<br/>
    /// </summary>
    public LibraDexSingletonReader<LibraDexIndexEntry<byte[]?, object>> OpenReader()
        => new(LibraDexDuplicateExecution.RawEntriesBySingletonIdentity(index));
}

/// <summary>
/// Provides a forward-only reader over singleton-analysis results.<br/>
/// The reader does not materialize the returned result collection, although discovery may retain compact cardinality state until a scan completes.<br/>
/// </summary>
/// <typeparam name="TValue">The value returned for each singleton result.<br/></typeparam>
public sealed class LibraDexSingletonReader<TValue> : IDisposable
{
    private IEnumerator<TValue>? reader;

    internal LibraDexSingletonReader(IEnumerable<TValue> source)
    {
        ArgumentNullException.ThrowIfNull(source);
        reader = source.GetEnumerator();
    }

    /// <summary>
    /// Gets the current singleton-analysis result.<br/>
    /// The value is valid only after <see cref="Read"/> returns <see langword="true"/> and before the next read or disposal.<br/>
    /// </summary>
    public TValue Current { get; private set; } = default!;

    /// <summary>
    /// Advances to the next singleton-analysis result.<br/>
    /// The forward index may be scanned before the first result can be proven, especially for identity-oriented discovery without a current inversion.<br/>
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
    /// Advances to the next singleton-analysis result.<br/>
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
/// Provides singleton-key collection and reader access for one typed index.<br/>
/// </summary>
public readonly struct LibraDexIndexKeySingletons<TKey, TIdentity>
{
    private readonly IIndex index;

    internal LibraDexIndexKeySingletons(IIndex index)
    {
        this.index = index;
    }

    /// <summary>
    /// Materializes distinct keys associated with exactly one distinct identity.<br/>
    /// </summary>
    public IReadOnlyList<TKey> Get()
        => LibraDexDuplicateExecution.SingletonKeys<TKey>(index).ToList();

    /// <summary>
    /// Opens a forward-only singleton-key reader.<br/>
    /// </summary>
    public LibraDexSingletonReader<TKey> OpenReader()
        => new(LibraDexDuplicateExecution.SingletonKeys<TKey>(index));
}

/// <summary>
/// Provides singleton-identity collection and reader access for one typed index.<br/>
/// </summary>
public readonly struct LibraDexIndexIdentitySingletons<TKey, TIdentity>
{
    private readonly IIndex index;

    internal LibraDexIndexIdentitySingletons(IIndex index)
    {
        this.index = index;
    }

    /// <summary>
    /// Materializes distinct identities associated with exactly one distinct key.<br/>
    /// Configured identity inversion is preferred and the forward index is used as the policy-controlled fallback.<br/>
    /// </summary>
    public IReadOnlyList<TIdentity> Get()
        => LibraDexDuplicateExecution.SingletonIdentities<TIdentity>(index).ToList();

    /// <summary>
    /// Opens a forward-only singleton-identity reader.<br/>
    /// </summary>
    public LibraDexSingletonReader<TIdentity> OpenReader()
        => new(LibraDexDuplicateExecution.SingletonIdentities<TIdentity>(index));
}

/// <summary>
/// Provides complete entry tuples for keys associated with exactly one distinct identity.<br/>
/// </summary>
public readonly struct LibraDexEntriesBySingletonKey<TKey, TIdentity>
{
    private readonly IIndex index;

    internal LibraDexEntriesBySingletonKey(IIndex index)
    {
        this.index = index;
    }

    /// <summary>
    /// Materializes every key/identity entry belonging to a singleton key.<br/>
    /// </summary>
    public IReadOnlyList<LibraDexIndexEntry<TKey, TIdentity>> Get()
        => LibraDexDuplicateExecution.EntriesBySingletonKey<TKey, TIdentity>(index).ToList();

    /// <summary>
    /// Opens a forward-only reader over every key/identity entry belonging to a singleton key.<br/>
    /// </summary>
    public LibraDexSingletonReader<LibraDexIndexEntry<TKey, TIdentity>> OpenReader()
        => new(LibraDexDuplicateExecution.EntriesBySingletonKey<TKey, TIdentity>(index));
}

/// <summary>
/// Provides complete entry tuples for identities associated with exactly one distinct key.<br/>
/// </summary>
public readonly struct LibraDexEntriesBySingletonIdentity<TKey, TIdentity>
{
    private readonly IIndex index;

    internal LibraDexEntriesBySingletonIdentity(IIndex index)
    {
        this.index = index;
    }

    /// <summary>
    /// Materializes every key/identity entry belonging to a singleton identity.<br/>
    /// </summary>
    public IReadOnlyList<LibraDexIndexEntry<TKey, TIdentity>> Get()
        => LibraDexDuplicateExecution.EntriesBySingletonIdentity<TKey, TIdentity>(index).ToList();

    /// <summary>
    /// Opens a forward-only reader over every key/identity entry belonging to a singleton identity.<br/>
    /// </summary>
    public LibraDexSingletonReader<LibraDexIndexEntry<TKey, TIdentity>> OpenReader()
        => new(LibraDexDuplicateExecution.EntriesBySingletonIdentity<TKey, TIdentity>(index));
}

internal static partial class LibraDexDuplicateExecution
{
    internal static IEnumerable<byte[]?> IterateSingletonRawKeys(IIndex index)
    {
        ArgumentNullException.ThrowIfNull(index);
        if (index is LibraDexStringScalar8Index stringIndex)
        {
            foreach (byte[]? key in stringIndex.IterateRawSingletonKeys())
                yield return key;
            yield break;
        }

        Dictionary<LibraDexDuplicateValue, HashSet<LibraDexDuplicateValue>> groups = BuildGroups(index, byKey: true);
        foreach ((LibraDexDuplicateValue key, HashSet<LibraDexDuplicateValue> identities) in groups)
        {
            if (identities.Count == 1)
                yield return EncodeRawKey(index, key.Value);
        }
    }

    internal static IReadOnlyList<TValue> ProjectRawSingletonKeys<TValue>(
        IIndex index,
        LibraDexKeyProjection projection)
    {
        List<TValue> results = new();
        foreach (byte[]? singleton in IterateSingletonRawKeys(index))
            results.Add(Project<TValue>(index, singleton, projection));
        return results;
    }

    internal static IReadOnlyList<TValue> SingletonProjectedKeys<TValue>(
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
            if (identities.Count == 1)
                results.Add(ConvertValue<TValue>(value.Value, "projected key"));
        }

        return results;
    }

    internal static IEnumerable<TKey> SingletonKeys<TKey>(IIndex index)
    {
        Dictionary<LibraDexDuplicateValue, HashSet<LibraDexDuplicateValue>> groups = BuildGroups(index, byKey: true);
        foreach ((LibraDexDuplicateValue key, HashSet<LibraDexDuplicateValue> identities) in groups)
        {
            if (identities.Count == 1)
                yield return ConvertValue<TKey>(key.Value, "key");
        }
    }

    internal static IEnumerable<TIdentity> SingletonIdentities<TIdentity>(IIndex index)
    {
        if (index.Catalog.TryGetSingletonIdentities(index, out object[] inverseIdentities))
        {
            for (int i = 0; i < inverseIdentities.Length; i++)
                yield return ConvertValue<TIdentity>(inverseIdentities[i], "identity");
            yield break;
        }

        Dictionary<LibraDexDuplicateValue, HashSet<LibraDexDuplicateValue>> groups = BuildGroups(index, byKey: false);
        foreach ((LibraDexDuplicateValue identity, HashSet<LibraDexDuplicateValue> keys) in groups)
        {
            if (keys.Count == 1)
                yield return ConvertValue<TIdentity>(identity.Value, "identity");
        }
    }

    internal static IEnumerable<LibraDexIndexEntry<TKey, TIdentity>> EntriesBySingletonKey<TKey, TIdentity>(IIndex index)
    {
        HashSet<LibraDexDuplicateValue> singletons = FindSingletonValues(index, byKey: true);
        if (singletons.Count == 0)
            yield break;

        foreach (LibraDexObjectTuple tuple in Iterate(index))
        {
            if (singletons.Contains(new LibraDexDuplicateValue(tuple.Key)))
            {
                yield return new LibraDexIndexEntry<TKey, TIdentity>(
                    ConvertValue<TKey>(tuple.Key, "key"),
                    ConvertValue<TIdentity>(tuple.Identity, "identity"));
            }
        }
    }

    internal static IEnumerable<LibraDexIndexEntry<TKey, TIdentity>> EntriesBySingletonIdentity<TKey, TIdentity>(IIndex index)
    {
        if (index.Catalog.TryGetEntriesForSingletonIdentities(index, out LibraDexObjectTuple[] inverseEntries))
        {
            for (int i = 0; i < inverseEntries.Length; i++)
            {
                yield return new LibraDexIndexEntry<TKey, TIdentity>(
                    ConvertValue<TKey>(inverseEntries[i].Key, "key"),
                    ConvertValue<TIdentity>(inverseEntries[i].Identity, "identity"));
            }

            yield break;
        }

        HashSet<LibraDexDuplicateValue> singletons = FindSingletonValues(index, byKey: false);
        if (singletons.Count == 0)
            yield break;

        HashSet<LibraDexIndexEntry<TKey, TIdentity>> emitted = new();
        foreach (LibraDexObjectTuple tuple in Iterate(index))
        {
            if (!singletons.Contains(new LibraDexDuplicateValue(tuple.Identity)))
                continue;

            LibraDexIndexEntry<TKey, TIdentity> entry = new(
                ConvertValue<TKey>(tuple.Key, "key"),
                ConvertValue<TIdentity>(tuple.Identity, "identity"));
            if (emitted.Add(entry))
                yield return entry;
        }
    }

    internal static IEnumerable<LibraDexIndexEntry<byte[]?, object>> RawEntriesBySingletonKey(IIndex index)
    {
        HashSet<LibraDexDuplicateValue> singletons =
            new(IterateSingletonRawKeys(index).Select(static key => new LibraDexDuplicateValue(key)),
                LibraDexDuplicateValueComparer.Instance);
        if (singletons.Count == 0)
            yield break;

        HashSet<LibraDexObjectTuple> emitted = new();
        foreach (LibraDexRawTuple tuple in IterateRaw(index, null))
        {
            if (!singletons.Contains(new LibraDexDuplicateValue(tuple.Key)))
                continue;

            LibraDexObjectTuple marker = new(tuple.Key, tuple.Identity);
            if (emitted.Add(marker))
                yield return new LibraDexIndexEntry<byte[]?, object>(tuple.Key, tuple.Identity);
        }
    }

    internal static IEnumerable<LibraDexIndexEntry<byte[]?, object>> RawEntriesBySingletonIdentity(IIndex index)
    {
        if (index.Catalog.TryGetEntriesForSingletonIdentities(index, out LibraDexObjectTuple[] inverseEntries))
        {
            for (int i = 0; i < inverseEntries.Length; i++)
            {
                yield return new LibraDexIndexEntry<byte[]?, object>(
                    EncodeRawKey(index, inverseEntries[i].Key),
                    inverseEntries[i].Identity);
            }

            yield break;
        }

        HashSet<LibraDexDuplicateValue> singletons = FindSingletonValues(index, byKey: false);
        if (singletons.Count == 0)
            yield break;

        HashSet<LibraDexObjectTuple> emitted = new();
        foreach (LibraDexRawTuple tuple in IterateRaw(index, null))
        {
            if (!singletons.Contains(new LibraDexDuplicateValue(tuple.Identity)))
                continue;

            LibraDexObjectTuple marker = new(tuple.Key, tuple.Identity);
            if (emitted.Add(marker))
                yield return new LibraDexIndexEntry<byte[]?, object>(tuple.Key, tuple.Identity);
        }
    }

    private static HashSet<LibraDexDuplicateValue> FindSingletonValues(IIndex index, bool byKey)
    {
        Dictionary<LibraDexDuplicateValue, HashSet<LibraDexDuplicateValue>> groups = BuildGroups(index, byKey);
        HashSet<LibraDexDuplicateValue> singletons = new(LibraDexDuplicateValueComparer.Instance);
        foreach ((LibraDexDuplicateValue value, HashSet<LibraDexDuplicateValue> related) in groups)
        {
            if (related.Count == 1)
                singletons.Add(value);
        }

        return singletons;
    }
}
