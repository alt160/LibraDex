using System.Buffers;

namespace LibraDex;

/// <summary>
/// Supplies the shared traversal and borrowed-byte surface for typed page-native composite readers.<br/>
/// Derived reader arities expose their scalar key parts as compile-time properties while this base keeps lifetime-sensitive raw access uniform through the full persisted composite depth.<br/>
/// </summary>
public abstract class LibraDexCompositeReaderBase : IDisposable
{
    private readonly LibraDexRoutedCompositeIndex.NativeEntryCursor inner;

    /// <summary>
    /// Opens the shared page-native cursor over an already validated composite index.<br/>
    /// </summary>
    /// <param name="index">The persisted routed composite index.<br/></param>
    protected LibraDexCompositeReaderBase(LibraDexRoutedCompositeIndex index)
    {
        ArgumentNullException.ThrowIfNull(index);
        inner = index.OpenNativeEntryCursor();
    }

    /// <summary>
    /// Advances to the next composite key and identity tuple in natural index order.<br/>
    /// </summary>
    /// <returns><see langword="true"/> when current members are available; otherwise <see langword="false"/>.<br/></returns>
    public bool Read() => inner.Read();

    /// <summary>
    /// Tests whether one current key part is a persisted logical null route.<br/>
    /// </summary>
    /// <param name="partIndex">The zero-based composite part ordinal.<br/></param>
    /// <returns><see langword="true"/> when the current part is null; otherwise <see langword="false"/>.<br/></returns>
    public bool IsNull(int partIndex) => inner.IsNull(partIndex);

    /// <summary>
    /// Gets exact encoded bytes for one current composite component, including its null marker and type-specific framing.<br/>
    /// The span is borrowed from the reader and must not be retained after the next <c>Read()</c> or disposal.<br/>
    /// </summary>
    /// <param name="partIndex">The zero-based composite part ordinal.<br/></param>
    /// <returns>The borrowed encoded component span.<br/></returns>
    public ReadOnlySpan<byte> GetRawSpan(int partIndex) => inner.GetRawSpan(partIndex);

    /// <summary>
    /// Gets exact encoded bytes for one current composite component as borrowed memory, including its null marker and type-specific framing.<br/>
    /// The memory must not be retained after the next <c>Read()</c> or disposal.<br/>
    /// </summary>
    /// <param name="partIndex">The zero-based composite part ordinal.<br/></param>
    /// <returns>The borrowed encoded component memory.<br/></returns>
    public ReadOnlyMemory<byte> GetRawMem(int partIndex) => inner.GetRawMem(partIndex);

    /// <summary>
    /// Gets the logical UTF-8 payload for one current string component without allocating a UTF-16 <see cref="string"/>.<br/>
    /// The span is borrowed from the reader; null string routes return an empty span and can be distinguished through <see cref="IsNull(int)"/>.<br/>
    /// </summary>
    /// <param name="partIndex">The zero-based composite part ordinal, which must store <see cref="string"/>.<br/></param>
    /// <returns>The borrowed logical UTF-8 payload.<br/></returns>
    public ReadOnlySpan<byte> GetUtf8Span(int partIndex) => inner.GetUtf8Span(partIndex);

    /// <summary>
    /// Gets the logical UTF-8 payload for one current string component as borrowed memory without allocating a UTF-16 <see cref="string"/>.<br/>
    /// The memory is borrowed from the reader; null string routes return empty memory and can be distinguished through <see cref="IsNull(int)"/>.<br/>
    /// </summary>
    /// <param name="partIndex">The zero-based composite part ordinal, which must store <see cref="string"/>.<br/></param>
    /// <returns>The borrowed logical UTF-8 payload memory.<br/></returns>
    public ReadOnlyMemory<byte> GetUtf8Mem(int partIndex) => inner.GetUtf8Mem(partIndex);

    /// <summary>
    /// Gets exact encoded bytes for the current identity.<br/>
    /// The span is borrowed from the reader and must not be retained after the next <c>Read()</c> or disposal.<br/>
    /// </summary>
    /// <returns>The borrowed encoded identity span.<br/></returns>
    public ReadOnlySpan<byte> GetIdentityRawSpan() => inner.GetIdentityRawSpan();

    /// <summary>
    /// Gets exact encoded bytes for the current identity as borrowed memory.<br/>
    /// The memory must not be retained after the next <c>Read()</c> or disposal.<br/>
    /// </summary>
    /// <returns>The borrowed encoded identity memory.<br/></returns>
    public ReadOnlyMemory<byte> GetIdentityRawMem() => inner.GetIdentityRawMem();

    /// <summary>
    /// Returns rented page buffers and invalidates every current borrowed value.<br/>
    /// </summary>
    public void Dispose() => inner.Dispose();

    /// <summary>
    /// Gets one current key part by its statically declared type without object materialization or boxing.<br/>
    /// </summary>
    /// <typeparam name="T">The persisted CLR type of the selected part.<br/></typeparam>
    /// <param name="partIndex">The zero-based composite part ordinal.<br/></param>
    /// <returns>The directly decoded current part.<br/></returns>
    protected T ReadPart<T>(int partIndex) => inner.ReadPart<T>(partIndex);

    /// <summary>
    /// Gets the current identity by its statically declared type without object materialization or boxing.<br/>
    /// </summary>
    /// <typeparam name="T">The persisted CLR identity type.<br/></typeparam>
    /// <returns>The directly decoded current identity.<br/></returns>
    protected T ReadIdentity<T>() => inner.ReadIdentity<T>();
}

/// <summary>
/// Provides a strongly typed, page-native reader over a persisted single-part composite index.<br/>
/// The reader inherits direct scalar decoding plus borrowed UTF-8 and raw-byte access from <see cref="LibraDexCompositeReaderBase"/>.<br/>
/// </summary>
/// <typeparam name="TPart1">The first persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TIdentity">The persisted composite identity type.<br/></typeparam>
public sealed class LibraDexCompositeReader<TPart1, TIdentity> : LibraDexCompositeReaderBase
{
    /// <summary>Creates one reader over an already validated persisted composite index.<br/></summary>
    /// <param name="index">The opened routed composite index.<br/></param>
    internal LibraDexCompositeReader(LibraDexRoutedCompositeIndex index)
        : base(index)
    {
    }

    /// <summary>Gets the current first key part after a successful <c>Read()</c>.<br/></summary>
    public TPart1 Part1 => ReadPart<TPart1>(0);

    /// <summary>Gets the current identity after a successful <c>Read()</c>.<br/></summary>
    public TIdentity Identity => ReadIdentity<TIdentity>();
}

/// <summary>
/// Provides a strongly typed, page-native reader over a persisted 4-part composite index.<br/>
/// The reader inherits direct scalar decoding plus borrowed UTF-8 and raw-byte access from <see cref="LibraDexCompositeReaderBase"/>.<br/>
/// </summary>
/// <typeparam name="TPart1">The first persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart2">The second persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart3">The third persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart4">The fourth persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TIdentity">The persisted composite identity type.<br/></typeparam>
public sealed class LibraDexCompositeReader<TPart1, TPart2, TPart3, TPart4, TIdentity> : LibraDexCompositeReaderBase
{
    /// <summary>Creates one reader over an already validated persisted composite index.<br/></summary>
    /// <param name="index">The opened routed composite index.<br/></param>
    internal LibraDexCompositeReader(LibraDexRoutedCompositeIndex index)
        : base(index)
    {
    }

    /// <summary>Gets the current 1th key part after a successful <c>Read()</c>.<br/></summary>
    public TPart1 Part1 => ReadPart<TPart1>(0);

    /// <summary>Gets the current 2th key part after a successful <c>Read()</c>.<br/></summary>
    public TPart2 Part2 => ReadPart<TPart2>(1);

    /// <summary>Gets the current 3th key part after a successful <c>Read()</c>.<br/></summary>
    public TPart3 Part3 => ReadPart<TPart3>(2);

    /// <summary>Gets the current 4th key part after a successful <c>Read()</c>.<br/></summary>
    public TPart4 Part4 => ReadPart<TPart4>(3);

