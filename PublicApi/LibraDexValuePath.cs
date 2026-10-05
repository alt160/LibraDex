using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Reflection;

namespace LibraDex;

/// <summary>Identifies why a CLR value path could not be prepared.<br/></summary>
public enum LibraDexValuePathFailureKind
{
    /// <summary>The value path was prepared successfully.<br/></summary>
    None = 0,
    /// <summary>The requested path contains no member segments.<br/></summary>
    EmptyPath = 1,
    /// <summary>A requested public instance member could not be found.<br/></summary>
    MissingMember = 2,
    /// <summary>A requested method is excluded by the value-path contract.<br/></summary>
    BlockedMethod = 3,
    /// <summary>The resolved member result does not match the expected result type.<br/></summary>
    ResultTypeMismatch = 4
}

/// <summary>
/// Describes one nonexecuting CLR value-path check.<br/>
/// Checking inspects public instance metadata only; it does not invoke a property getter, field, or method.<br/>
/// </summary>
public sealed class LibraDexValuePathCheck
{
    internal LibraDexValuePathCheck(
        Type sourceType,
        Type expectedResultType,
        string requestedPath,
        string canonicalPath,
        Type? resultType,
        LibraDexValuePathFailureKind failureKind,
        string? failedSegment,
        bool containsMethod,
        MemberInfo[] members)
    {
        SourceType = sourceType;
        ExpectedResultType = expectedResultType;
        RequestedPath = requestedPath;
        CanonicalPath = canonicalPath;
        ResultType = resultType;
        FailureKind = failureKind;
        FailedSegment = failedSegment;
        ContainsMethod = containsMethod;
        Members = members;
    }

    /// <summary>Gets the declared CLR value type from which resolution begins.<br/></summary>
    public Type SourceType { get; }

    /// <summary>Gets the result type required by the caller.<br/></summary>
    public Type ExpectedResultType { get; }

    /// <summary>Gets the caller-supplied path.<br/></summary>
    public string RequestedPath { get; }

    /// <summary>Gets the canonical leading-dot path; admitted method segments include <c>()</c>.<br/></summary>
    public string CanonicalPath { get; }

    /// <summary>Gets the CLR type produced by the final member, or <see langword="null"/> when resolution failed.<br/></summary>
    public Type? ResultType { get; }

    /// <summary>Gets the structural failure classification.<br/></summary>
    public LibraDexValuePathFailureKind FailureKind { get; }

    /// <summary>Gets the first unresolved or blocked segment.<br/></summary>
    public string? FailedSegment { get; }

    /// <summary>Gets whether at least one path segment invokes a parameterless method.<br/></summary>
    public bool ContainsMethod { get; }

    /// <summary>Gets whether the complete path resolves to the requested result type.<br/></summary>
    public bool IsValid => FailureKind == LibraDexValuePathFailureKind.None;

    internal MemberInfo[] Members { get; }
}

/// <summary>
/// Checks and compiles reusable CLR property, field, and admitted parameterless-method paths.<br/>
/// Reflection and expression compilation occur once per source type, result type, and canonical path; repeated reads invoke the cached typed delegate directly.<br/>
/// </summary>
public static class LibraDexValuePath
{
    private static readonly ConcurrentDictionary<(RuntimeTypeHandle Source, RuntimeTypeHandle Result, string Path), LibraDexValuePathCheck> Checks = new();

    /// <summary>
    /// Checks and canonicalizes a CLR value path without invoking developer code.<br/>
    /// Properties and fields are preferred over methods; methods must be public, instance, parameterless, non-void, and return an admitted value-like type.<br/>
    /// </summary>
    /// <typeparam name="TSource">Declared source value type.<br/></typeparam>
    /// <typeparam name="TValue">Required final result type.<br/></typeparam>
    /// <param name="path">Dotted path whose leading dot and parameterless-method parentheses are optional.<br/></param>
    /// <returns>A cached immutable structural check.<br/></returns>
    public static LibraDexValuePathCheck Check<TSource, TValue>(string path)
    {
        path ??= string.Empty;
        return Checks.GetOrAdd(
            (typeof(TSource).TypeHandle, typeof(TValue).TypeHandle, path),
            static key => BuildCheck(
                Type.GetTypeFromHandle(key.Source)!,
                Type.GetTypeFromHandle(key.Result)!,
                key.Path));
    }

