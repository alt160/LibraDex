namespace LibraDex;

/// <summary>
/// Selects the byte direction represented by a maintained index projection.<br/>
/// Forward projections support prefix and ordinary ordered traversal; reversed projections support suffix-style lookup without query-time reversal scans.<br/>
/// </summary>
public enum LibraDexIndexByteDirection
{
    /// <summary>
    /// Stores encoded key bytes in their normal comparison direction.<br/>
    /// </summary>
    Forward = 0,

    /// <summary>
    /// Stores encoded key bytes in reverse direction so suffix-oriented lookups can become prefix-oriented range work.<br/>
    /// </summary>
    Reversed = 1
}

/// <summary>
/// Selects which byte directions should be represented by a logical index shape.<br/>
/// This is shape intent only in the first slice; physical creation will map each requested direction to maintained projections later.<br/>
/// </summary>
[Flags]
public enum LibraDexProjectionDirectionSet
{
    /// <summary>
    /// Stores only forward projections.<br/>
    /// </summary>
    Forward = 1,

    /// <summary>
    /// Stores only reversed projections.<br/>
    /// </summary>
    Reversed = 2,

    /// <summary>
    /// Stores both forward and reversed projections.<br/>
    /// </summary>
    ForwardAndReversed = Forward | Reversed
}

/// <summary>
/// Selects the physical ordering intent for a maintained projection.<br/>
/// Query-time direction remains separate; this describes how the projection is stored so the planner can know whether reverse traversal is enough or a different projection is preferable.<br/>
/// </summary>
public enum LibraDexIndexSortOrder
{
    /// <summary>
    /// Stores the projection in ascending key order.<br/>
    /// </summary>
    Ascending = 0,

    /// <summary>
    /// Stores the projection in descending key order.<br/>
    /// </summary>
    Descending = 1
}

/// <summary>
/// Identifies one maintained projection inside a logical index shape.<br/>
/// The names are deliberately semantic rather than SQLite-flavored so the future planner can target projection intent directly instead of recognizing SQL function text.<br/>
/// </summary>
public enum LibraDexIndexProjectionKind
{
    /// <summary>
    /// Stores the exact encoded key value.<br/>
    /// </summary>
    Exact = 0,

    /// <summary>
    /// Stores folded text for no-case point lookup.<br/>
    /// </summary>
    FoldedText = 1,

    /// <summary>
    /// Stores sort-key bytes for no-case or culture-aware range ordering without query-time collation.<br/>
    /// </summary>
    SortKey = 2,

    /// <summary>
    /// Stores structured date or time parts as sortable scalar fields.<br/>
    /// </summary>
    StructuredDate = 3,

    /// <summary>
    /// Stores GUID segment projections for segment-oriented lookup.<br/>
    /// </summary>
    GuidSegments = 4,

    /// <summary>
    /// Stores canonical GUID text for text-oriented lookup over GUID values.<br/>
    /// </summary>
    GuidText = 5,

    /// <summary>
    /// Stores fixed-width sortable BigInteger bytes.<br/>
    /// </summary>
    BigIntFixed = 6,

    /// <summary>
    /// Stores variable-width sortable BigInteger bytes.<br/>
    /// </summary>
    BigIntVarLen = 7,

    /// <summary>
    /// Stores fixed-width sortable BigInteger bytes with variable-length raw identity bytes.<br/>
    /// </summary>
    BigIntFixedVarIdentity = 8,

    /// <summary>
    /// Stores exact variable-length raw blob keys with an explicit logical payload-byte cap.<br/>
    /// This discriminator keeps metadata-driven reopen from confusing bounded variable blobs with fixed 8-, 16-, or 32-byte blob keys.<br/>
    /// </summary>
    VariableBlobExact = 9,

    /// <summary>
    /// Stores canonically equivalent Unicode text in case-preserving Form-C representation.<br/>
    /// </summary>
    NormalizedText = 10
}

/// <summary>
/// Describes one maintained projection requested by a logical index shape.<br/>
/// A single logical index can own several projection specs, such as exact string bytes, folded text, sort keys, and reversed folded text.<br/>
/// </summary>
/// <param name="Kind">The semantic projection kind.</param>
/// <param name="Direction">The encoded byte direction for this projection.</param>
/// <param name="SortOrder">The physical sort order requested for this projection.</param>
public readonly record struct LibraDexIndexProjectionSpec(
    LibraDexIndexProjectionKind Kind,
    LibraDexIndexByteDirection Direction,
    LibraDexIndexSortOrder SortOrder);

/// <summary>
/// Describes one part of a logical composite key.<br/>
/// Composite parts preserve per-member key family and projection intent so unique composite indexes can be planned without flattening everything into an opaque caller-owned blob.<br/>
/// </summary>
/// <param name="Name">The logical part name.</param>
/// <param name="KeyType">The CLR type represented by this key part.</param>
/// <param name="KeyFamily">The broad LibraDex key family represented by this part.</param>
/// <param name="StringKeys">String projection flags for string parts.</param>
/// <param name="GuidKeys">GUID projection flags for GUID parts.</param>
/// <param name="DateKeys">Date projection flags for date parts.</param>
/// <param name="DateTimeKeyEncoding">The DateTime-like key encoding contract for date parts.</param>
/// <param name="SortOrder">The physical comparison direction for this composite part.</param>
public readonly record struct LibraDexCompositeKeyPartSpec(
    string Name,
    Type KeyType,
    CatalogIndexKeyFamily KeyFamily,
    StringKeys StringKeys,
    GuidKeys GuidKeys,
    DateKeys DateKeys,
    DateTimeKeyEncoding DateTimeKeyEncoding = DateTimeKeyEncoding.CalendarSdt,
    LibraDexIndexSortOrder SortOrder = LibraDexIndexSortOrder.Ascending);

/// <summary>
/// Describes one runtime value supplied for a composite key.<br/>
/// Names are optional for low-friction positional callers, but generated callers can provide names so validation catches accidental part reordering early.<br/>
/// </summary>
/// <param name="Name">The optional composite part name.</param>
/// <param name="Value">The runtime value for the part.</param>
public readonly record struct LibraDexCompositeKeyValue(string? Name, object? Value);

/// <summary>
/// Represents one runtime composite key value.<br/>
/// This is a programmatic container for generated callers and higher-level adapters; it avoids caller-owned delimiter rules, byte concatenation, or ambiguous object-array conventions.<br/>
/// Physical composite-key storage is not connected yet, but this type lets public APIs and adapters agree on shape validation now.<br/>
/// </summary>
public sealed class LibraDexCompositeKey
{
    private readonly LibraDexCompositeKeyValue[] values;

    private LibraDexCompositeKey(IReadOnlyList<LibraDexCompositeKeyValue> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Count == 0)
        {
            throw new ArgumentException("Composite keys require at least one value.", nameof(values));
        }