    /// <summary>Gets the current identity after a successful <c>Read()</c>.<br/></summary>
    public TIdentity Identity => ReadIdentity<TIdentity>();
}

/// <summary>
/// Provides a strongly typed, page-native reader over a persisted 5-part composite index.<br/>
/// The reader inherits direct scalar decoding plus borrowed UTF-8 and raw-byte access from <see cref="LibraDexCompositeReaderBase"/>.<br/>
/// </summary>
/// <typeparam name="TPart1">The first persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart2">The second persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart3">The third persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart4">The fourth persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart5">The 5th persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TIdentity">The persisted composite identity type.<br/></typeparam>
public sealed class LibraDexCompositeReader<TPart1, TPart2, TPart3, TPart4, TPart5, TIdentity> : LibraDexCompositeReaderBase
{
    /// <summary>Creates one reader over an already validated persisted composite index.<br/></summary>
    /// <param name="index">The opened routed composite index.<br/></param>
    internal LibraDexCompositeReader(LibraDexRoutedCompositeIndex index)
        : base(index)
    {
    }

    /// <summary>Gets the current 1th key part after a successful <c>Read()</c>.<br/></summary>
    public TPart1 Part1 => ReadPart<TPart1>(0);

    /// <summary>Gets the current 2th key part after a successful <c>Read()</c>.<br/></summary>
    public TPart2 Part2 => ReadPart<TPart2>(1);

    /// <summary>Gets the current 3th key part after a successful <c>Read()</c>.<br/></summary>
    public TPart3 Part3 => ReadPart<TPart3>(2);

    /// <summary>Gets the current 4th key part after a successful <c>Read()</c>.<br/></summary>
    public TPart4 Part4 => ReadPart<TPart4>(3);

    /// <summary>Gets the current 5th key part after a successful <c>Read()</c>.<br/></summary>
    public TPart5 Part5 => ReadPart<TPart5>(4);

    /// <summary>Gets the current identity after a successful <c>Read()</c>.<br/></summary>
    public TIdentity Identity => ReadIdentity<TIdentity>();
}

/// <summary>
/// Provides a strongly typed, page-native reader over a persisted 6-part composite index.<br/>
/// The reader inherits direct scalar decoding plus borrowed UTF-8 and raw-byte access from <see cref="LibraDexCompositeReaderBase"/>.<br/>
/// </summary>
/// <typeparam name="TPart1">The first persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart2">The second persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart3">The third persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart4">The fourth persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart5">The 5th persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart6">The 6th persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TIdentity">The persisted composite identity type.<br/></typeparam>
public sealed class LibraDexCompositeReader<TPart1, TPart2, TPart3, TPart4, TPart5, TPart6, TIdentity> : LibraDexCompositeReaderBase
{
    /// <summary>Creates one reader over an already validated persisted composite index.<br/></summary>
    /// <param name="index">The opened routed composite index.<br/></param>
    internal LibraDexCompositeReader(LibraDexRoutedCompositeIndex index)
        : base(index)
    {
    }

    /// <summary>Gets the current 1th key part after a successful <c>Read()</c>.<br/></summary>
    public TPart1 Part1 => ReadPart<TPart1>(0);

    /// <summary>Gets the current 2th key part after a successful <c>Read()</c>.<br/></summary>
    public TPart2 Part2 => ReadPart<TPart2>(1);

    /// <summary>Gets the current 3th key part after a successful <c>Read()</c>.<br/></summary>
    public TPart3 Part3 => ReadPart<TPart3>(2);

    /// <summary>Gets the current 4th key part after a successful <c>Read()</c>.<br/></summary>
    public TPart4 Part4 => ReadPart<TPart4>(3);

    /// <summary>Gets the current 5th key part after a successful <c>Read()</c>.<br/></summary>
    public TPart5 Part5 => ReadPart<TPart5>(4);

    /// <summary>Gets the current 6th key part after a successful <c>Read()</c>.<br/></summary>
    public TPart6 Part6 => ReadPart<TPart6>(5);

    /// <summary>Gets the current identity after a successful <c>Read()</c>.<br/></summary>
    public TIdentity Identity => ReadIdentity<TIdentity>();
}

/// <summary>
/// Provides a strongly typed, page-native reader over a persisted 7-part composite index.<br/>
/// The reader inherits direct scalar decoding plus borrowed UTF-8 and raw-byte access from <see cref="LibraDexCompositeReaderBase"/>.<br/>
/// </summary>
/// <typeparam name="TPart1">The first persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart2">The second persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart3">The third persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart4">The fourth persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart5">The 5th persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart6">The 6th persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart7">The 7th persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TIdentity">The persisted composite identity type.<br/></typeparam>
public sealed class LibraDexCompositeReader<TPart1, TPart2, TPart3, TPart4, TPart5, TPart6, TPart7, TIdentity> : LibraDexCompositeReaderBase
{
    /// <summary>Creates one reader over an already validated persisted composite index.<br/></summary>
    /// <param name="index">The opened routed composite index.<br/></param>
    internal LibraDexCompositeReader(LibraDexRoutedCompositeIndex index)
        : base(index)
    {
    }

    /// <summary>Gets the current 1th key part after a successful <c>Read()</c>.<br/></summary>
    public TPart1 Part1 => ReadPart<TPart1>(0);

    /// <summary>Gets the current 2th key part after a successful <c>Read()</c>.<br/></summary>
    public TPart2 Part2 => ReadPart<TPart2>(1);

    /// <summary>Gets the current 3th key part after a successful <c>Read()</c>.<br/></summary>
    public TPart3 Part3 => ReadPart<TPart3>(2);

    /// <summary>Gets the current 4th key part after a successful <c>Read()</c>.<br/></summary>
    public TPart4 Part4 => ReadPart<TPart4>(3);

    /// <summary>Gets the current 5th key part after a successful <c>Read()</c>.<br/></summary>
    public TPart5 Part5 => ReadPart<TPart5>(4);

    /// <summary>Gets the current 6th key part after a successful <c>Read()</c>.<br/></summary>
    public TPart6 Part6 => ReadPart<TPart6>(5);

    /// <summary>Gets the current 7th key part after a successful <c>Read()</c>.<br/></summary>
    public TPart7 Part7 => ReadPart<TPart7>(6);

    /// <summary>Gets the current identity after a successful <c>Read()</c>.<br/></summary>
    public TIdentity Identity => ReadIdentity<TIdentity>();
}

/// <summary>
/// Provides a strongly typed, page-native reader over a persisted 8-part composite index.<br/>
/// The reader inherits direct scalar decoding plus borrowed UTF-8 and raw-byte access from <see cref="LibraDexCompositeReaderBase"/>.<br/>
/// </summary>
/// <typeparam name="TPart1">The first persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart2">The second persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart3">The third persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart4">The fourth persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart5">The 5th persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart6">The 6th persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart7">The 7th persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart8">The 8th persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TIdentity">The persisted composite identity type.<br/></typeparam>
public sealed class LibraDexCompositeReader<TPart1, TPart2, TPart3, TPart4, TPart5, TPart6, TPart7, TPart8, TIdentity> : LibraDexCompositeReaderBase
{
    /// <summary>Creates one reader over an already validated persisted composite index.<br/></summary>
    /// <param name="index">The opened routed composite index.<br/></param>
    internal LibraDexCompositeReader(LibraDexRoutedCompositeIndex index)
        : base(index)
    {
    }

    /// <summary>Gets the current 1th key part after a successful <c>Read()</c>.<br/></summary>
    public TPart1 Part1 => ReadPart<TPart1>(0);

    /// <summary>Gets the current 2th key part after a successful <c>Read()</c>.<br/></summary>
    public TPart2 Part2 => ReadPart<TPart2>(1);

    /// <summary>Gets the current 3th key part after a successful <c>Read()</c>.<br/></summary>
    public TPart3 Part3 => ReadPart<TPart3>(2);

    /// <summary>Gets the current 4th key part after a successful <c>Read()</c>.<br/></summary>
    public TPart4 Part4 => ReadPart<TPart4>(3);

    /// <summary>Gets the current 5th key part after a successful <c>Read()</c>.<br/></summary>
    public TPart5 Part5 => ReadPart<TPart5>(4);

