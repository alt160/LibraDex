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
    GuidText = 5
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
public readonly record struct LibraDexCompositeKeyPartSpec(
    string Name,
    Type KeyType,
    CatalogIndexKeyFamily KeyFamily,
    StringKeys StringKeys,
    GuidKeys GuidKeys,
    DateKeys DateKeys);

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
        return Create(name, typeof(TKey), CatalogIndexKeyFamily.Scalar, StringKeys.Exact, GuidKeys.Exact, DateKeys.Exact);
    }

    /// <summary>
    /// Creates a string composite-key part descriptor.<br/>
    /// </summary>
    /// <param name="name">The logical part name.</param>
    /// <param name="stringKeys">The string projection profile for this part.</param>
    /// <returns>A composite-key part descriptor.</returns>
    public static LibraDexCompositeKeyPartSpec String(string name, StringKeys stringKeys = StringKeys.Exact)
    {
        return Create(name, typeof(string), CatalogIndexKeyFamily.String, stringKeys, GuidKeys.Exact, DateKeys.Exact);
    }

    /// <summary>
    /// Creates a GUID composite-key part descriptor.<br/>
    /// </summary>
    /// <param name="name">The logical part name.</param>
    /// <param name="guidKeys">The GUID projection profile for this part.</param>
    /// <returns>A composite-key part descriptor.</returns>
    public static LibraDexCompositeKeyPartSpec Guid(string name, GuidKeys guidKeys = GuidKeys.Exact)
    {
        return Create(name, typeof(Guid), CatalogIndexKeyFamily.Guid, StringKeys.Exact, guidKeys, DateKeys.Exact);
    }

    /// <summary>
    /// Creates a date/time composite-key part descriptor.<br/>
    /// </summary>
    /// <typeparam name="TKey">The date/time key-part type.</typeparam>
    /// <param name="name">The logical part name.</param>
    /// <param name="dateKeys">The date projection profile for this part.</param>
    /// <returns>A composite-key part descriptor.</returns>
    public static LibraDexCompositeKeyPartSpec Date<TKey>(string name, DateKeys dateKeys = DateKeys.Exact)
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

        return Create(name, keyType, CatalogIndexKeyFamily.Date, StringKeys.Exact, GuidKeys.Exact, dateKeys);
    }

    private static LibraDexCompositeKeyPartSpec Create(
        string name,
        Type keyType,
        CatalogIndexKeyFamily keyFamily,
        StringKeys stringKeys,
        GuidKeys guidKeys,
        DateKeys dateKeys)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return new LibraDexCompositeKeyPartSpec(name, keyType, keyFamily, stringKeys, guidKeys, dateKeys);
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
        LibraDexProjectionDirectionSet directions,
        LibraDexIndexSortOrder sortOrder,
        IReadOnlyList<LibraDexIndexProjectionSpec> projections,
        IReadOnlyList<LibraDexCompositeKeyPartSpec>? compositeParts = null)
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
        Directions = directions;
        SortOrder = sortOrder;
        Projections = projections;
        CompositeParts = compositeParts ?? Array.Empty<LibraDexCompositeKeyPartSpec>();
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
            StringKeys = StringKeys,
            GuidKeys = GuidKeys,
            DateKeys = DateKeys
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
        if ((stringKeys & StringKeys.Exact) != 0)
        {
            kinds.Add(LibraDexIndexProjectionKind.Exact);
        }

        if ((stringKeys & StringKeys.Folded) != 0)
        {
            kinds.Add(LibraDexIndexProjectionKind.FoldedText);
        }

        if ((stringKeys & StringKeys.SortKey) != 0)
        {
            kinds.Add(LibraDexIndexProjectionKind.SortKey);
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
            directions,
            sortOrder,
            kinds);
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
    /// <param name="keys">The duplicate-key contract for the logical shape.</param>
    /// <param name="sortOrder">The physical sort order requested for maintained projections.</param>
    /// <param name="directions">The byte directions requested for maintained projections.</param>
    /// <returns>A descriptor for the requested logical shape.</returns>
    public LibraDexIndexShapeSpec Date<TKey, TIdentity>(
        DateKeys dateKeys = DateKeys.Exact,
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
            typeof(ValueTuple),
            typeof(TIdentity),
            CatalogIndexKeyFamily.Composite,
            CatalogIndexIdentityFamily.Scalar,
            keys,
            StringKeys.Exact,
            GuidKeys.Exact,
            DateKeys.Exact,
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
            directions,
            sortOrder,
            projections.ToArray(),
            compositeParts);
    }
}