    /// <summary>
    /// Creates or reuses one compiled CLR value path.<br/>
    /// Invalid paths fail before any source value is read; the returned plan is safe to reuse across threads when the invoked developer members are themselves thread-safe.<br/>
    /// </summary>
    /// <typeparam name="TSource">Declared source value type.<br/></typeparam>
    /// <typeparam name="TValue">Required final result type.<br/></typeparam>
    /// <param name="path">Dotted path whose leading dot and parameterless-method parentheses are optional.<br/></param>
    /// <returns>A cached typed execution plan.<br/></returns>
    public static LibraDexValuePath<TSource, TValue> Create<TSource, TValue>(string path)
        => LibraDexValuePath<TSource, TValue>.Create(Check<TSource, TValue>(path));

    private static LibraDexValuePathCheck BuildCheck(Type sourceType, Type expectedResultType, string requestedPath)
    {
        string[] segments = requestedPath.Split(
            new[] { '.' },
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length == 0)
        {
            return new LibraDexValuePathCheck(
                sourceType,
                expectedResultType,
                requestedPath,
                string.Empty,
                null,
                LibraDexValuePathFailureKind.EmptyPath,
                null,
                containsMethod: false,
                Array.Empty<MemberInfo>());
        }

        var members = new MemberInfo[segments.Length];
        var canonical = new string[segments.Length];
        Type current = sourceType;
        bool containsMethod = false;
        for (int i = 0; i < segments.Length; i++)
        {
            string raw = segments[i];
            string name = NormalizeSegment(raw);
            Type lookup = Nullable.GetUnderlyingType(current) ?? current;
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public;

            PropertyInfo? property = lookup.GetProperty(name, flags);
            if (property is not null && property.GetIndexParameters().Length == 0)
            {
                members[i] = property;
                canonical[i] = property.Name;
                current = property.PropertyType;
                continue;
            }

            FieldInfo? field = lookup.GetField(name, flags);
            if (field is not null)
            {
                members[i] = field;
                canonical[i] = field.Name;
                current = field.FieldType;
                continue;
            }

            MethodInfo? method = null;
            bool namedMethod = false;
            MethodInfo[] methods = lookup.GetMethods(flags);
            for (int methodIndex = 0; methodIndex < methods.Length; methodIndex++)
            {
                MethodInfo candidate = methods[methodIndex];
                if (!string.Equals(candidate.Name, name, StringComparison.Ordinal) ||
                    candidate.ContainsGenericParameters ||
                    candidate.GetParameters().Length != 0)
                {
                    continue;
                }

                namedMethod = true;
                if (IsAllowedMethodResult(candidate.ReturnType))
                {
                    method = candidate;
                    break;
                }
            }

            if (method is null)
            {
                return new LibraDexValuePathCheck(
                    sourceType,
                    expectedResultType,
                    requestedPath,
                    i == 0 ? string.Empty : "." + string.Join(".", canonical, 0, i),
                    current,
                    namedMethod ? LibraDexValuePathFailureKind.BlockedMethod : LibraDexValuePathFailureKind.MissingMember,
                    raw,
                    containsMethod,
                    members[..i]);
            }

            members[i] = method;
            canonical[i] = method.Name + "()";
            current = method.ReturnType;
            containsMethod = true;
        }

        Type result = Nullable.GetUnderlyingType(current) ?? current;
        Type expected = Nullable.GetUnderlyingType(expectedResultType) ?? expectedResultType;
        if (!expected.IsAssignableFrom(result) && expected != result)
        {
            return new LibraDexValuePathCheck(
                sourceType,
                expectedResultType,
                requestedPath,
                "." + string.Join('.', canonical),
                current,
                LibraDexValuePathFailureKind.ResultTypeMismatch,
                null,
                containsMethod,
                members);
        }

        return new LibraDexValuePathCheck(
            sourceType,
            expectedResultType,
            requestedPath,
            "." + string.Join('.', canonical),
            current,
            LibraDexValuePathFailureKind.None,
            null,
            containsMethod,
            members);
    }

    private static string NormalizeSegment(string value)
    {
        string normalized = value.Trim();
        return normalized.EndsWith("()", StringComparison.Ordinal)
            ? normalized[..^2].TrimEnd()
            : normalized;
    }