    /// <summary>Gets the current 6th key part after a successful <c>Read()</c>.<br/></summary>
    public TPart6 Part6 => ReadPart<TPart6>(5);

    /// <summary>Gets the current 7th key part after a successful <c>Read()</c>.<br/></summary>
    public TPart7 Part7 => ReadPart<TPart7>(6);

    /// <summary>Gets the current 8th key part after a successful <c>Read()</c>.<br/></summary>
    public TPart8 Part8 => ReadPart<TPart8>(7);

    /// <summary>Gets the current identity after a successful <c>Read()</c>.<br/></summary>
    public TIdentity Identity => ReadIdentity<TIdentity>();
}

/// <summary>
/// Provides a strongly typed, page-native reader over a persisted 9-part composite index.<br/>
/// The reader inherits direct scalar decoding plus borrowed UTF-8 and raw-byte access from <see cref="LibraDexCompositeReaderBase"/>.<br/>
/// </summary>
/// <typeparam name="TPart1">The first persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart2">The second persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart3">The third persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart4">The fourth persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart5">The 5th persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart6">The 6th persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart7">The 7th persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart8">The 8th persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart9">The 9th persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TIdentity">The persisted composite identity type.<br/></typeparam>
public sealed class LibraDexCompositeReader<TPart1, TPart2, TPart3, TPart4, TPart5, TPart6, TPart7, TPart8, TPart9, TIdentity> : LibraDexCompositeReaderBase
{
    /// <summary>Creates one reader over an already validated persisted composite index.<br/></summary>
    /// <param name="index">The opened routed composite index.<br/></param>
    internal LibraDexCompositeReader(LibraDexRoutedCompositeIndex index)
        : base(index)
    {
    }

    /// <summary>Gets the current 1th key part after a successful <c>Read()</c>.<br/></summary>
    public TPart1 Part1 => ReadPart<TPart1>(0);

    /// <summary>Gets the current 2th key part after a successful <c>Read()</c>.<br/></summary>
    public TPart2 Part2 => ReadPart<TPart2>(1);

    /// <summary>Gets the current 3th key part after a successful <c>Read()</c>.<br/></summary>
    public TPart3 Part3 => ReadPart<TPart3>(2);

    /// <summary>Gets the current 4th key part after a successful <c>Read()</c>.<br/></summary>
    public TPart4 Part4 => ReadPart<TPart4>(3);

    /// <summary>Gets the current 5th key part after a successful <c>Read()</c>.<br/></summary>
    public TPart5 Part5 => ReadPart<TPart5>(4);

    /// <summary>Gets the current 6th key part after a successful <c>Read()</c>.<br/></summary>
    public TPart6 Part6 => ReadPart<TPart6>(5);

    /// <summary>Gets the current 7th key part after a successful <c>Read()</c>.<br/></summary>
    public TPart7 Part7 => ReadPart<TPart7>(6);

    /// <summary>Gets the current 8th key part after a successful <c>Read()</c>.<br/></summary>
    public TPart8 Part8 => ReadPart<TPart8>(7);

    /// <summary>Gets the current 9th key part after a successful <c>Read()</c>.<br/></summary>
    public TPart9 Part9 => ReadPart<TPart9>(8);

    /// <summary>Gets the current identity after a successful <c>Read()</c>.<br/></summary>
    public TIdentity Identity => ReadIdentity<TIdentity>();
}

/// <summary>
/// Provides a strongly typed, page-native reader over a persisted 10-part composite index.<br/>
/// The reader inherits direct scalar decoding plus borrowed UTF-8 and raw-byte access from <see cref="LibraDexCompositeReaderBase"/>.<br/>
/// </summary>
/// <typeparam name="TPart1">The first persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart2">The second persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart3">The third persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart4">The fourth persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart5">The 5th persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart6">The 6th persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart7">The 7th persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart8">The 8th persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart9">The 9th persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart10">The 10th persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TIdentity">The persisted composite identity type.<br/></typeparam>
public sealed class LibraDexCompositeReader<TPart1, TPart2, TPart3, TPart4, TPart5, TPart6, TPart7, TPart8, TPart9, TPart10, TIdentity> : LibraDexCompositeReaderBase
{
    /// <summary>Creates one reader over an already validated persisted composite index.<br/></summary>
    /// <param name="index">The opened routed composite index.<br/></param>
    internal LibraDexCompositeReader(LibraDexRoutedCompositeIndex index)
        : base(index)
    {
    }

    /// <summary>Gets the current 1th key part after a successful <c>Read()</c>.<br/></summary>
    public TPart1 Part1 => ReadPart<TPart1>(0);

    /// <summary>Gets the current 2th key part after a successful <c>Read()</c>.<br/></summary>
    public TPart2 Part2 => ReadPart<TPart2>(1);

    /// <summary>Gets the current 3th key part after a successful <c>Read()</c>.<br/></summary>
    public TPart3 Part3 => ReadPart<TPart3>(2);

    /// <summary>Gets the current 4th key part after a successful <c>Read()</c>.<br/></summary>
    public TPart4 Part4 => ReadPart<TPart4>(3);

    /// <summary>Gets the current 5th key part after a successful <c>Read()</c>.<br/></summary>
    public TPart5 Part5 => ReadPart<TPart5>(4);

    /// <summary>Gets the current 6th key part after a successful <c>Read()</c>.<br/></summary>
    public TPart6 Part6 => ReadPart<TPart6>(5);

    /// <summary>Gets the current 7th key part after a successful <c>Read()</c>.<br/></summary>
    public TPart7 Part7 => ReadPart<TPart7>(6);

    /// <summary>Gets the current 8th key part after a successful <c>Read()</c>.<br/></summary>
    public TPart8 Part8 => ReadPart<TPart8>(7);

    /// <summary>Gets the current 9th key part after a successful <c>Read()</c>.<br/></summary>
    public TPart9 Part9 => ReadPart<TPart9>(8);

    /// <summary>Gets the current 10th key part after a successful <c>Read()</c>.<br/></summary>
    public TPart10 Part10 => ReadPart<TPart10>(9);

    /// <summary>Gets the current identity after a successful <c>Read()</c>.<br/></summary>
    public TIdentity Identity => ReadIdentity<TIdentity>();
}

/// <summary>
/// Provides a strongly typed, page-native reader over a persisted 11-part composite index.<br/>
/// The reader inherits direct scalar decoding plus borrowed UTF-8 and raw-byte access from <see cref="LibraDexCompositeReaderBase"/>.<br/>
/// </summary>
/// <typeparam name="TPart1">The first persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart2">The second persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart3">The third persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart4">The fourth persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart5">The 5th persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart6">The 6th persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart7">The 7th persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart8">The 8th persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart9">The 9th persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart10">The 10th persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart11">The 11th persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TIdentity">The persisted composite identity type.<br/></typeparam>
public sealed class LibraDexCompositeReader<TPart1, TPart2, TPart3, TPart4, TPart5, TPart6, TPart7, TPart8, TPart9, TPart10, TPart11, TIdentity> : LibraDexCompositeReaderBase
{
    /// <summary>Creates one reader over an already validated persisted composite index.<br/></summary>
    /// <param name="index">The opened routed composite index.<br/></param>
    internal LibraDexCompositeReader(LibraDexRoutedCompositeIndex index)
        : base(index)
    {
    }

    /// <summary>Gets the current 1th key part after a successful <c>Read()</c>.<br/></summary>
    public TPart1 Part1 => ReadPart<TPart1>(0);

    /// <summary>Gets the current 2th key part after a successful <c>Read()</c>.<br/></summary>
    public TPart2 Part2 => ReadPart<TPart2>(1);

    /// <summary>Gets the current 3th key part after a successful <c>Read()</c>.<br/></summary>
    public TPart3 Part3 => ReadPart<TPart3>(2);

    /// <summary>Gets the current 4th key part after a successful <c>Read()</c>.<br/></summary>
    public TPart4 Part4 => ReadPart<TPart4>(3);

    /// <summary>Gets the current 5th key part after a successful <c>Read()</c>.<br/></summary>
    public TPart5 Part5 => ReadPart<TPart5>(4);