        this.values = values.ToArray();
    }

    private LibraDexCompositeKey(LibraDexCompositeKeyValue[] values, bool takeOwnership)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Length == 0)
        {
            throw new ArgumentException("Composite keys require at least one value.", nameof(values));
        }

        this.values = takeOwnership ? values : values.ToArray();
    }

    /// <summary>
    /// Gets the ordered composite key values.<br/>
    /// The order must match the owning composite index shape unless every value is named and a future resolver supports name-based mapping.<br/>
    /// </summary>
    public IReadOnlyList<LibraDexCompositeKeyValue> Values => values;

    /// <summary>
    /// Gets the number of values in the composite key.<br/>
    /// </summary>
    public int Count => values.Length;

    /// <summary>
    /// Gets a composite key value by ordinal.<br/>
    /// </summary>
    /// <param name="ordinal">The zero-based value ordinal.</param>
    /// <returns>The value at the requested ordinal.</returns>
    public object? this[int ordinal] => values[ordinal].Value;

    /// <summary>
    /// Creates a positional composite key.<br/>
    /// Positional keys are concise for handwritten code; generated callers should prefer named values when shape drift is possible.<br/>
    /// </summary>
    /// <param name="values">The ordered values that make up the composite key.</param>
    /// <returns>A composite key descriptor.</returns>
    public static LibraDexCompositeKey Of(params object?[] values)
    {
        ArgumentNullException.ThrowIfNull(values);
        LibraDexCompositeKeyValue[] parts = new LibraDexCompositeKeyValue[values.Length];
        for (int i = 0; i < values.Length; i++)
        {
            parts[i] = new LibraDexCompositeKeyValue(Name: null, values[i]);
        }

        return new LibraDexCompositeKey(parts);
    }

    /// <summary>
    /// Creates a named composite key.<br/>
    /// Named values still preserve order today, but validation can also verify the supplied names against the composite index shape.<br/>
    /// </summary>
    /// <param name="values">The ordered named values that make up the composite key.</param>
    /// <returns>A composite key descriptor.</returns>
    public static LibraDexCompositeKey Named(params LibraDexCompositeKeyValue[] values)
    {
        return new LibraDexCompositeKey(values);
    }

    /// <summary>
    /// Creates a positional composite key from an owned value array without copying it again.<br/>
    /// Internal routed-index traversals already allocate a fresh key-part array for each yielded key, so this factory avoids the second defensive copy while keeping public factories immutable.<br/>
    /// </summary>
    /// <param name="values">The owned positional value array.</param>
    /// <returns>A composite key descriptor.</returns>
    internal static LibraDexCompositeKey TakePositionalValues(object?[] values)
    {
        ArgumentNullException.ThrowIfNull(values);
        LibraDexCompositeKeyValue[] parts = new LibraDexCompositeKeyValue[values.Length];
        for (int i = 0; i < values.Length; i++)
        {
            parts[i] = new LibraDexCompositeKeyValue(Name: null, values[i]);
        }

        return new LibraDexCompositeKey(parts, takeOwnership: true);
    }

    /// <summary>
    /// Creates one named composite key value.<br/>
    /// This helper keeps call sites compact: `LibraDexCompositeKey.Named(LibraDexCompositeKey.Part("tenantId", tenantId), ...)`.<br/>
    /// </summary>
    /// <param name="name">The composite part name.</param>
    /// <param name="value">The runtime value for the part.</param>
    /// <returns>A named composite key value.</returns>
    public static LibraDexCompositeKeyValue Part(string name, object? value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return new LibraDexCompositeKeyValue(name, value);
    }

    /// <summary>
    /// Validates this runtime key against a composite index shape.<br/>
    /// Validation checks part count, optional names, null values, and CLR value types so dynamic callers fail before any physical key encoding occurs.<br/>
    /// </summary>
    /// <param name="shape">The composite index shape to validate against.</param>
    /// <exception cref="ArgumentException">Thrown when the supplied shape is not composite or the key values do not match the shape.</exception>
    public void ValidateAgainst(LibraDexIndexShapeSpec shape)
    {
        ArgumentNullException.ThrowIfNull(shape);
        if (shape.KeyFamily != CatalogIndexKeyFamily.Composite)
        {
            throw new ArgumentException("Composite key validation requires a composite index shape.", nameof(shape));
        }

        ValidateAgainst(shape.CompositeParts);
    }

    /// <summary>
    /// Validates this runtime key against ordered composite part descriptors.<br/>
    /// This overload lets adapters validate against persisted metadata without reconstructing a full shape descriptor first.<br/>
    /// </summary>
    /// <param name="parts">The ordered composite key part descriptors.</param>
    /// <exception cref="ArgumentException">Thrown when the key values do not match the supplied part descriptors.</exception>
    public void ValidateAgainst(IReadOnlyList<LibraDexCompositeKeyPartSpec> parts)
    {
        ArgumentNullException.ThrowIfNull(parts);
        if (parts.Count != values.Length)
        {
            throw new ArgumentException($"Composite key value count {values.Length} does not match composite part count {parts.Count}.", nameof(parts));
        }

        for (int i = 0; i < values.Length; i++)
        {
            LibraDexCompositeKeyValue value = values[i];
            LibraDexCompositeKeyPartSpec part = parts[i];
            if (!string.IsNullOrWhiteSpace(value.Name) && !string.Equals(value.Name, part.Name, StringComparison.Ordinal))
            {
                throw new ArgumentException($"Composite key value '{value.Name}' does not match part '{part.Name}' at ordinal {i}.", nameof(parts));
            }

            _ = LibraDexCompositeKeyValueSemantics.NormalizeForStorage(part, value.Value);
        }
    }
}

/// <summary>
/// Provides short aliases for composite runtime keys.<br/>
/// These aliases keep insert call sites compact while preserving the explicit <see cref="LibraDexCompositeKey"/> container and validation behavior.<br/>
/// </summary>
public static class Key
{
    /// <summary>
    /// Creates a positional composite key.<br/>
    /// This is a short alias for <see cref="LibraDexCompositeKey.Of(object?[])"/>.<br/>
    /// </summary>
    /// <param name="values">The ordered values that make up the composite key.</param>
    /// <returns>A composite key descriptor.</returns>
    public static LibraDexCompositeKey Of(params object?[] values)
    {
        return LibraDexCompositeKey.Of(values);
    }

    /// <summary>
    /// Creates a named composite key.<br/>
    /// This is a short alias for <see cref="LibraDexCompositeKey.Named(LibraDexCompositeKeyValue[])"/>.<br/>
    /// </summary>
    /// <param name="values">The ordered named values that make up the composite key.</param>
    /// <returns>A composite key descriptor.</returns>
    public static LibraDexCompositeKey Named(params LibraDexCompositeKeyValue[] values)
    {
        return LibraDexCompositeKey.Named(values);
    }

