namespace LibraDex;

/// <summary>
/// Holds one caller-owned value that a reusable LibraDex condition reads when execution begins.<br/>
/// The same parameter instance may be used by multiple condition leaves; LibraDex snapshots it once per execution so every use observes one coherent value.<br/>
/// The caller owns synchronization when <see cref="Value"/> can change concurrently with condition execution.<br/>
/// </summary>
/// <typeparam name="T">The selector, criterion, or collection value type.</typeparam>
public sealed class LibraDexParameter<T> : ILibraDexParameter
{
    private T value;
    private readonly Func<T, object?>? snapshotFactory;

    /// <summary>
    /// Initializes a reusable LibraDex parameter with its current value.<br/>
    /// </summary>
    /// <param name="value">The value visible to the next condition execution.</param>
    /// <param name="name">Optional diagnostic name shown by condition inspection surfaces.</param>
    public LibraDexParameter(T value, string? name = null)
        : this(value, name, snapshotFactory: null)
    {
    }

    internal LibraDexParameter(T value, string? name, Func<T, object?>? snapshotFactory)
    {
        this.value = value;
        Name = name;
        this.snapshotFactory = snapshotFactory;
    }

    /// <summary>
    /// Gets the optional diagnostic name for this parameter.<br/>
    /// The name does not participate in value lookup or replacement.<br/>
    /// </summary>
    public string? Name { get; }

    /// <summary>
    /// Gets or sets the value visible to the next condition execution.<br/>
    /// LibraDex reads this property once per execution even when the parameter appears in multiple leaves.<br/>
    /// The caller must coordinate writes that can overlap execution; LibraDex intentionally does not add locking to this low-allocation adapter.<br/>
    /// </summary>
    public T Value
    {
        get => value;
        set => this.value = value;
    }

    object? ILibraDexParameter.ReadValue() => value;

    object? ILibraDexParameter.ReadSnapshotValue()
        => snapshotFactory is null ? value : snapshotFactory(value);
}

/// <summary>
/// Creates reusable LibraDex parameters without repeating the generic type at the call site.<br/>
/// </summary>
public static class LibraDexParameter
{
    /// <summary>
    /// Creates a caller-owned execution-time parameter inferred from the supplied value.<br/>
    /// </summary>
    /// <typeparam name="T">The selector, criterion, or collection value type.</typeparam>
    /// <param name="value">The value visible to the next condition execution.</param>
    /// <param name="name">Optional diagnostic name shown by condition inspection surfaces.</param>
    /// <returns>A reusable typed parameter.</returns>
    public static LibraDexParameter<T> Create<T>(T value, string? name = null)
        => new(value, name);

    /// <summary>
    /// Creates a reusable enumerable parameter with its element type preserved for set and key-existence IntelliSense.<br/>
    /// The enumerable itself is not consumed until condition execution.<br/>
    /// </summary>
    /// <typeparam name="T">The enumerable element type.</typeparam>
    /// <param name="values">The values visible to the next condition execution.</param>
    /// <param name="name">Optional diagnostic name shown by condition inspection surfaces.</param>
    /// <returns>A reusable enumerable parameter.</returns>
    public static LibraDexParameter<IEnumerable<T>> Set<T>(IEnumerable<T> values, string? name = null)
    {
        ArgumentNullException.ThrowIfNull(values);
        return new LibraDexParameter<IEnumerable<T>>(values, name, SnapshotSet<T>);
    }

    /// <summary>
    /// Copies one membership source into a typed array when condition execution begins.<br/>
    /// Arrays and collections require one exact allocation; streaming enumerables use a temporary list because their final count is not known in advance.<br/>
    /// </summary>
    /// <typeparam name="T">The membership element type.</typeparam>
    /// <param name="values">The caller-owned membership source visible at execution start.</param>
    /// <returns>A stable typed array that cannot observe later collection mutations.</returns>
    private static object SnapshotSet<T>(IEnumerable<T> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values is ICollection<T> collection)
        {
            if (collection.Count == 0)
                return Array.Empty<T>();

            var snapshot = new T[collection.Count];
            collection.CopyTo(snapshot, 0);
            return snapshot;
        }

        var captured = new List<T>();
        foreach (T value in values)
            captured.Add(value);
        return captured.Count == 0 ? Array.Empty<T>() : captured.ToArray();
    }
}

internal interface ILibraDexParameter
{
    string? Name { get; }

    object? ReadValue();

    object? ReadSnapshotValue();
}

internal interface ILibraDexParameterSnapshotValue
{
    bool HasParameters { get; }

    void CaptureParameters(LibraDexParameterSnapshot snapshot);

    object Snapshot(LibraDexParameterSnapshot snapshot);
}

/// <summary>
/// Caches parameter values by parameter identity for one complete condition materialization.<br/>
/// Ordinary deferred factories retain their existing per-operand semantics; only explicit parameters receive shared snapshot semantics.<br/>
/// </summary>
internal sealed class LibraDexParameterSnapshot
{
    internal static readonly LibraDexParameterSnapshot Empty = new();

    private Dictionary<ILibraDexParameter, object?>? values;

    internal object? Read(ILibraDexParameter parameter)
    {
        values ??= new Dictionary<ILibraDexParameter, object?>(ReferenceEqualityComparer.Instance);
        if (values.TryGetValue(parameter, out object? value))
            return value;

        value = parameter.ReadSnapshotValue();
        values.Add(parameter, value);
        return value;
    }
}