    /// <summary>Gets the current 6th key part after a successful <c>Read()</c>.<br/></summary>
    public TPart6 Part6 => ReadPart<TPart6>(5);

    /// <summary>Gets the current 7th key part after a successful <c>Read()</c>.<br/></summary>
    public TPart7 Part7 => ReadPart<TPart7>(6);

    /// <summary>Gets the current 8th key part after a successful <c>Read()</c>.<br/></summary>
    public TPart8 Part8 => ReadPart<TPart8>(7);

    /// <summary>Gets the current 9th key part after a successful <c>Read()</c>.<br/></summary>
    public TPart9 Part9 => ReadPart<TPart9>(8);

    /// <summary>Gets the current 10th key part after a successful <c>Read()</c>.<br/></summary>
    public TPart10 Part10 => ReadPart<TPart10>(9);

    /// <summary>Gets the current 11th key part after a successful <c>Read()</c>.<br/></summary>
    public TPart11 Part11 => ReadPart<TPart11>(10);

    /// <summary>Gets the current identity after a successful <c>Read()</c>.<br/></summary>
    public TIdentity Identity => ReadIdentity<TIdentity>();
}

/// <summary>
/// Provides a strongly typed, page-native reader over a persisted 12-part composite index.<br/>
/// The reader inherits direct scalar decoding plus borrowed UTF-8 and raw-byte access from <see cref="LibraDexCompositeReaderBase"/>.<br/>
/// </summary>
/// <typeparam name="TPart1">The first persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart2">The second persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart3">The third persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart4">The fourth persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart5">The 5th persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart6">The 6th persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart7">The 7th persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart8">The 8th persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart9">The 9th persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart10">The 10th persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart11">The 11th persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart12">The 12th persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TIdentity">The persisted composite identity type.<br/></typeparam>
public sealed class LibraDexCompositeReader<TPart1, TPart2, TPart3, TPart4, TPart5, TPart6, TPart7, TPart8, TPart9, TPart10, TPart11, TPart12, TIdentity> : LibraDexCompositeReaderBase
{
    /// <summary>Creates one reader over an already validated persisted composite index.<br/></summary>
    /// <param name="index">The opened routed composite index.<br/></param>
    internal LibraDexCompositeReader(LibraDexRoutedCompositeIndex index)
        : base(index)
    {
    }

    /// <summary>Gets the current 1th key part after a successful <c>Read()</c>.<br/></summary>
    public TPart1 Part1 => ReadPart<TPart1>(0);

    /// <summary>Gets the current 2th key part after a successful <c>Read()</c>.<br/></summary>
    public TPart2 Part2 => ReadPart<TPart2>(1);

    /// <summary>Gets the current 3th key part after a successful <c>Read()</c>.<br/></summary>
    public TPart3 Part3 => ReadPart<TPart3>(2);

    /// <summary>Gets the current 4th key part after a successful <c>Read()</c>.<br/></summary>
    public TPart4 Part4 => ReadPart<TPart4>(3);

    /// <summary>Gets the current 5th key part after a successful <c>Read()</c>.<br/></summary>
    public TPart5 Part5 => ReadPart<TPart5>(4);

    /// <summary>Gets the current 6th key part after a successful <c>Read()</c>.<br/></summary>
    public TPart6 Part6 => ReadPart<TPart6>(5);

    /// <summary>Gets the current 7th key part after a successful <c>Read()</c>.<br/></summary>
    public TPart7 Part7 => ReadPart<TPart7>(6);

    /// <summary>Gets the current 8th key part after a successful <c>Read()</c>.<br/></summary>
    public TPart8 Part8 => ReadPart<TPart8>(7);

    /// <summary>Gets the current 9th key part after a successful <c>Read()</c>.<br/></summary>
    public TPart9 Part9 => ReadPart<TPart9>(8);

    /// <summary>Gets the current 10th key part after a successful <c>Read()</c>.<br/></summary>
    public TPart10 Part10 => ReadPart<TPart10>(9);

    /// <summary>Gets the current 11th key part after a successful <c>Read()</c>.<br/></summary>
    public TPart11 Part11 => ReadPart<TPart11>(10);

    /// <summary>Gets the current 12th key part after a successful <c>Read()</c>.<br/></summary>
    public TPart12 Part12 => ReadPart<TPart12>(11);

    /// <summary>Gets the current identity after a successful <c>Read()</c>.<br/></summary>
    public TIdentity Identity => ReadIdentity<TIdentity>();
}

/// <summary>
/// Provides a strongly typed, page-native reader over a persisted 13-part composite index.<br/>
/// The reader inherits direct scalar decoding plus borrowed UTF-8 and raw-byte access from <see cref="LibraDexCompositeReaderBase"/>.<br/>
/// </summary>
/// <typeparam name="TPart1">The first persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart2">The second persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart3">The third persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart4">The fourth persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart5">The 5th persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart6">The 6th persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart7">The 7th persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart8">The 8th persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart9">The 9th persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart10">The 10th persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart11">The 11th persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart12">The 12th persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart13">The 13th persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TIdentity">The persisted composite identity type.<br/></typeparam>
public sealed class LibraDexCompositeReader<TPart1, TPart2, TPart3, TPart4, TPart5, TPart6, TPart7, TPart8, TPart9, TPart10, TPart11, TPart12, TPart13, TIdentity> : LibraDexCompositeReaderBase
{
    /// <summary>Creates one reader over an already validated persisted composite index.<br/></summary>
    /// <param name="index">The opened routed composite index.<br/></param>
    internal LibraDexCompositeReader(LibraDexRoutedCompositeIndex index)
        : base(index)
    {
    }

    /// <summary>Gets the current 1th key part after a successful <c>Read()</c>.<br/></summary>
    public TPart1 Part1 => ReadPart<TPart1>(0);

    /// <summary>Gets the current 2th key part after a successful <c>Read()</c>.<br/></summary>
    public TPart2 Part2 => ReadPart<TPart2>(1);

    /// <summary>Gets the current 3th key part after a successful <c>Read()</c>.<br/></summary>
    public TPart3 Part3 => ReadPart<TPart3>(2);

    /// <summary>Gets the current 4th key part after a successful <c>Read()</c>.<br/></summary>
    public TPart4 Part4 => ReadPart<TPart4>(3);

    /// <summary>Gets the current 5th key part after a successful <c>Read()</c>.<br/></summary>
    public TPart5 Part5 => ReadPart<TPart5>(4);

    /// <summary>Gets the current 6th key part after a successful <c>Read()</c>.<br/></summary>
    public TPart6 Part6 => ReadPart<TPart6>(5);

    /// <summary>Gets the current 7th key part after a successful <c>Read()</c>.<br/></summary>
    public TPart7 Part7 => ReadPart<TPart7>(6);

    /// <summary>Gets the current 8th key part after a successful <c>Read()</c>.<br/></summary>
    public TPart8 Part8 => ReadPart<TPart8>(7);

    /// <summary>Gets the current 9th key part after a successful <c>Read()</c>.<br/></summary>
    public TPart9 Part9 => ReadPart<TPart9>(8);

    /// <summary>Gets the current 10th key part after a successful <c>Read()</c>.<br/></summary>
    public TPart10 Part10 => ReadPart<TPart10>(9);

    /// <summary>Gets the current 11th key part after a successful <c>Read()</c>.<br/></summary>
    public TPart11 Part11 => ReadPart<TPart11>(10);

    /// <summary>Gets the current 12th key part after a successful <c>Read()</c>.<br/></summary>
    public TPart12 Part12 => ReadPart<TPart12>(11);

    /// <summary>Gets the current 13th key part after a successful <c>Read()</c>.<br/></summary>
    public TPart13 Part13 => ReadPart<TPart13>(12);

    /// <summary>Gets the current identity after a successful <c>Read()</c>.<br/></summary>
    public TIdentity Identity => ReadIdentity<TIdentity>();
}