    /// <summary>
    /// Creates one named composite key value.<br/>
    /// This is a short alias for <see cref="LibraDexCompositeKey.Part(string, object?)"/>.<br/>
    /// </summary>
    /// <param name="name">The composite part name.</param>
    /// <param name="value">The runtime value for the part.</param>
    /// <returns>A named composite key value.</returns>
    public static LibraDexCompositeKeyValue Part(string name, object? value)
    {
        return LibraDexCompositeKey.Part(name, value);
    }
}

internal static class LibraDexCompositeKeyValueSemantics
{
    /// <summary>
    /// Normalizes one caller-supplied composite key part value into the concrete routed value stored by the composite mini-router.<br/>
    /// String and binary parts use <see cref="NullKey"/> routes, while scalar, GUID, and date-like parts use <see cref="ScalarNull"/> routes so null never collides with a real key value.<br/>
    /// </summary>
    /// <param name="part">The composite part descriptor that defines the value family.</param>
    /// <param name="value">The caller-supplied value or null-state sentinel.</param>
    /// <returns>The concrete value stored in the routed composite tree.</returns>
    internal static object NormalizeForStorage(LibraDexCompositeKeyPartSpec part, object? value)
    {
        if (value is null)
        {
            return UsesNullKeyState(part)
                ? NullKey.Null
                : ScalarNull.Null;
        }

        if (value is NullKey keyState)
        {
            if (!UsesNullKeyState(part))
            {
                throw new ArgumentException($"Composite key part '{part.Name}' uses scalar null state semantics. Use ScalarNull for this part.");
            }

            return keyState switch
            {
                NullKey.Null => NullKey.Null,
                NullKey.Empty => CreateEmptyValue(part),
                NullKey.NullOrEmpty => throw new ArgumentOutOfRangeException(nameof(value), keyState, "NullKey.NullOrEmpty is a predicate state and is not one concrete composite key route."),
                _ => throw new ArgumentOutOfRangeException(nameof(value), keyState, "Unknown null key state.")
            };
        }

        if (value is ScalarNull scalarState)
        {
            if (UsesNullKeyState(part))
            {
                throw new ArgumentException($"Composite key part '{part.Name}' uses string/binary null key semantics. Use NullKey for this part.");
            }

            return scalarState switch
            {
                ScalarNull.Null => ScalarNull.Null,
                ScalarNull.NonNull => throw new ArgumentOutOfRangeException(nameof(value), scalarState, "ScalarNull.NonNull is a predicate state and is not one concrete composite key route."),
                _ => throw new ArgumentOutOfRangeException(nameof(value), scalarState, "Unknown scalar null state.")
            };
        }

        if (!part.KeyType.IsInstanceOfType(value))
        {
            throw new ArgumentException($"Composite key part '{part.Name}' expects {part.KeyType.FullName}, but received {value.GetType().FullName}.");
        }

        return value;
    }

    /// <summary>
    /// Determines whether a composite part uses string/binary null-key semantics.<br/>
    /// Parts that do not use <see cref="NullKey"/> use <see cref="ScalarNull"/> for optional route state.<br/>
    /// </summary>
    /// <param name="part">The composite part descriptor to inspect.</param>
    /// <returns><see langword="true"/> for string and binary parts; otherwise <see langword="false"/>.</returns>
    internal static bool UsesNullKeyState(LibraDexCompositeKeyPartSpec part)
    {
        return part.KeyType == typeof(string) || part.KeyType == typeof(byte[]);
    }

    /// <summary>
    /// Determines whether a normalized routed composite part value is the stored null route for its value family.<br/>
    /// This keeps null-state checks centralized so condition execution, exact lookup, and durable child ordering agree.<br/>
    /// </summary>
    /// <param name="part">The composite part descriptor that defines null-state semantics.</param>
    /// <param name="value">The normalized routed value to test.</param>
    /// <returns><see langword="true"/> when the value is the stored null route.</returns>
    internal static bool IsPartNull(LibraDexCompositeKeyPartSpec part, object value)
    {
        return UsesNullKeyState(part)
            ? value is NullKey.Null
            : value is ScalarNull.Null;
    }

    /// <summary>
    /// Compares two normalized routed composite part values using LibraDex's null-first component ordering.<br/>
    /// The comparison treats explicit null routes as lower than ordinary values, then uses ordinal string comparison, byte-wise binary comparison, or the value's comparable contract.<br/>
    /// </summary>
    /// <param name="part">The composite part descriptor that defines value-family semantics.</param>
    /// <param name="left">The left normalized routed value.</param>
    /// <param name="right">The right normalized routed value.</param>
    /// <returns>A negative, zero, or positive comparison result.</returns>
    internal static int ComparePartValues(LibraDexCompositeKeyPartSpec part, object left, object right)
    {
        int leftRank = GetRouteRank(part, left);
        int rightRank = GetRouteRank(part, right);
        int comparison;
        if (leftRank != rightRank)
        {
            comparison = leftRank.CompareTo(rightRank);
        }
        else if (left is string leftText && right is string rightText)
        {
            comparison = string.CompareOrdinal(leftText, rightText);
        }
        else if (left is byte[] leftBytes && right is byte[] rightBytes)
        {
            comparison = leftBytes.AsSpan().SequenceCompareTo(rightBytes);
        }
        else
        {
            comparison = left is IComparable comparable
                ? comparable.CompareTo(right)
                : string.CompareOrdinal(left.ToString(), right.ToString());
        }

        return part.SortOrder == LibraDexIndexSortOrder.Descending
            ? comparison > 0 ? -1 : comparison < 0 ? 1 : 0
            : comparison;
    }

    /// <summary>
    /// Reads one tagged composite part value from durable storage.<br/>
    /// The tag distinguishes stored null routes from ordinary CLR values so null does not collide with empty strings, empty byte arrays, default GUIDs, or numeric sentinels.<br/>
    /// </summary>
    /// <param name="reader">The binary reader positioned at the tagged part value.</param>
    /// <param name="part">The composite part descriptor that defines the expected value family.</param>
    /// <returns>The normalized routed value.</returns>
    internal static object ReadPartValue(BinaryReader reader, LibraDexCompositeKeyPartSpec part)
    {
        byte marker = reader.ReadByte();
        return marker switch
        {
            0 => LibraDexCompositeSnapshotCodec.ReadValue(reader, part.KeyType) ??
                throw new InvalidDataException("Composite part value cannot decode to null."),
            1 when UsesNullKeyState(part) => NullKey.Null,
            1 => throw new InvalidDataException("Composite NullKey marker was found on a scalar-null composite part."),
            2 when !UsesNullKeyState(part) => ScalarNull.Null,
            2 => throw new InvalidDataException("Composite ScalarNull marker was found on a null-key composite part."),
            _ => throw new InvalidDataException($"Composite part value marker {marker} is not supported.")
        };
    }