    private static bool IsAllowedMethodResult(Type type)
    {
        if (type == typeof(void))
            return false;

        type = Nullable.GetUnderlyingType(type) ?? type;
        if (type.IsPrimitive || type.IsEnum || type == typeof(string) || type == typeof(decimal) ||
            type == typeof(Int128) || type == typeof(UInt128) || type == typeof(Half) ||
            type == typeof(DateOnly) || type == typeof(DateTime) || type == typeof(DateTimeOffset) ||
            type == typeof(TimeOnly) || type == typeof(TimeSpan) || type == typeof(Guid) ||
            type == typeof(Version) || type == typeof(Uri) || type == typeof(byte[]) ||
            type == typeof(System.Numerics.BigInteger))
        {
            return true;
        }

        return type.Namespace?.StartsWith("System.Numerics", StringComparison.Ordinal) == true;
    }
}

/// <summary>
/// Executes one cached typed CLR value path.<br/>
/// The plan owns no source values and allocates no per-read reflection or expression objects.<br/>
/// </summary>
/// <typeparam name="TSource">Source value type.<br/></typeparam>
/// <typeparam name="TValue">Final path value type.<br/></typeparam>
public sealed class LibraDexValuePath<TSource, TValue>
{
    private static readonly ConcurrentDictionary<string, LibraDexValuePath<TSource, TValue>> Plans = new(StringComparer.Ordinal);
    private readonly Func<TSource, TValue> read;

    private LibraDexValuePath(LibraDexValuePathCheck check, Func<TSource, TValue> read)
    {
        Check = check;
        this.read = read;
    }

    /// <summary>Gets the nonexecuting structural check that produced this plan.<br/></summary>
    public LibraDexValuePathCheck Check { get; }

    /// <summary>Gets the canonical leading-dot path used as the plan cache identity.<br/></summary>
    public string Path => Check.CanonicalPath;

    /// <summary>
    /// Reads the final value from one source instance.<br/>
    /// Null intermediates, getter failures, and developer method failures remain visible to the caller.<br/>
    /// </summary>
    /// <param name="source">Source value from which the prepared path begins.<br/></param>
    /// <returns>The final typed value.<br/></returns>
    public TValue Get(TSource source)
    {
        if (source is null)
            throw new ArgumentNullException(nameof(source));
        return read(source);
    }

    /// <summary>
    /// Attempts to read the final value without propagating a null intermediate, getter failure, conversion failure, or developer method failure.<br/>
    /// </summary>
    /// <param name="source">Source value from which the prepared path begins.<br/></param>
    /// <param name="value">Receives the final typed value when successful.<br/></param>
    /// <returns><see langword="true"/> when the path completed; otherwise <see langword="false"/>.<br/></returns>
    public bool TryGet(TSource source, out TValue? value)
    {
        value = default;
        if (source is null)
            return false;

        try
        {
            value = read(source);
            return true;
        }
        catch
        {
            value = default;
            return false;
        }
    }

    /// <summary>
    /// Projects another input type to this plan's source type before executing the cached member path.<br/>
    /// The supplied projection remains caller-owned application code and is never persisted by LibraDex.<br/>
    /// </summary>
    /// <typeparam name="TInput">Input key or identity type received by the caller-owned projection.<br/></typeparam>
    /// <param name="source">Projection that supplies this plan's source value.<br/></param>
    /// <returns>A reusable input-to-value projection.<br/></returns>
    public LibraDexValueProjection<TInput, TSource, TValue> From<TInput>(Func<TInput, TSource> source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return new LibraDexValueProjection<TInput, TSource, TValue>(source, this);
    }

    internal static LibraDexValuePath<TSource, TValue> Create(LibraDexValuePathCheck check)
    {
        if (!check.IsValid)
        {
            throw new ArgumentException(
                $"CLR value path '{check.RequestedPath}' is not resolvable from '{typeof(TSource).FullName}': {check.FailureKind}" +
                (string.IsNullOrEmpty(check.FailedSegment) ? "." : $" at segment '{check.FailedSegment}'."),
                nameof(check));
        }

        return Plans.GetOrAdd(check.CanonicalPath, _ => new LibraDexValuePath<TSource, TValue>(check, Compile(check)));
    }

    private static Func<TSource, TValue> Compile(LibraDexValuePathCheck check)
    {
        ParameterExpression source = Expression.Parameter(typeof(TSource), "source");
        Expression current = source;
        Type currentType = typeof(TSource);
        MemberInfo[] members = check.Members;
        for (int i = 0; i < members.Length; i++)
        {
            Type? nullable = Nullable.GetUnderlyingType(currentType);
            if (nullable is not null)
            {
                current = Expression.Property(current, "Value");
                currentType = nullable;
            }

            switch (members[i])
            {
                case PropertyInfo property:
                    current = Expression.Property(current, property);
                    currentType = property.PropertyType;
                    break;
                case FieldInfo field:
                    current = Expression.Field(current, field);
                    currentType = field.FieldType;
                    break;
                case MethodInfo method:
                    current = Expression.Call(current, method);
                    currentType = method.ReturnType;
                    break;
                default:
                    throw new InvalidDataException($"Unsupported CLR value-path member '{members[i].MemberType}'.");
            }
        }

        return Expression.Lambda<Func<TSource, TValue>>(
            Expression.Convert(current, typeof(TValue)),
            source).Compile();
    }
}