/// <summary>
/// Provides a strongly typed, page-native reader over a persisted 14-part composite index.<br/>
/// The reader inherits direct scalar decoding plus borrowed UTF-8 and raw-byte access from <see cref="LibraDexCompositeReaderBase"/>.<br/>
/// </summary>
/// <typeparam name="TPart1">The first persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart2">The second persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart3">The third persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart4">The fourth persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart5">The 5th persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart6">The 6th persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart7">The 7th persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart8">The 8th persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart9">The 9th persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart10">The 10th persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart11">The 11th persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart12">The 12th persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart13">The 13th persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart14">The 14th persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TIdentity">The persisted composite identity type.<br/></typeparam>
public sealed class LibraDexCompositeReader<TPart1, TPart2, TPart3, TPart4, TPart5, TPart6, TPart7, TPart8, TPart9, TPart10, TPart11, TPart12, TPart13, TPart14, TIdentity> : LibraDexCompositeReaderBase
{
    /// <summary>Creates one reader over an already validated persisted composite index.<br/></summary>
    /// <param name="index">The opened routed composite index.<br/></param>
    internal LibraDexCompositeReader(LibraDexRoutedCompositeIndex index)
        : base(index)
    {
    }

    /// <summary>Gets the current 1th key part after a successful <c>Read()</c>.<br/></summary>
    public TPart1 Part1 => ReadPart<TPart1>(0);

    /// <summary>Gets the current 2th key part after a successful <c>Read()</c>.<br/></summary>
    public TPart2 Part2 => ReadPart<TPart2>(1);

    /// <summary>Gets the current 3th key part after a successful <c>Read()</c>.<br/></summary>
    public TPart3 Part3 => ReadPart<TPart3>(2);

    /// <summary>Gets the current 4th key part after a successful <c>Read()</c>.<br/></summary>
    public TPart4 Part4 => ReadPart<TPart4>(3);

    /// <summary>Gets the current 5th key part after a successful <c>Read()</c>.<br/></summary>
    public TPart5 Part5 => ReadPart<TPart5>(4);

    /// <summary>Gets the current 6th key part after a successful <c>Read()</c>.<br/></summary>
    public TPart6 Part6 => ReadPart<TPart6>(5);

    /// <summary>Gets the current 7th key part after a successful <c>Read()</c>.<br/></summary>
    public TPart7 Part7 => ReadPart<TPart7>(6);

    /// <summary>Gets the current 8th key part after a successful <c>Read()</c>.<br/></summary>
    public TPart8 Part8 => ReadPart<TPart8>(7);

    /// <summary>Gets the current 9th key part after a successful <c>Read()</c>.<br/></summary>
    public TPart9 Part9 => ReadPart<TPart9>(8);

    /// <summary>Gets the current 10th key part after a successful <c>Read()</c>.<br/></summary>
    public TPart10 Part10 => ReadPart<TPart10>(9);

    /// <summary>Gets the current 11th key part after a successful <c>Read()</c>.<br/></summary>
    public TPart11 Part11 => ReadPart<TPart11>(10);

    /// <summary>Gets the current 12th key part after a successful <c>Read()</c>.<br/></summary>
    public TPart12 Part12 => ReadPart<TPart12>(11);

    /// <summary>Gets the current 13th key part after a successful <c>Read()</c>.<br/></summary>
    public TPart13 Part13 => ReadPart<TPart13>(12);

    /// <summary>Gets the current 14th key part after a successful <c>Read()</c>.<br/></summary>
    public TPart14 Part14 => ReadPart<TPart14>(13);

    /// <summary>Gets the current identity after a successful <c>Read()</c>.<br/></summary>
    public TIdentity Identity => ReadIdentity<TIdentity>();
}

/// <summary>
/// Provides a strongly typed, page-native reader over a persisted 15-part composite index.<br/>
/// The reader inherits direct scalar decoding plus borrowed UTF-8 and raw-byte access from <see cref="LibraDexCompositeReaderBase"/>.<br/>
/// </summary>
/// <typeparam name="TPart1">The first persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart2">The second persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart3">The third persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart4">The fourth persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart5">The 5th persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart6">The 6th persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart7">The 7th persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart8">The 8th persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart9">The 9th persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart10">The 10th persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart11">The 11th persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart12">The 12th persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart13">The 13th persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart14">The 14th persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart15">The 15th persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TIdentity">The persisted composite identity type.<br/></typeparam>
public sealed class LibraDexCompositeReader<TPart1, TPart2, TPart3, TPart4, TPart5, TPart6, TPart7, TPart8, TPart9, TPart10, TPart11, TPart12, TPart13, TPart14, TPart15, TIdentity> : LibraDexCompositeReaderBase
{
    /// <summary>Creates one reader over an already validated persisted composite index.<br/></summary>
    /// <param name="index">The opened routed composite index.<br/></param>
    internal LibraDexCompositeReader(LibraDexRoutedCompositeIndex index)
        : base(index)
    {
    }

    /// <summary>Gets the current 1th key part after a successful <c>Read()</c>.<br/></summary>
    public TPart1 Part1 => ReadPart<TPart1>(0);

    /// <summary>Gets the current 2th key part after a successful <c>Read()</c>.<br/></summary>
    public TPart2 Part2 => ReadPart<TPart2>(1);

    /// <summary>Gets the current 3th key part after a successful <c>Read()</c>.<br/></summary>
    public TPart3 Part3 => ReadPart<TPart3>(2);

    /// <summary>Gets the current 4th key part after a successful <c>Read()</c>.<br/></summary>
    public TPart4 Part4 => ReadPart<TPart4>(3);

    /// <summary>Gets the current 5th key part after a successful <c>Read()</c>.<br/></summary>
    public TPart5 Part5 => ReadPart<TPart5>(4);

    /// <summary>Gets the current 6th key part after a successful <c>Read()</c>.<br/></summary>
    public TPart6 Part6 => ReadPart<TPart6>(5);

    /// <summary>Gets the current 7th key part after a successful <c>Read()</c>.<br/></summary>
    public TPart7 Part7 => ReadPart<TPart7>(6);

    /// <summary>Gets the current 8th key part after a successful <c>Read()</c>.<br/></summary>
    public TPart8 Part8 => ReadPart<TPart8>(7);

    /// <summary>Gets the current 9th key part after a successful <c>Read()</c>.<br/></summary>
    public TPart9 Part9 => ReadPart<TPart9>(8);

    /// <summary>Gets the current 10th key part after a successful <c>Read()</c>.<br/></summary>
    public TPart10 Part10 => ReadPart<TPart10>(9);

    /// <summary>Gets the current 11th key part after a successful <c>Read()</c>.<br/></summary>
    public TPart11 Part11 => ReadPart<TPart11>(10);

    /// <summary>Gets the current 12th key part after a successful <c>Read()</c>.<br/></summary>
    public TPart12 Part12 => ReadPart<TPart12>(11);

    /// <summary>Gets the current 13th key part after a successful <c>Read()</c>.<br/></summary>
    public TPart13 Part13 => ReadPart<TPart13>(12);

    /// <summary>Gets the current 14th key part after a successful <c>Read()</c>.<br/></summary>
    public TPart14 Part14 => ReadPart<TPart14>(13);

    /// <summary>Gets the current 15th key part after a successful <c>Read()</c>.<br/></summary>
    public TPart15 Part15 => ReadPart<TPart15>(14);

    /// <summary>Gets the current identity after a successful <c>Read()</c>.<br/></summary>
    public TIdentity Identity => ReadIdentity<TIdentity>();
}

/// <summary>
/// Provides a strongly typed, page-native reader over a persisted 16-part composite index.<br/>
/// The reader inherits direct scalar decoding plus borrowed UTF-8 and raw-byte access from <see cref="LibraDexCompositeReaderBase"/>.<br/>
/// </summary>
/// <typeparam name="TPart1">The first persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart2">The second persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart3">The third persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart4">The fourth persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart5">The 5th persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart6">The 6th persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart7">The 7th persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart8">The 8th persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart9">The 9th persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart10">The 10th persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart11">The 11th persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart12">The 12th persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart13">The 13th persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart14">The 14th persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart15">The 15th persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart16">The 16th persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TIdentity">The persisted composite identity type.<br/></typeparam>
public sealed class LibraDexCompositeReader<TPart1, TPart2, TPart3, TPart4, TPart5, TPart6, TPart7, TPart8, TPart9, TPart10, TPart11, TPart12, TPart13, TPart14, TPart15, TPart16, TIdentity> : LibraDexCompositeReaderBase
{
    /// <summary>Creates one reader over an already validated persisted composite index.<br/></summary>
    /// <param name="index">The opened routed composite index.<br/></param>
    internal LibraDexCompositeReader(LibraDexRoutedCompositeIndex index)
        : base(index)
    {
    }