    /// <summary>
    /// Writes one normalized composite part value with a small null-state tag before ordinary value bytes.<br/>
    /// Identity values still use the untagged snapshot value codec; this method is only for composite part routes where null-state preservation is required.<br/>
    /// </summary>
    /// <param name="writer">The binary writer receiving the tagged part value.</param>
    /// <param name="part">The composite part descriptor that defines the value family.</param>
    /// <param name="value">The normalized routed value to persist.</param>
    internal static void WritePartValue(BinaryWriter writer, LibraDexCompositeKeyPartSpec part, object value)
    {
        if (UsesNullKeyState(part) && value is NullKey.Null)
        {
            writer.Write((byte)1);
            return;
        }

        if (!UsesNullKeyState(part) && value is ScalarNull.Null)
        {
            writer.Write((byte)2);
            return;
        }

        writer.Write((byte)0);
        LibraDexCompositeSnapshotCodec.WriteValue(writer, part.KeyType, value);
    }

    /// <summary>
    /// Assigns the ordering rank for one normalized routed part value.<br/>
    /// Null routes rank before ordinary values so composite traversal keeps optional parts in deterministic index-natural order.<br/>
    /// </summary>
    /// <param name="part">The composite part descriptor that defines null-state semantics.</param>
    /// <param name="value">The normalized routed value to rank.</param>
    /// <returns>The route ordering rank.</returns>
    private static int GetRouteRank(LibraDexCompositeKeyPartSpec part, object value)
    {
        if (IsPartNull(part, value))
        {
            return 0;
        }

        return 1;
    }

    /// <summary>
    /// Creates the concrete empty route value for a string or binary composite part.<br/>
    /// Empty is a real key value, unlike the distinct null route, so it is stored as `string.Empty` or `Array.Empty&lt;byte&gt;()`.<br/>
    /// </summary>
    /// <param name="part">The null-key composite part descriptor.</param>
    /// <returns>The concrete empty route value.</returns>
    private static object CreateEmptyValue(LibraDexCompositeKeyPartSpec part)
    {
        if (part.KeyType == typeof(string))
        {
            return string.Empty;
        }

        if (part.KeyType == typeof(byte[]))
        {
            return Array.Empty<byte>();
        }

        throw new ArgumentException($"Composite key part '{part.Name}' does not have a NullKey empty route.");
    }
}

/// <summary>
/// Provides low-friction composite-key part descriptors.<br/>
/// These descriptors are metadata only; higher-level systems such as Abraxas remain responsible for converting object members into the corresponding LibraDex key-part values.<br/>
/// </summary>
public static class LibraDexCompositeKeyPart
{
    /// <summary>
    /// Creates a scalar composite-key part descriptor.<br/>
    /// </summary>
    /// <typeparam name="TKey">The scalar key-part type.</typeparam>
    /// <param name="name">The logical part name.</param>
    /// <returns>A composite-key part descriptor.</returns>
    public static LibraDexCompositeKeyPartSpec Scalar<TKey>(string name)
    {
        return Create(name, typeof(TKey), CatalogIndexKeyFamily.Scalar, StringKeys.Exact, GuidKeys.Exact, DateKeys.Exact, DateTimeKeyEncoding.CalendarSdt);
    }

    /// <summary>
    /// Creates a string composite-key part descriptor.<br/>
    /// </summary>
    /// <param name="name">The logical part name.</param>
    /// <param name="stringKeys">The string projection profile for this part.</param>
    /// <returns>A composite-key part descriptor.</returns>
    public static LibraDexCompositeKeyPartSpec String(string name, StringKeys stringKeys = StringKeys.Exact)
    {
        return Create(name, typeof(string), CatalogIndexKeyFamily.String, stringKeys, GuidKeys.Exact, DateKeys.Exact, DateTimeKeyEncoding.CalendarSdt);
    }

    /// <summary>
    /// Creates a binary composite-key part descriptor.<br/>
    /// Binary parts preserve caller-owned byte values and expose byte-domain predicates such as prefix matching without formatting the component as text.<br/>
    /// </summary>
    /// <param name="name">The logical part name.</param>
    /// <returns>A composite-key part descriptor.</returns>
    public static LibraDexCompositeKeyPartSpec Binary(string name)
    {
        return Create(name, typeof(byte[]), CatalogIndexKeyFamily.Blob, StringKeys.Exact, GuidKeys.Exact, DateKeys.Exact, DateTimeKeyEncoding.CalendarSdt);
    }

    /// <summary>
    /// Creates a GUID composite-key part descriptor.<br/>
    /// </summary>
    /// <param name="name">The logical part name.</param>
    /// <param name="guidKeys">The GUID projection profile for this part.</param>
    /// <returns>A composite-key part descriptor.</returns>
    public static LibraDexCompositeKeyPartSpec Guid(string name, GuidKeys guidKeys = GuidKeys.Exact)
    {
        return Create(name, typeof(Guid), CatalogIndexKeyFamily.Guid, StringKeys.Exact, guidKeys, DateKeys.Exact, DateTimeKeyEncoding.CalendarSdt);
    }

    /// <summary>
    /// Creates a date/time composite-key part descriptor.<br/>
    /// </summary>
    /// <typeparam name="TKey">The date/time key-part type.</typeparam>
    /// <param name="name">The logical part name.</param>
    /// <param name="dateKeys">The date projection profile for this part.</param>
    /// <param name="dateTimeKeyEncoding">The DateTime-like key encoding contract for this part.</param>
    /// <returns>A composite-key part descriptor.</returns>
    public static LibraDexCompositeKeyPartSpec Date<TKey>(
        string name,
        DateKeys dateKeys = DateKeys.Exact,
        DateTimeKeyEncoding dateTimeKeyEncoding = DateTimeKeyEncoding.CalendarSdt)
    {
        Type keyType = typeof(TKey);
        if (keyType != typeof(DateTime) &&
            keyType != typeof(DateTimeOffset) &&
            keyType != typeof(DateOnly) &&
            keyType != typeof(TimeOnly) &&
            keyType != typeof(TimeSpan))
        {
            throw new NotSupportedException("Date composite-key parts require DateTime, DateTimeOffset, DateOnly, TimeOnly, or TimeSpan keys.");
        }

        return Create(name, keyType, CatalogIndexKeyFamily.Date, StringKeys.Exact, GuidKeys.Exact, dateKeys, dateTimeKeyEncoding);
    }