/// <summary>
/// Projects an input key or identity to a CLR source value and then executes one cached typed member path.<br/>
/// </summary>
/// <typeparam name="TInput">Input key or identity type.<br/></typeparam>
/// <typeparam name="TSource">Developer-projected CLR source type.<br/></typeparam>
/// <typeparam name="TValue">Final member-path value type.<br/></typeparam>
public sealed class LibraDexValueProjection<TInput, TSource, TValue>
{
    private readonly Func<TInput, TSource> source;
    private readonly LibraDexValuePath<TSource, TValue> path;

    internal LibraDexValueProjection(Func<TInput, TSource> source, LibraDexValuePath<TSource, TValue> path)
    {
        this.source = source;
        this.path = path;
    }

    /// <summary>Gets the cached CLR member-path plan applied after the caller-owned source projection.<br/></summary>
    public LibraDexValuePath<TSource, TValue> Path => path;

    /// <summary>
    /// Projects one key or identity and reads its final member-path value.<br/>
    /// Source projection and member invocation failures remain visible to the caller.<br/>
    /// </summary>
    /// <param name="input">Input key or identity supplied to the caller-owned projection.<br/></param>
    /// <returns>The final typed value.<br/></returns>
    public TValue Get(TInput input) => path.Get(source(input));

    /// <summary>
    /// Attempts to project one input and read its final member-path value.<br/>
    /// </summary>
    /// <param name="input">Input key or identity supplied to the caller-owned projection.<br/></param>
    /// <param name="value">Receives the final typed value when successful.<br/></param>
    /// <returns><see langword="true"/> when both projection and path execution completed; otherwise <see langword="false"/>.<br/></returns>
    public bool TryGet(TInput input, out TValue? value)
    {
        try
        {
            return path.TryGet(source(input), out value);
        }
        catch
        {
            value = default;
            return false;
        }
    }
}

/// <summary>Provides low-ceremony ordinary-index mutations from cached CLR value paths.<br/></summary>
public static class LibraDexValuePathIndexExtensions
{
    /// <summary>
    /// Derives one key from a CLR source value and adds it to an ordinary typed LibraDex index.<br/>
    /// The index remains an independent ordinary index; the caller retains ownership of future mutation coordination.<br/>
    /// </summary>
    public static LibraDexGenericInsertResult AddFrom<TSource, TKey, TIdentity>(
        this LibraDexIndex<TKey, TIdentity> index,
        LibraDexValuePath<TSource, TKey> path,
        TSource source,
        TIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(path);
        return index.Add(path.Get(source), identity);
    }

    /// <summary>
    /// Derives one key through a caller-owned key/identity projection and adds it to an ordinary typed LibraDex index.<br/>
    /// </summary>
    public static LibraDexGenericInsertResult AddFrom<TInput, TSource, TKey, TIdentity>(
        this LibraDexIndex<TKey, TIdentity> index,
        LibraDexValueProjection<TInput, TSource, TKey> path,
        TInput input,
        TIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(path);
        return index.Add(path.Get(input), identity);
    }

    /// <summary>
    /// Derives one key from a CLR source value and removes its exact tuple from an ordinary typed LibraDex index.<br/>
    /// </summary>
    public static void DeleteFrom<TSource, TKey, TIdentity>(
        this LibraDexIndex<TKey, TIdentity> index,
        LibraDexValuePath<TSource, TKey> path,
        TSource source,
        TIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(path);
        index.Delete(path.Get(source), identity);
    }

    /// <summary>
    /// Derives one key through a caller-owned key/identity projection and removes its exact tuple from an ordinary typed LibraDex index.<br/>
    /// </summary>
    public static void DeleteFrom<TInput, TSource, TKey, TIdentity>(
        this LibraDexIndex<TKey, TIdentity> index,
        LibraDexValueProjection<TInput, TSource, TKey> path,
        TInput input,
        TIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(path);
        index.Delete(path.Get(input), identity);
    }
}