    /// <summary>Gets the current 1th key part after a successful <c>Read()</c>.<br/></summary>
    public TPart1 Part1 => ReadPart<TPart1>(0);

    /// <summary>Gets the current 2th key part after a successful <c>Read()</c>.<br/></summary>
    public TPart2 Part2 => ReadPart<TPart2>(1);

    /// <summary>Gets the current 3th key part after a successful <c>Read()</c>.<br/></summary>
    public TPart3 Part3 => ReadPart<TPart3>(2);

    /// <summary>Gets the current 4th key part after a successful <c>Read()</c>.<br/></summary>
    public TPart4 Part4 => ReadPart<TPart4>(3);

    /// <summary>Gets the current 5th key part after a successful <c>Read()</c>.<br/></summary>
    public TPart5 Part5 => ReadPart<TPart5>(4);

    /// <summary>Gets the current 6th key part after a successful <c>Read()</c>.<br/></summary>
    public TPart6 Part6 => ReadPart<TPart6>(5);

    /// <summary>Gets the current 7th key part after a successful <c>Read()</c>.<br/></summary>
    public TPart7 Part7 => ReadPart<TPart7>(6);

    /// <summary>Gets the current 8th key part after a successful <c>Read()</c>.<br/></summary>
    public TPart8 Part8 => ReadPart<TPart8>(7);

    /// <summary>Gets the current 9th key part after a successful <c>Read()</c>.<br/></summary>
    public TPart9 Part9 => ReadPart<TPart9>(8);

    /// <summary>Gets the current 10th key part after a successful <c>Read()</c>.<br/></summary>
    public TPart10 Part10 => ReadPart<TPart10>(9);

    /// <summary>Gets the current 11th key part after a successful <c>Read()</c>.<br/></summary>
    public TPart11 Part11 => ReadPart<TPart11>(10);

    /// <summary>Gets the current 12th key part after a successful <c>Read()</c>.<br/></summary>
    public TPart12 Part12 => ReadPart<TPart12>(11);

    /// <summary>Gets the current 13th key part after a successful <c>Read()</c>.<br/></summary>
    public TPart13 Part13 => ReadPart<TPart13>(12);

    /// <summary>Gets the current 14th key part after a successful <c>Read()</c>.<br/></summary>
    public TPart14 Part14 => ReadPart<TPart14>(13);

    /// <summary>Gets the current 15th key part after a successful <c>Read()</c>.<br/></summary>
    public TPart15 Part15 => ReadPart<TPart15>(14);

    /// <summary>Gets the current 16th key part after a successful <c>Read()</c>.<br/></summary>
    public TPart16 Part16 => ReadPart<TPart16>(15);

    /// <summary>Gets the current identity after a successful <c>Read()</c>.<br/></summary>
    public TIdentity Identity => ReadIdentity<TIdentity>();
}

/// <summary>
/// Adds direct 4-through-16-part reader entry points whose generic arities mirror the persisted composite-depth contract.<br/>
/// </summary>
public static class LibraDexCompositeReaderArityExtensions
{

    /// <summary>
    /// Opens a page-native typed reader for a persisted single-part composite index.<br/>
    /// The declared part and identity types are validated against the stored index definition before traversal starts.<br/>
    /// </summary>
    /// <typeparam name="TPart1">The expected first composite part type.<br/></typeparam>
    /// <typeparam name="TIdentity">The expected composite identity type.<br/></typeparam>
    /// <param name="indexSet">The catalog index set that owns the composite index.<br/></param>
    /// <param name="name">The composite index name.<br/></param>
    /// <returns>An unpositioned page-native reader owned by the caller.<br/></returns>
    public static LibraDexCompositeReader<TPart1, TIdentity> OpenCompositeReader<TPart1, TIdentity>(
        this CatalogIdentityGroupIndexes indexSet,
        string name)
        => new(OpenTypedCompositeReaderIndex(indexSet, name, typeof(TIdentity), typeof(TPart1)));

    /// <summary>
    /// Opens and validates a typed 4-part composite reader by its catalog index name.<br/>
    /// </summary>
/// <typeparam name="TPart1">The first persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart2">The second persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart3">The third persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart4">The fourth persisted composite key-part type.<br/></typeparam>
    /// <typeparam name="TIdentity">The expected composite identity type.<br/></typeparam>
    /// <param name="indexSet">The catalog index set that owns the composite index.<br/></param>
    /// <param name="name">The composite index name.<br/></param>
    /// <returns>An unpositioned page-native reader owned by the caller.<br/></returns>
    public static LibraDexCompositeReader<TPart1, TPart2, TPart3, TPart4, TIdentity> OpenCompositeReader<TPart1, TPart2, TPart3, TPart4, TIdentity>(
        this CatalogIdentityGroupIndexes indexSet,
        string name)
        => new(OpenTypedCompositeReaderIndex(indexSet, name, typeof(TIdentity), typeof(TPart1), typeof(TPart2), typeof(TPart3), typeof(TPart4)));

    /// <summary>
    /// Opens and validates a typed 5-part composite reader by its catalog index name.<br/>
    /// </summary>
/// <typeparam name="TPart1">The first persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart2">The second persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart3">The third persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart4">The fourth persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart5">The 5th persisted composite key-part type.<br/></typeparam>
    /// <typeparam name="TIdentity">The expected composite identity type.<br/></typeparam>
    /// <param name="indexSet">The catalog index set that owns the composite index.<br/></param>
    /// <param name="name">The composite index name.<br/></param>
    /// <returns>An unpositioned page-native reader owned by the caller.<br/></returns>
    public static LibraDexCompositeReader<TPart1, TPart2, TPart3, TPart4, TPart5, TIdentity> OpenCompositeReader<TPart1, TPart2, TPart3, TPart4, TPart5, TIdentity>(
        this CatalogIdentityGroupIndexes indexSet,
        string name)
        => new(OpenTypedCompositeReaderIndex(indexSet, name, typeof(TIdentity), typeof(TPart1), typeof(TPart2), typeof(TPart3), typeof(TPart4), typeof(TPart5)));

    /// <summary>
    /// Opens and validates a typed 6-part composite reader by its catalog index name.<br/>
    /// </summary>
/// <typeparam name="TPart1">The first persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart2">The second persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart3">The third persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart4">The fourth persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart5">The 5th persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart6">The 6th persisted composite key-part type.<br/></typeparam>
    /// <typeparam name="TIdentity">The expected composite identity type.<br/></typeparam>
    /// <param name="indexSet">The catalog index set that owns the composite index.<br/></param>
    /// <param name="name">The composite index name.<br/></param>
    /// <returns>An unpositioned page-native reader owned by the caller.<br/></returns>
    public static LibraDexCompositeReader<TPart1, TPart2, TPart3, TPart4, TPart5, TPart6, TIdentity> OpenCompositeReader<TPart1, TPart2, TPart3, TPart4, TPart5, TPart6, TIdentity>(
        this CatalogIdentityGroupIndexes indexSet,
        string name)
        => new(OpenTypedCompositeReaderIndex(indexSet, name, typeof(TIdentity), typeof(TPart1), typeof(TPart2), typeof(TPart3), typeof(TPart4), typeof(TPart5), typeof(TPart6)));