    private static LibraDexCompositeKeyPartSpec Create(
        string name,
        Type keyType,
        CatalogIndexKeyFamily keyFamily,
        StringKeys stringKeys,
        GuidKeys guidKeys,
        DateKeys dateKeys,
        DateTimeKeyEncoding dateTimeKeyEncoding)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return new LibraDexCompositeKeyPartSpec(name, keyType, keyFamily, stringKeys, guidKeys, dateKeys, dateTimeKeyEncoding);
    }
}

/// <summary>
/// Provides short aliases for composite-key part descriptors.<br/>
/// The aliases are intended for handwritten schema declarations where `LibraDexCompositeKeyPart.String(...)` and similar long-form names add noise without adding precision.<br/>
/// </summary>
public static class C
{
    /// <summary>
    /// Gets the unique-key contract for compact composite declarations that import or qualify the short alias class.<br/>
    /// This alias avoids making callers spell `IndexKeys.Unique` when the surrounding call already declares an index shape.<br/>
    /// </summary>
    public const IndexKeys Unique = IndexKeys.Unique;

    /// <summary>
    /// Gets the non-unique-key contract for compact composite declarations that import or qualify the short alias class.<br/>
    /// This is the default duplicate-key contract for LibraDex indexes.<br/>
    /// </summary>
    public const IndexKeys NonUnique = IndexKeys.NonUnique;

    /// <summary>
    /// Creates a scalar composite-key part descriptor.<br/>
    /// This is a short alias for <see cref="LibraDexCompositeKeyPart.Scalar{TKey}(string)"/>.<br/>
    /// </summary>
    /// <typeparam name="TKey">The scalar key-part type.</typeparam>
    /// <param name="name">The logical part name.</param>
    /// <returns>A composite-key part descriptor.</returns>
    public static LibraDexCompositeKeyPartSpec Scalar<TKey>(string name)
    {
        return LibraDexCompositeKeyPart.Scalar<TKey>(name);
    }

    /// <summary>
    /// Creates an Int32 scalar composite-key part descriptor.<br/>
    /// This common-width alias keeps compact declarations readable without requiring generic syntax at the call site.<br/>
    /// </summary>
    /// <param name="name">The logical part name.</param>
    /// <returns>A composite-key part descriptor.</returns>
    public static LibraDexCompositeKeyPartSpec Int32(string name)
    {
        return LibraDexCompositeKeyPart.Scalar<int>(name);
    }

    /// <summary>
    /// Creates an Int64 scalar composite-key part descriptor.<br/>
    /// This common-width alias keeps compact declarations readable without requiring generic syntax at the call site.<br/>
    /// </summary>
    /// <param name="name">The logical part name.</param>
    /// <returns>A composite-key part descriptor.</returns>
    public static LibraDexCompositeKeyPartSpec Int64(string name)
    {
        return LibraDexCompositeKeyPart.Scalar<long>(name);
    }

    /// <summary>
    /// Creates an exact Decimal scalar composite-key part descriptor.<br/>
    /// Decimal parts use the same canonical ordered scalar-16 representation as standalone Decimal indexes.<br/>
    /// </summary>
    /// <param name="name">The logical part name.<br/></param>
    /// <returns>A Decimal composite-key part descriptor.<br/></returns>
    public static LibraDexCompositeKeyPartSpec Decimal(string name)
    {
        return LibraDexCompositeKeyPart.Scalar<decimal>(name);
    }

    /// <summary>
    /// Creates a native Single scalar composite-key part descriptor.<br/>
    /// Single parts use the canonical ordered scalar-8 representation, including one NaN key and one zero key.<br/>
    /// </summary>
    /// <param name="name">The logical part name.<br/></param>
    /// <returns>A Single composite-key part descriptor.<br/></returns>
    public static LibraDexCompositeKeyPartSpec Single(string name)
    {
        return LibraDexCompositeKeyPart.Scalar<float>(name);
    }

    /// <summary>
    /// Creates a native Double scalar composite-key part descriptor.<br/>
    /// Double parts use the canonical ordered scalar-8 representation, including one NaN key and one zero key.<br/>
    /// </summary>
    /// <param name="name">The logical part name.<br/></param>
    /// <returns>A Double composite-key part descriptor.<br/></returns>
    public static LibraDexCompositeKeyPartSpec Double(string name)
    {
        return LibraDexCompositeKeyPart.Scalar<double>(name);
    }

    /// <summary>
    /// Creates a GUID composite-key part descriptor.<br/>
    /// This is a short alias for <see cref="LibraDexCompositeKeyPart.Guid(string, GuidKeys)"/>.<br/>
    /// </summary>
    /// <param name="name">The logical part name.</param>
    /// <param name="guidKeys">The GUID projection profile for this part.</param>
    /// <returns>A composite-key part descriptor.</returns>
    public static LibraDexCompositeKeyPartSpec Guid(string name, GuidKeys guidKeys = GuidKeys.Exact)
    {
        return LibraDexCompositeKeyPart.Guid(name, guidKeys);
    }

    /// <summary>
    /// Creates a string composite-key part descriptor.<br/>
    /// This is a short alias for <see cref="LibraDexCompositeKeyPart.String(string, StringKeys)"/> with a developer-facing `Text` name to reduce visual collision with <see cref="string"/>.<br/>
    /// </summary>
    /// <param name="name">The logical part name.</param>
    /// <param name="stringKeys">The string projection profile for this part.</param>
    /// <returns>A composite-key part descriptor.</returns>
    public static LibraDexCompositeKeyPartSpec Text(string name, StringKeys stringKeys = StringKeys.Exact)
    {
        return LibraDexCompositeKeyPart.String(name, stringKeys);
    }

    /// <summary>
    /// Creates a binary composite-key part descriptor.<br/>
    /// This is a short alias for <see cref="LibraDexCompositeKeyPart.Binary(string)"/> for compact routed-key declarations.<br/>
    /// </summary>
    /// <param name="name">The logical part name.</param>
    /// <returns>A composite-key part descriptor.</returns>
    public static LibraDexCompositeKeyPartSpec Binary(string name)
    {
        return LibraDexCompositeKeyPart.Binary(name);
    }

    /// <summary>
    /// Creates a date/time composite-key part descriptor.<br/>
    /// This is a short alias for <see cref="LibraDexCompositeKeyPart.Date{TKey}(string, DateKeys, DateTimeKeyEncoding)"/>.<br/>
    /// </summary>
    /// <typeparam name="TKey">The date/time key-part type.</typeparam>
    /// <param name="name">The logical part name.</param>
    /// <param name="dateKeys">The date projection profile for this part.</param>
    /// <param name="dateTimeKeyEncoding">The DateTime-like key encoding contract for this part.</param>
    /// <returns>A composite-key part descriptor.</returns>
    public static LibraDexCompositeKeyPartSpec Date<TKey>(
        string name,
        DateKeys dateKeys = DateKeys.Exact,
        DateTimeKeyEncoding dateTimeKeyEncoding = DateTimeKeyEncoding.CalendarSdt)
    {
        return LibraDexCompositeKeyPart.Date<TKey>(name, dateKeys, dateTimeKeyEncoding);
    }
}