    /// <summary>
    /// Opens and validates a typed 7-part composite reader by its catalog index name.<br/>
    /// </summary>
/// <typeparam name="TPart1">The first persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart2">The second persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart3">The third persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart4">The fourth persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart5">The 5th persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart6">The 6th persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart7">The 7th persisted composite key-part type.<br/></typeparam>
    /// <typeparam name="TIdentity">The expected composite identity type.<br/></typeparam>
    /// <param name="indexSet">The catalog index set that owns the composite index.<br/></param>
    /// <param name="name">The composite index name.<br/></param>
    /// <returns>An unpositioned page-native reader owned by the caller.<br/></returns>
    public static LibraDexCompositeReader<TPart1, TPart2, TPart3, TPart4, TPart5, TPart6, TPart7, TIdentity> OpenCompositeReader<TPart1, TPart2, TPart3, TPart4, TPart5, TPart6, TPart7, TIdentity>(
        this CatalogIdentityGroupIndexes indexSet,
        string name)
        => new(OpenTypedCompositeReaderIndex(indexSet, name, typeof(TIdentity), typeof(TPart1), typeof(TPart2), typeof(TPart3), typeof(TPart4), typeof(TPart5), typeof(TPart6), typeof(TPart7)));

    /// <summary>
    /// Opens and validates a typed 8-part composite reader by its catalog index name.<br/>
    /// </summary>
/// <typeparam name="TPart1">The first persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart2">The second persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart3">The third persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart4">The fourth persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart5">The 5th persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart6">The 6th persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart7">The 7th persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart8">The 8th persisted composite key-part type.<br/></typeparam>
    /// <typeparam name="TIdentity">The expected composite identity type.<br/></typeparam>
    /// <param name="indexSet">The catalog index set that owns the composite index.<br/></param>
    /// <param name="name">The composite index name.<br/></param>
    /// <returns>An unpositioned page-native reader owned by the caller.<br/></returns>
    public static LibraDexCompositeReader<TPart1, TPart2, TPart3, TPart4, TPart5, TPart6, TPart7, TPart8, TIdentity> OpenCompositeReader<TPart1, TPart2, TPart3, TPart4, TPart5, TPart6, TPart7, TPart8, TIdentity>(
        this CatalogIdentityGroupIndexes indexSet,
        string name)
        => new(OpenTypedCompositeReaderIndex(indexSet, name, typeof(TIdentity), typeof(TPart1), typeof(TPart2), typeof(TPart3), typeof(TPart4), typeof(TPart5), typeof(TPart6), typeof(TPart7), typeof(TPart8)));

    /// <summary>
    /// Opens and validates a typed 9-part composite reader by its catalog index name.<br/>
    /// </summary>
/// <typeparam name="TPart1">The first persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart2">The second persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart3">The third persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart4">The fourth persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart5">The 5th persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart6">The 6th persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart7">The 7th persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart8">The 8th persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart9">The 9th persisted composite key-part type.<br/></typeparam>
    /// <typeparam name="TIdentity">The expected composite identity type.<br/></typeparam>
    /// <param name="indexSet">The catalog index set that owns the composite index.<br/></param>
    /// <param name="name">The composite index name.<br/></param>
    /// <returns>An unpositioned page-native reader owned by the caller.<br/></returns>
    public static LibraDexCompositeReader<TPart1, TPart2, TPart3, TPart4, TPart5, TPart6, TPart7, TPart8, TPart9, TIdentity> OpenCompositeReader<TPart1, TPart2, TPart3, TPart4, TPart5, TPart6, TPart7, TPart8, TPart9, TIdentity>(
        this CatalogIdentityGroupIndexes indexSet,
        string name)
        => new(OpenTypedCompositeReaderIndex(indexSet, name, typeof(TIdentity), typeof(TPart1), typeof(TPart2), typeof(TPart3), typeof(TPart4), typeof(TPart5), typeof(TPart6), typeof(TPart7), typeof(TPart8), typeof(TPart9)));

    /// <summary>
    /// Opens and validates a typed 10-part composite reader by its catalog index name.<br/>
    /// </summary>
/// <typeparam name="TPart1">The first persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart2">The second persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart3">The third persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart4">The fourth persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart5">The 5th persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart6">The 6th persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart7">The 7th persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart8">The 8th persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart9">The 9th persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart10">The 10th persisted composite key-part type.<br/></typeparam>
    /// <typeparam name="TIdentity">The expected composite identity type.<br/></typeparam>
    /// <param name="indexSet">The catalog index set that owns the composite index.<br/></param>
    /// <param name="name">The composite index name.<br/></param>
    /// <returns>An unpositioned page-native reader owned by the caller.<br/></returns>
    public static LibraDexCompositeReader<TPart1, TPart2, TPart3, TPart4, TPart5, TPart6, TPart7, TPart8, TPart9, TPart10, TIdentity> OpenCompositeReader<TPart1, TPart2, TPart3, TPart4, TPart5, TPart6, TPart7, TPart8, TPart9, TPart10, TIdentity>(
        this CatalogIdentityGroupIndexes indexSet,
        string name)
        => new(OpenTypedCompositeReaderIndex(indexSet, name, typeof(TIdentity), typeof(TPart1), typeof(TPart2), typeof(TPart3), typeof(TPart4), typeof(TPart5), typeof(TPart6), typeof(TPart7), typeof(TPart8), typeof(TPart9), typeof(TPart10)));

    /// <summary>
    /// Opens and validates a typed 11-part composite reader by its catalog index name.<br/>
    /// </summary>
/// <typeparam name="TPart1">The first persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart2">The second persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart3">The third persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart4">The fourth persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart5">The 5th persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart6">The 6th persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart7">The 7th persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart8">The 8th persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart9">The 9th persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart10">The 10th persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart11">The 11th persisted composite key-part type.<br/></typeparam>
    /// <typeparam name="TIdentity">The expected composite identity type.<br/></typeparam>
    /// <param name="indexSet">The catalog index set that owns the composite index.<br/></param>
    /// <param name="name">The composite index name.<br/></param>
    /// <returns>An unpositioned page-native reader owned by the caller.<br/></returns>
    public static LibraDexCompositeReader<TPart1, TPart2, TPart3, TPart4, TPart5, TPart6, TPart7, TPart8, TPart9, TPart10, TPart11, TIdentity> OpenCompositeReader<TPart1, TPart2, TPart3, TPart4, TPart5, TPart6, TPart7, TPart8, TPart9, TPart10, TPart11, TIdentity>(
        this CatalogIdentityGroupIndexes indexSet,
        string name)
        => new(OpenTypedCompositeReaderIndex(indexSet, name, typeof(TIdentity), typeof(TPart1), typeof(TPart2), typeof(TPart3), typeof(TPart4), typeof(TPart5), typeof(TPart6), typeof(TPart7), typeof(TPart8), typeof(TPart9), typeof(TPart10), typeof(TPart11)));

    /// <summary>
    /// Opens and validates a typed 12-part composite reader by its catalog index name.<br/>
    /// </summary>
/// <typeparam name="TPart1">The first persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart2">The second persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart3">The third persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart4">The fourth persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart5">The 5th persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart6">The 6th persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart7">The 7th persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart8">The 8th persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart9">The 9th persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart10">The 10th persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart11">The 11th persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart12">The 12th persisted composite key-part type.<br/></typeparam>
    /// <typeparam name="TIdentity">The expected composite identity type.<br/></typeparam>
    /// <param name="indexSet">The catalog index set that owns the composite index.<br/></param>
    /// <param name="name">The composite index name.<br/></param>
    /// <returns>An unpositioned page-native reader owned by the caller.<br/></returns>
    public static LibraDexCompositeReader<TPart1, TPart2, TPart3, TPart4, TPart5, TPart6, TPart7, TPart8, TPart9, TPart10, TPart11, TPart12, TIdentity> OpenCompositeReader<TPart1, TPart2, TPart3, TPart4, TPart5, TPart6, TPart7, TPart8, TPart9, TPart10, TPart11, TPart12, TIdentity>(
        this CatalogIdentityGroupIndexes indexSet,
        string name)
        => new(OpenTypedCompositeReaderIndex(indexSet, name, typeof(TIdentity), typeof(TPart1), typeof(TPart2), typeof(TPart3), typeof(TPart4), typeof(TPart5), typeof(TPart6), typeof(TPart7), typeof(TPart8), typeof(TPart9), typeof(TPart10), typeof(TPart11), typeof(TPart12)));

    /// <summary>
    /// Opens and validates a typed 13-part composite reader by its catalog index name.<br/>
    /// </summary>
/// <typeparam name="TPart1">The first persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart2">The second persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart3">The third persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart4">The fourth persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart5">The 5th persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart6">The 6th persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart7">The 7th persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart8">The 8th persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart9">The 9th persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart10">The 10th persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart11">The 11th persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart12">The 12th persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart13">The 13th persisted composite key-part type.<br/></typeparam>
    /// <typeparam name="TIdentity">The expected composite identity type.<br/></typeparam>
    /// <param name="indexSet">The catalog index set that owns the composite index.<br/></param>
    /// <param name="name">The composite index name.<br/></param>
    /// <returns>An unpositioned page-native reader owned by the caller.<br/></returns>
    public static LibraDexCompositeReader<TPart1, TPart2, TPart3, TPart4, TPart5, TPart6, TPart7, TPart8, TPart9, TPart10, TPart11, TPart12, TPart13, TIdentity> OpenCompositeReader<TPart1, TPart2, TPart3, TPart4, TPart5, TPart6, TPart7, TPart8, TPart9, TPart10, TPart11, TPart12, TPart13, TIdentity>(
        this CatalogIdentityGroupIndexes indexSet,
        string name)
        => new(OpenTypedCompositeReaderIndex(indexSet, name, typeof(TIdentity), typeof(TPart1), typeof(TPart2), typeof(TPart3), typeof(TPart4), typeof(TPart5), typeof(TPart6), typeof(TPart7), typeof(TPart8), typeof(TPart9), typeof(TPart10), typeof(TPart11), typeof(TPart12), typeof(TPart13)));

    /// <summary>
    /// Opens and validates a typed 14-part composite reader by its catalog index name.<br/>
    /// </summary>
/// <typeparam name="TPart1">The first persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart2">The second persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart3">The third persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart4">The fourth persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart5">The 5th persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart6">The 6th persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart7">The 7th persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart8">The 8th persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart9">The 9th persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart10">The 10th persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart11">The 11th persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart12">The 12th persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart13">The 13th persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart14">The 14th persisted composite key-part type.<br/></typeparam>
    /// <typeparam name="TIdentity">The expected composite identity type.<br/></typeparam>
    /// <param name="indexSet">The catalog index set that owns the composite index.<br/></param>
    /// <param name="name">The composite index name.<br/></param>
    /// <returns>An unpositioned page-native reader owned by the caller.<br/></returns>
    public static LibraDexCompositeReader<TPart1, TPart2, TPart3, TPart4, TPart5, TPart6, TPart7, TPart8, TPart9, TPart10, TPart11, TPart12, TPart13, TPart14, TIdentity> OpenCompositeReader<TPart1, TPart2, TPart3, TPart4, TPart5, TPart6, TPart7, TPart8, TPart9, TPart10, TPart11, TPart12, TPart13, TPart14, TIdentity>(
        this CatalogIdentityGroupIndexes indexSet,
        string name)
        => new(OpenTypedCompositeReaderIndex(indexSet, name, typeof(TIdentity), typeof(TPart1), typeof(TPart2), typeof(TPart3), typeof(TPart4), typeof(TPart5), typeof(TPart6), typeof(TPart7), typeof(TPart8), typeof(TPart9), typeof(TPart10), typeof(TPart11), typeof(TPart12), typeof(TPart13), typeof(TPart14)));

    /// <summary>
    /// Opens and validates a typed 15-part composite reader by its catalog index name.<br/>
    /// </summary>
/// <typeparam name="TPart1">The first persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart2">The second persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart3">The third persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart4">The fourth persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart5">The 5th persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart6">The 6th persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart7">The 7th persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart8">The 8th persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart9">The 9th persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart10">The 10th persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart11">The 11th persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart12">The 12th persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart13">The 13th persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart14">The 14th persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart15">The 15th persisted composite key-part type.<br/></typeparam>
    /// <typeparam name="TIdentity">The expected composite identity type.<br/></typeparam>
    /// <param name="indexSet">The catalog index set that owns the composite index.<br/></param>
    /// <param name="name">The composite index name.<br/></param>
    /// <returns>An unpositioned page-native reader owned by the caller.<br/></returns>
    public static LibraDexCompositeReader<TPart1, TPart2, TPart3, TPart4, TPart5, TPart6, TPart7, TPart8, TPart9, TPart10, TPart11, TPart12, TPart13, TPart14, TPart15, TIdentity> OpenCompositeReader<TPart1, TPart2, TPart3, TPart4, TPart5, TPart6, TPart7, TPart8, TPart9, TPart10, TPart11, TPart12, TPart13, TPart14, TPart15, TIdentity>(
        this CatalogIdentityGroupIndexes indexSet,
        string name)
        => new(OpenTypedCompositeReaderIndex(indexSet, name, typeof(TIdentity), typeof(TPart1), typeof(TPart2), typeof(TPart3), typeof(TPart4), typeof(TPart5), typeof(TPart6), typeof(TPart7), typeof(TPart8), typeof(TPart9), typeof(TPart10), typeof(TPart11), typeof(TPart12), typeof(TPart13), typeof(TPart14), typeof(TPart15)));

    /// <summary>
    /// Opens and validates a typed 16-part composite reader by its catalog index name.<br/>
    /// </summary>
/// <typeparam name="TPart1">The first persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart2">The second persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart3">The third persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart4">The fourth persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart5">The 5th persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart6">The 6th persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart7">The 7th persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart8">The 8th persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart9">The 9th persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart10">The 10th persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart11">The 11th persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart12">The 12th persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart13">The 13th persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart14">The 14th persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart15">The 15th persisted composite key-part type.<br/></typeparam>
/// <typeparam name="TPart16">The 16th persisted composite key-part type.<br/></typeparam>
    /// <typeparam name="TIdentity">The expected composite identity type.<br/></typeparam>
    /// <param name="indexSet">The catalog index set that owns the composite index.<br/></param>
    /// <param name="name">The composite index name.<br/></param>
    /// <returns>An unpositioned page-native reader owned by the caller.<br/></returns>
    public static LibraDexCompositeReader<TPart1, TPart2, TPart3, TPart4, TPart5, TPart6, TPart7, TPart8, TPart9, TPart10, TPart11, TPart12, TPart13, TPart14, TPart15, TPart16, TIdentity> OpenCompositeReader<TPart1, TPart2, TPart3, TPart4, TPart5, TPart6, TPart7, TPart8, TPart9, TPart10, TPart11, TPart12, TPart13, TPart14, TPart15, TPart16, TIdentity>(
        this CatalogIdentityGroupIndexes indexSet,
        string name)
        => new(OpenTypedCompositeReaderIndex(indexSet, name, typeof(TIdentity), typeof(TPart1), typeof(TPart2), typeof(TPart3), typeof(TPart4), typeof(TPart5), typeof(TPart6), typeof(TPart7), typeof(TPart8), typeof(TPart9), typeof(TPart10), typeof(TPart11), typeof(TPart12), typeof(TPart13), typeof(TPart14), typeof(TPart15), typeof(TPart16)));

    private static LibraDexRoutedCompositeIndex OpenTypedCompositeReaderIndex(
        CatalogIdentityGroupIndexes indexSet,
        string name,
        Type identityType,
        params Type[] partTypes)
    {
        ArgumentNullException.ThrowIfNull(indexSet);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(identityType);
        ArgumentNullException.ThrowIfNull(partTypes);
        return indexSet.Index(name) is LibraDexRoutedCompositeIndex index
            ? Validate(index, identityType, partTypes)
        : throw new InvalidOperationException($"Index '{name}' in index set '{indexSet.Name}' is not a routed composite index.");
    }

    private static LibraDexRoutedCompositeIndex Validate(
        LibraDexRoutedCompositeIndex index,
        Type identityType,
        Type[] partTypes)
    {
        index.ValidateTypedCompositeShape(identityType, partTypes);
        return index;
    }
}