/// <summary>
/// Describes a logical LibraDex index shape before it is physically created.<br/>
/// This is the typed counterpart to Abraxas selector/index creation metadata: condition builders can target these projections directly instead of rendering SQL and hoping SQLite activates the expected index.<br/>
/// </summary>
public sealed class LibraDexIndexShapeSpec
{
    internal LibraDexIndexShapeSpec(
        string group,
        string name,
        Type keyType,
        Type identityType,
        CatalogIndexKeyFamily keyFamily,
        CatalogIndexIdentityFamily identityFamily,
        IndexKeys keyContract,
        StringKeys stringKeys,
        GuidKeys guidKeys,
        DateKeys dateKeys,
        DateTimeKeyEncoding dateTimeKeyEncoding,
        LibraDexProjectionDirectionSet directions,
        LibraDexIndexSortOrder sortOrder,
        IReadOnlyList<LibraDexIndexProjectionSpec> projections,
        IReadOnlyList<LibraDexCompositeKeyPartSpec>? compositeParts = null,
        IdentityKeyMultiplicity identityKeyMultiplicity = IdentityKeyMultiplicity.MultipleKeysPerIdentity)
    {
        Group = group;
        Name = name;
        KeyType = keyType;
        IdentityType = identityType;
        KeyFamily = keyFamily;
        IdentityFamily = identityFamily;
        KeyContract = keyContract;
        StringKeys = stringKeys;
        GuidKeys = guidKeys;
        DateKeys = dateKeys;
        DateTimeKeyEncoding = dateTimeKeyEncoding;
        Directions = directions;
        SortOrder = sortOrder;
        Projections = projections;
        CompositeParts = compositeParts ?? Array.Empty<LibraDexCompositeKeyPartSpec>();
        IdentityKeyMultiplicity = identityKeyMultiplicity;
    }

    /// <summary>
    /// Gets the identity group that owns this logical shape.<br/>
    /// </summary>
    public string Group { get; }

    /// <summary>
    /// Gets the index name inside the identity group.<br/>
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// Gets the CLR key type represented by this shape.<br/>
    /// </summary>
    public Type KeyType { get; }

    /// <summary>
    /// Gets the CLR identity type represented by this shape.<br/>
    /// </summary>
    public Type IdentityType { get; }

    /// <summary>
    /// Gets the broad key family used for public discovery and planner routing.<br/>
    /// </summary>
    public CatalogIndexKeyFamily KeyFamily { get; }

    /// <summary>
    /// Gets the broad identity family used for public discovery and planner routing.<br/>
    /// </summary>
    public CatalogIndexIdentityFamily IdentityFamily { get; }

    /// <summary>
    /// Gets the index-wide duplicate-key contract.<br/>
    /// </summary>
    public IndexKeys KeyContract { get; }

    /// <summary>
    /// Gets whether this logical index shape allows one identity to be stored under multiple keys.<br/>
    /// Planners may only use tuple/range cardinality as identity-set cardinality when this contract is enforced by the opened index.<br/>
    /// </summary>
    public IdentityKeyMultiplicity IdentityKeyMultiplicity { get; }

    /// <summary>
    /// Gets the string projection flags requested by this shape.<br/>
    /// Non-string shapes return <see cref="StringKeys.Exact"/> as the neutral default.<br/>
    /// </summary>
    public StringKeys StringKeys { get; }

    /// <summary>
    /// Gets the GUID projection flags requested by this shape.<br/>
    /// Non-GUID shapes return <see cref="GuidKeys.Exact"/> as the neutral default.<br/>
    /// </summary>
    public GuidKeys GuidKeys { get; }

    /// <summary>
    /// Gets the date projection flags requested by this shape.<br/>
    /// Non-date shapes return <see cref="DateKeys.Exact"/> as the neutral default.<br/>
    /// </summary>
    public DateKeys DateKeys { get; }

    /// <summary>
    /// Gets the DateTime-like key encoding contract requested by this shape.<br/>
    /// Non-date shapes return <see cref="DateTimeKeyEncoding.CalendarSdt"/> as the neutral default.<br/>
    /// </summary>
    public DateTimeKeyEncoding DateTimeKeyEncoding { get; }

    /// <summary>
    /// Gets the requested projection byte directions.<br/>
    /// </summary>
    public LibraDexProjectionDirectionSet Directions { get; }

    /// <summary>
    /// Gets the physical sort order requested for every projection in this shape.<br/>
    /// Later physical creation can widen this if per-projection ordering becomes necessary.<br/>
    /// </summary>
    public LibraDexIndexSortOrder SortOrder { get; }

    /// <summary>
    /// Gets the maintained projections requested by this logical shape.<br/>
    /// The collection is disconnected and immutable from the caller's perspective.<br/>
    /// </summary>
    public IReadOnlyList<LibraDexIndexProjectionSpec> Projections { get; }

    /// <summary>
    /// Gets the ordered composite-key parts for composite logical shapes.<br/>
    /// Non-composite shapes return an empty list.<br/>
    /// </summary>
    public IReadOnlyList<LibraDexCompositeKeyPartSpec> CompositeParts { get; }

    /// <summary>
    /// Gets whether this logical shape contains a projection with the supplied kind and byte direction.<br/>
    /// This is the first planner-facing affordance: a condition builder can ask for `FoldedText + Forward` for no-case point lookup or `SortKey + Forward` for no-case ranges.<br/>
    /// </summary>
    /// <param name="kind">The projection kind to locate.</param>
    /// <param name="direction">The byte direction to locate.</param>
    /// <returns><see langword="true"/> when a matching projection exists; otherwise <see langword="false"/>.</returns>
    public bool HasProjection(LibraDexIndexProjectionKind kind, LibraDexIndexByteDirection direction = LibraDexIndexByteDirection.Forward)
    {
        for (int i = 0; i < Projections.Count; i++)
        {
            LibraDexIndexProjectionSpec projection = Projections[i];
            if (projection.Kind == kind && projection.Direction == direction)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Converts this logical shape into the current public per-index options object.<br/>
    /// This keeps descriptor-driven creation aligned with the existing catalog metadata fields until durable projection metadata is widened.<br/>
    /// </summary>
    /// <returns>An <see cref="IndexOptions"/> instance carrying the shape's duplicate-key and projection contracts.</returns>
    public IndexOptions ToIndexOptions()
    {
        return new IndexOptions
        {
            Keys = KeyContract,
            IdentityKeyMultiplicity = IdentityKeyMultiplicity,
            StringKeys = StringKeys,
            GuidKeys = GuidKeys,
            DateKeys = DateKeys,
            DateTimeKeyEncoding = DateTimeKeyEncoding
        };
    }
}

/// <summary>
/// Builds descriptor-only logical index shapes for one named index inside an identity group.<br/>
/// These descriptors are intentionally separate from physical creation in the first slice so the public API can settle before catalog metadata and storage formats are widened.<br/>
/// </summary>
public sealed class CatalogNamedIndexShapeBuilder
{
    private readonly string group;
    private readonly string name;

    internal CatalogNamedIndexShapeBuilder(string group, string name)
    {
        this.group = group;
        this.name = name;
    }

    /// <summary>
    /// Describes a scalar-key, scalar-identity index shape.<br/>
    /// Scalar shapes currently request an exact projection, optionally in both forward and reversed byte directions.<br/>
    /// </summary>
    /// <typeparam name="TKey">The scalar key type.</typeparam>
    /// <typeparam name="TIdentity">The scalar identity type.</typeparam>
    /// <param name="keys">The duplicate-key contract for the logical shape.</param>
    /// <param name="sortOrder">The physical sort order requested for maintained projections.</param>
    /// <param name="directions">The byte directions requested for maintained projections.</param>
    /// <returns>A descriptor for the requested logical shape.</returns>
    public LibraDexIndexShapeSpec Scalar<TKey, TIdentity>(
        IndexKeys keys = IndexKeys.NonUnique,
        LibraDexIndexSortOrder sortOrder = LibraDexIndexSortOrder.Ascending,
        LibraDexProjectionDirectionSet directions = LibraDexProjectionDirectionSet.Forward)
    {
        return Create(
            typeof(TKey),
            typeof(TIdentity),
            CatalogIndexKeyFamily.Scalar,
            CatalogIndexIdentityFamily.Scalar,
            keys,
            StringKeys.Exact,
            GuidKeys.Exact,
            DateKeys.Exact,
            DateTimeKeyEncoding.CalendarSdt,
            directions,
            sortOrder,
            new[] { LibraDexIndexProjectionKind.Exact });
    }

    /// <summary>
    /// Describes a string-key, scalar-identity index shape.<br/>
    /// String keys can request exact, folded-text, and sort-key projections, with optional reversed siblings for suffix-oriented lookups.<br/>
    /// </summary>
    /// <typeparam name="TIdentity">The scalar identity type.</typeparam>
    /// <param name="stringKeys">The string projection profile.</param>
    /// <param name="keys">The duplicate-key contract for the logical shape.</param>
    /// <param name="sortOrder">The physical sort order requested for maintained projections.</param>
    /// <param name="directions">The byte directions requested for maintained projections.</param>
    /// <returns>A descriptor for the requested logical shape.</returns>
    public LibraDexIndexShapeSpec String<TIdentity>(
        StringKeys stringKeys = StringKeys.Exact,
        IndexKeys keys = IndexKeys.NonUnique,
        LibraDexIndexSortOrder sortOrder = LibraDexIndexSortOrder.Ascending,
        LibraDexProjectionDirectionSet directions = LibraDexProjectionDirectionSet.Forward)
    {
        List<LibraDexIndexProjectionKind> kinds = new();
        ValidateStringKeys(stringKeys);
        kinds.Add(LibraDexIndexProjectionKind.Exact);

        if ((stringKeys & StringKeys.Folded) != 0)
        {
            kinds.Add(LibraDexIndexProjectionKind.FoldedText);
        }

        if ((stringKeys & StringKeys.SortKey) != 0)
        {
            kinds.Add(LibraDexIndexProjectionKind.SortKey);
        }

        if ((stringKeys & StringKeys.Normalized) != 0)
        {
            kinds.Add(LibraDexIndexProjectionKind.NormalizedText);
        }

        return Create(
            typeof(string),
            typeof(TIdentity),
            CatalogIndexKeyFamily.String,
            CatalogIndexIdentityFamily.Scalar,
            keys,
            stringKeys,
            GuidKeys.Exact,
            DateKeys.Exact,
            DateTimeKeyEncoding.CalendarSdt,
            directions,
            sortOrder,
            kinds);
    }

    private static void ValidateStringKeys(StringKeys stringKeys)
    {
        const StringKeys supported = StringKeys.Exact | StringKeys.Folded | StringKeys.SortKey | StringKeys.Normalized;
        if ((stringKeys & StringKeys.Exact) == 0 || (stringKeys & ~supported) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(stringKeys), stringKeys, "The string-key profile is not supported by the string shape descriptor.");
        }
    }

    /// <summary>
    /// Describes a GUID-key, scalar-identity index shape.<br/>
    /// GUID keys can request exact bytes, segment projections, and canonical text projections for developer-facing GUID lookup semantics.<br/>
    /// </summary>
    /// <typeparam name="TIdentity">The scalar identity type.</typeparam>
    /// <param name="guidKeys">The GUID projection profile.</param>
    /// <param name="keys">The duplicate-key contract for the logical shape.</param>
    /// <param name="sortOrder">The physical sort order requested for maintained projections.</param>
    /// <param name="directions">The byte directions requested for maintained projections.</param>
    /// <returns>A descriptor for the requested logical shape.</returns>
    public LibraDexIndexShapeSpec Guid<TIdentity>(
        GuidKeys guidKeys = GuidKeys.Exact,
        IndexKeys keys = IndexKeys.NonUnique,
        LibraDexIndexSortOrder sortOrder = LibraDexIndexSortOrder.Ascending,
        LibraDexProjectionDirectionSet directions = LibraDexProjectionDirectionSet.Forward)
    {
        List<LibraDexIndexProjectionKind> kinds = new();
        if ((guidKeys & GuidKeys.Exact) != 0)
        {
            kinds.Add(LibraDexIndexProjectionKind.Exact);
        }

        if ((guidKeys & GuidKeys.Segments) != 0)
        {
            kinds.Add(LibraDexIndexProjectionKind.GuidSegments);
        }

        if ((guidKeys & GuidKeys.Text) != 0)
        {
            kinds.Add(LibraDexIndexProjectionKind.GuidText);
        }

        return Create(
            typeof(Guid),
            typeof(TIdentity),
            CatalogIndexKeyFamily.Guid,
            CatalogIndexIdentityFamily.Scalar,
            keys,
            StringKeys.Exact,
            guidKeys,
            DateKeys.Exact,
            DateTimeKeyEncoding.CalendarSdt,
            directions,
            sortOrder,
            kinds);
    }

    /// <summary>
    /// Describes a date/time-key, scalar-identity index shape.<br/>
    /// Date keys can request exact chronological projection and structured date-part projection for year/month/day/weekday-style criteria.<br/>
    /// </summary>
    /// <typeparam name="TKey">The date/time key type.</typeparam>
    /// <typeparam name="TIdentity">The scalar identity type.</typeparam>
    /// <param name="dateKeys">The date projection profile.</param>
    /// <param name="dateTimeKeyEncoding">The DateTime-like key encoding contract for this shape.</param>
    /// <param name="keys">The duplicate-key contract for the logical shape.</param>
    /// <param name="sortOrder">The physical sort order requested for maintained projections.</param>
    /// <param name="directions">The byte directions requested for maintained projections.</param>
    /// <returns>A descriptor for the requested logical shape.</returns>
    public LibraDexIndexShapeSpec Date<TKey, TIdentity>(
        DateKeys dateKeys = DateKeys.Exact,
        DateTimeKeyEncoding dateTimeKeyEncoding = DateTimeKeyEncoding.CalendarSdt,
        IndexKeys keys = IndexKeys.NonUnique,
        LibraDexIndexSortOrder sortOrder = LibraDexIndexSortOrder.Ascending,
        LibraDexProjectionDirectionSet directions = LibraDexProjectionDirectionSet.Forward)
    {
        Type keyType = typeof(TKey);
        if (keyType != typeof(DateTime) &&
            keyType != typeof(DateTimeOffset) &&
            keyType != typeof(DateOnly) &&
            keyType != typeof(TimeOnly) &&
            keyType != typeof(TimeSpan))
        {
            throw new NotSupportedException("Date index shapes require DateTime, DateTimeOffset, DateOnly, TimeOnly, or TimeSpan keys.");
        }

        List<LibraDexIndexProjectionKind> kinds = new();
        if ((dateKeys & DateKeys.Exact) != 0)
        {
            kinds.Add(LibraDexIndexProjectionKind.Exact);
        }

        if ((dateKeys & DateKeys.Structured) != 0)
        {
            kinds.Add(LibraDexIndexProjectionKind.StructuredDate);
        }

        return Create(
            keyType,
            typeof(TIdentity),
            CatalogIndexKeyFamily.Date,
            CatalogIndexIdentityFamily.Scalar,
            keys,
            StringKeys.Exact,
            GuidKeys.Exact,
            dateKeys,
            dateTimeKeyEncoding,
            directions,
            sortOrder,
            kinds);
    }

    /// <summary>
    /// Describes a composite-key, scalar-identity index shape.<br/>
    /// Composite shapes are descriptor-only in this slice; they capture ordered key-part metadata and uniqueness intent before physical composite storage is implemented.<br/>
    /// </summary>
    /// <typeparam name="TIdentity">The scalar identity type.</typeparam>
    /// <param name="parts">The ordered composite-key parts.</param>
    /// <param name="keys">The duplicate-key contract for the full composite key.</param>
    /// <param name="sortOrder">The physical sort order requested for maintained projections.</param>
    /// <param name="directions">The byte directions requested for maintained projections.</param>
    /// <returns>A descriptor for the requested logical composite shape.</returns>
    public LibraDexIndexShapeSpec Composite<TIdentity>(
        IReadOnlyList<LibraDexCompositeKeyPartSpec> parts,
        IndexKeys keys = IndexKeys.NonUnique,
        LibraDexIndexSortOrder sortOrder = LibraDexIndexSortOrder.Ascending,
        LibraDexProjectionDirectionSet directions = LibraDexProjectionDirectionSet.Forward)
    {
        ArgumentNullException.ThrowIfNull(parts);
        if (parts.Count == 0)
        {
            throw new ArgumentException("Composite index shapes require at least one key part.", nameof(parts));
        }

        HashSet<string> names = new(StringComparer.Ordinal);
        for (int i = 0; i < parts.Count; i++)
        {
            if (string.IsNullOrWhiteSpace(parts[i].Name))
            {
                throw new ArgumentException("Composite index part names cannot be empty.", nameof(parts));
            }

            if (!names.Add(parts[i].Name))
            {
                throw new ArgumentException("Composite index part names must be unique within the shape.", nameof(parts));
            }
        }

        return Create(
            typeof(LibraDexCompositeKey),
            typeof(TIdentity),
            CatalogIndexKeyFamily.Composite,
            CatalogIndexIdentityFamily.Scalar,
            keys,
            StringKeys.Exact,
            GuidKeys.Exact,
            DateKeys.Exact,
            DateTimeKeyEncoding.CalendarSdt,
            directions,
            sortOrder,
            new[] { LibraDexIndexProjectionKind.Exact },
            parts.ToArray());
    }

    /// <summary>
    /// Describes a composite-key, scalar-identity index shape from a parameter list of parts.<br/>
    /// </summary>
    /// <typeparam name="TIdentity">The scalar identity type.</typeparam>
    /// <param name="keys">The duplicate-key contract for the full composite key.</param>
    /// <param name="parts">The ordered composite-key parts.</param>
    /// <returns>A descriptor for the requested logical composite shape.</returns>
    public LibraDexIndexShapeSpec Composite<TIdentity>(
        IndexKeys keys,
        params LibraDexCompositeKeyPartSpec[] parts)
    {
        return Composite<TIdentity>(parts, keys);
    }

    private LibraDexIndexShapeSpec Create(
        Type keyType,
        Type identityType,
        CatalogIndexKeyFamily keyFamily,
        CatalogIndexIdentityFamily identityFamily,
        IndexKeys keys,
        StringKeys stringKeys,
        GuidKeys guidKeys,
        DateKeys dateKeys,
        DateTimeKeyEncoding dateTimeKeyEncoding,
        LibraDexProjectionDirectionSet directions,
        LibraDexIndexSortOrder sortOrder,
        IReadOnlyList<LibraDexIndexProjectionKind> projectionKinds,
        IReadOnlyList<LibraDexCompositeKeyPartSpec>? compositeParts = null)
    {
        if (directions == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(directions), directions, "At least one projection direction must be selected.");
        }

        List<LibraDexIndexProjectionSpec> projections = new();
        bool includeForward = (directions & LibraDexProjectionDirectionSet.Forward) != 0;
        bool includeReversed = (directions & LibraDexProjectionDirectionSet.Reversed) != 0;
        for (int i = 0; i < projectionKinds.Count; i++)
        {
            LibraDexIndexProjectionKind kind = projectionKinds[i];
            if (includeForward)
            {
                projections.Add(new LibraDexIndexProjectionSpec(kind, LibraDexIndexByteDirection.Forward, sortOrder));
            }

            if (includeReversed)
            {
                projections.Add(new LibraDexIndexProjectionSpec(kind, LibraDexIndexByteDirection.Reversed, sortOrder));
            }
        }

        return new LibraDexIndexShapeSpec(
            group,
            name,
            keyType,
            identityType,
            keyFamily,
            identityFamily,
            keys,
            stringKeys,
            guidKeys,
            dateKeys,
            dateTimeKeyEncoding,
            directions,
            sortOrder,
            projections.ToArray(),
            compositeParts);
    }
}
