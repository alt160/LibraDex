using System.Buffers.Binary;
using System.Text;

namespace LibraDex;

internal static class LibraDexCompositeNodePageCodec
{
    private const uint Magic = 0x4E50434C; // LCPN
    private const ushort Version1 = 1;
    private const ushort Version2 = 2;
    private const int HeaderSize = 22;
    private const int Version2HeaderSize = 26;
    private const int ChildDirectoryEntrySize = 16;

    /// <summary>
    /// Encodes one routed composite node page.<br/>
    /// The page stores one tier node, terminal identities for full composite paths, and child component values paired with child page offsets.<br/>
    /// </summary>
    /// <param name="shape">The logical composite shape that supplies part and identity types.</param>
    /// <param name="tier">The composite tier represented by this node.</param>
    /// <param name="identities">Terminal identities attached to this node.</param>
    /// <param name="children">Child component values and durable child page offsets.</param>
    /// <returns>The encoded node page bytes.</returns>
    internal static byte[] Encode(
        LibraDexIndexShapeSpec shape,
        int tier,
        IReadOnlyList<object> identities,
        IReadOnlyList<LibraDexCompositeNodeChildPage> children)
    {
        ArgumentNullException.ThrowIfNull(shape);
        ArgumentNullException.ThrowIfNull(identities);
        ArgumentNullException.ThrowIfNull(children);
        if (tier < 0 || tier > shape.CompositeParts.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(tier), tier, "Composite node tier is outside the logical shape depth.");
        }

        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream, Encoding.UTF8, leaveOpen: true);
        writer.Write(Magic);
        writer.Write(Version2);
        writer.Write(0);
        writer.Write(tier);
        writer.Write(checked((int)identities.Count));
        writer.Write(checked((int)children.Count));
        writer.Write(0);
        for (int i = 0; i < identities.Count; i++)
        {
            LibraDexCompositeSnapshotCodec.WriteValue(writer, shape.IdentityType, identities[i]);
        }

        int childDirectoryOffset = checked((int)stream.Position);
        for (int i = 0; i < children.Count; i++)
        {
            writer.Write(0);
            writer.Write(0);
            writer.Write(children[i].Offset);
        }

        int[] valueOffsets = new int[children.Count];
        int[] valueLengths = new int[children.Count];
        for (int i = 0; i < children.Count; i++)
        {
            if (tier >= shape.CompositeParts.Count)
            {
                throw new InvalidDataException("Terminal composite nodes cannot encode child routes.");
            }

            LibraDexCompositeNodeChildPage child = children[i];
            valueOffsets[i] = checked((int)stream.Position);
            LibraDexCompositeKeyValueSemantics.WritePartValue(writer, shape.CompositeParts[tier], child.Value);
            valueLengths[i] = checked((int)stream.Position - valueOffsets[i]);
        }

        writer.Flush();
        byte[] bytes = stream.ToArray();
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(6, 4), bytes.Length);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(22, 4), childDirectoryOffset);
        for (int i = 0; i < children.Count; i++)
        {
            int entryOffset = childDirectoryOffset + (i * ChildDirectoryEntrySize);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(entryOffset, 4), valueOffsets[i]);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(entryOffset + 4, 4), valueLengths[i]);
        }

        return bytes;
    }

    /// <summary>
    /// Attempts to read a composite node page length from a fixed-size header.<br/>
    /// </summary>
    /// <param name="header">The candidate node page header.</param>
    /// <param name="length">Receives the total encoded page length when valid.</param>
    /// <returns><see langword="true"/> when the header describes a supported composite node page; otherwise <see langword="false"/>.</returns>
    internal static bool TryReadLength(ReadOnlySpan<byte> header, out int length)
    {
        length = 0;
        if (header.Length < HeaderSize ||
            BinaryPrimitives.ReadUInt32LittleEndian(header[..4]) != Magic)
        {
            return false;
        }

        ushort version = BinaryPrimitives.ReadUInt16LittleEndian(header.Slice(4, 2));
        if (version != Version1 && version != Version2)
        {
            return false;
        }

        length = BinaryPrimitives.ReadInt32LittleEndian(header.Slice(6, 4));
        return length >= HeaderSize;
    }

    /// <summary>
    /// Decodes one routed composite node page.<br/>
    /// </summary>
    /// <param name="shape">The logical composite shape that supplies part and identity types.</param>
    /// <param name="source">The encoded node page bytes.</param>
    /// <returns>The decoded node page.</returns>
    internal static LibraDexCompositeNodePage Decode(LibraDexIndexShapeSpec shape, ReadOnlySpan<byte> source)
    {
        ArgumentNullException.ThrowIfNull(shape);
        if (source.Length < HeaderSize ||
            BinaryPrimitives.ReadUInt32LittleEndian(source[..4]) != Magic)
        {
            throw new InvalidDataException("Composite node page does not contain the expected magic value.");
        }

        ushort version = BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(4, 2));
        if (version != Version1 && version != Version2)
        {
            throw new InvalidDataException($"Composite node page version {version} is not supported.");
        }

        int length = BinaryPrimitives.ReadInt32LittleEndian(source.Slice(6, 4));
        if (length != source.Length)
        {
            throw new InvalidDataException("Composite node page length does not match the supplied bytes.");
        }

        int tier = BinaryPrimitives.ReadInt32LittleEndian(source.Slice(10, 4));
        if (tier < 0 || tier > shape.CompositeParts.Count)
        {
            throw new InvalidDataException("Composite node page tier is outside the logical shape depth.");
        }

        int identityCount = BinaryPrimitives.ReadInt32LittleEndian(source.Slice(14, 4));
        int childCount = BinaryPrimitives.ReadInt32LittleEndian(source.Slice(18, 4));
        if (identityCount < 0 || childCount < 0)
        {
            throw new InvalidDataException("Composite node page contains a negative count.");
        }

        if (identityCount > 0 && tier != shape.CompositeParts.Count)
        {
            throw new InvalidDataException("Composite node page contains identities before the terminal tier.");
        }

        if (childCount > 0 && tier >= shape.CompositeParts.Count)
        {
            throw new InvalidDataException("Terminal composite node page contains child routes.");
        }

        using MemoryStream stream = new(source.ToArray());
        using BinaryReader reader = new(stream, Encoding.UTF8, leaveOpen: false);
        stream.Position = version == Version1 ? HeaderSize : Version2HeaderSize;
        object[] identities = new object[identityCount];
        for (int i = 0; i < identities.Length; i++)
        {
            identities[i] = LibraDexCompositeSnapshotCodec.ReadValue(reader, shape.IdentityType) ??
                throw new InvalidDataException("Composite node page identity cannot be null.");
        }

        LibraDexCompositeNodeChildPage[] children = new LibraDexCompositeNodeChildPage[childCount];
        if (version == Version1)
        {
            for (int i = 0; i < children.Length; i++)
            {
                object value = LibraDexCompositeKeyValueSemantics.ReadPartValue(reader, shape.CompositeParts[tier]);
                long offset = reader.ReadInt64();
                children[i] = new LibraDexCompositeNodeChildPage(value, offset);
            }

            return new LibraDexCompositeNodePage(tier, identities, children);
        }

        int childDirectoryOffset = BinaryPrimitives.ReadInt32LittleEndian(source.Slice(22, 4));
        int childDirectoryLength = checked(childCount * ChildDirectoryEntrySize);
        if (childDirectoryOffset < Version2HeaderSize ||
            childDirectoryOffset > source.Length ||
            childDirectoryLength > source.Length - childDirectoryOffset)
        {
            throw new InvalidDataException("Composite node page child directory is outside the page body.");
        }

        for (int i = 0; i < children.Length; i++)
        {
            ReadOnlySpan<byte> entry = source.Slice(childDirectoryOffset + (i * ChildDirectoryEntrySize), ChildDirectoryEntrySize);
            int valueOffset = BinaryPrimitives.ReadInt32LittleEndian(entry[..4]);
            int valueLength = BinaryPrimitives.ReadInt32LittleEndian(entry.Slice(4, 4));
            long offset = BinaryPrimitives.ReadInt64LittleEndian(entry.Slice(8, 8));
            if (valueOffset < 0 || valueLength <= 0 || valueOffset > source.Length || valueLength > source.Length - valueOffset)
            {
                throw new InvalidDataException("Composite node page child value is outside the page body.");
            }

            using MemoryStream valueStream = new(source.Slice(valueOffset, valueLength).ToArray());
            using BinaryReader valueReader = new(valueStream, Encoding.UTF8, leaveOpen: false);
            object value = LibraDexCompositeKeyValueSemantics.ReadPartValue(valueReader, shape.CompositeParts[tier]);
            children[i] = new LibraDexCompositeNodeChildPage(value, offset);
        }

        return new LibraDexCompositeNodePage(tier, identities, children);
    }

    /// <summary>
    /// Attempts to find a child page offset directly from a versioned durable node page.<br/>
    /// Version 2 pages keep child value bodies behind a fixed-width child directory, which allows exact traversal to binary-search the page without hydrating every sibling child into the runtime tree.<br/>
    /// </summary>
    /// <param name="shape">The logical composite shape that supplies part types.</param>
    /// <param name="source">The encoded node page bytes.</param>
    /// <param name="tier">The expected composite tier for the node page.</param>
    /// <param name="value">The child component value to locate.</param>
    /// <param name="offset">Receives the durable child page offset when found.</param>
    /// <returns><see langword="true"/> when a matching child route exists; otherwise <see langword="false"/>.</returns>
    internal static bool TryFindChildOffset(
        LibraDexIndexShapeSpec shape,
        ReadOnlySpan<byte> source,
        int tier,
        object value,
        out long offset)
    {
        ArgumentNullException.ThrowIfNull(shape);
        ArgumentNullException.ThrowIfNull(value);
        offset = 0;
        if (source.Length < Version2HeaderSize ||
            BinaryPrimitives.ReadUInt32LittleEndian(source[..4]) != Magic ||
            BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(4, 2)) != Version2)
        {
            return false;
        }

        int length = BinaryPrimitives.ReadInt32LittleEndian(source.Slice(6, 4));
        int pageTier = BinaryPrimitives.ReadInt32LittleEndian(source.Slice(10, 4));
        int childCount = BinaryPrimitives.ReadInt32LittleEndian(source.Slice(18, 4));
        int childDirectoryOffset = BinaryPrimitives.ReadInt32LittleEndian(source.Slice(22, 4));
        int childDirectoryLength = checked(childCount * ChildDirectoryEntrySize);
        if (length != source.Length ||
            pageTier != tier ||
            tier < 0 ||
            tier >= shape.CompositeParts.Count ||
            childCount < 0 ||
            childDirectoryOffset < Version2HeaderSize ||
            childDirectoryOffset > source.Length ||
            childDirectoryLength > source.Length - childDirectoryOffset)
        {
            return false;
        }

        LibraDexCompositeKeyPartSpec part = shape.CompositeParts[tier];
        int low = 0;
        int high = childCount - 1;
        while (low <= high)
        {
            int mid = low + ((high - low) / 2);
            ReadOnlySpan<byte> entry = source.Slice(childDirectoryOffset + (mid * ChildDirectoryEntrySize), ChildDirectoryEntrySize);
            int valueOffset = BinaryPrimitives.ReadInt32LittleEndian(entry[..4]);
            int valueLength = BinaryPrimitives.ReadInt32LittleEndian(entry.Slice(4, 4));
            if (valueOffset < 0 || valueLength <= 0 || valueOffset > source.Length || valueLength > source.Length - valueOffset)
            {
                return false;
            }

            using MemoryStream valueStream = new(source.Slice(valueOffset, valueLength).ToArray());
            using BinaryReader valueReader = new(valueStream, Encoding.UTF8, leaveOpen: false);
            object midValue = LibraDexCompositeKeyValueSemantics.ReadPartValue(valueReader, part);
            int comparison = LibraDexCompositeKeyValueSemantics.ComparePartValues(part, midValue, value);
            if (comparison == 0)
            {
                offset = BinaryPrimitives.ReadInt64LittleEndian(entry.Slice(8, 8));
                return true;
            }

            if (comparison < 0)
            {
                low = mid + 1;
            }
            else
            {
                high = mid - 1;
            }
        }

        return false;
    }

    /// <summary>
    /// Attempts to enumerate child routes from a version 2 durable node page within inclusive component-value bounds.<br/>
    /// The child directory is ordered by the encoded component values, so this method binary-locates the first possible child and stops as soon as decoded values move beyond the upper bound.<br/>
    /// </summary>
    /// <param name="shape">The logical composite shape that supplies part types.</param>
    /// <param name="source">The encoded node page bytes.</param>
    /// <param name="tier">The expected composite tier for the node page.</param>
    /// <param name="lower">The inclusive lower component value.</param>
    /// <param name="upper">The inclusive upper component value.</param>
    /// <param name="children">Receives matching child route values and durable child page offsets.</param>
    /// <returns><see langword="true"/> when the page supports directory range enumeration; otherwise <see langword="false"/>.</returns>
    internal static bool TryEnumerateChildRange(
        LibraDexIndexShapeSpec shape,
        ReadOnlySpan<byte> source,
        int tier,
        object lower,
        object upper,
        out IReadOnlyList<LibraDexCompositeNodeChildPage> children)
    {
        ArgumentNullException.ThrowIfNull(shape);
        ArgumentNullException.ThrowIfNull(lower);
        ArgumentNullException.ThrowIfNull(upper);
        children = Array.Empty<LibraDexCompositeNodeChildPage>();
        if (!TryReadVersion2Directory(shape, source, tier, out int childCount, out int childDirectoryOffset))
        {
            return false;
        }

        LibraDexCompositeKeyPartSpec part = shape.CompositeParts[tier];
        if (LibraDexCompositeKeyValueSemantics.ComparePartValues(part, lower, upper) > 0)
        {
            return true;
        }

        int first = LowerBound(source, childDirectoryOffset, childCount, part, lower);
        if (first < 0)
        {
            return false;
        }

        List<LibraDexCompositeNodeChildPage> matches = new();
        for (int i = first; i < childCount; i++)
        {
            if (!TryReadChildEntry(source, childDirectoryOffset, i, part, out object value, out long offset))
            {
                return false;
            }

            if (LibraDexCompositeKeyValueSemantics.ComparePartValues(part, value, upper) > 0)
            {
                break;
            }

            matches.Add(new LibraDexCompositeNodeChildPage(value, offset));
        }

        children = matches;
        return true;
    }

    /// <summary>
    /// Attempts to enumerate string child routes from a version 2 durable node page that share an ordinal prefix.<br/>
    /// The method lower-bounds to the prefix and stops when the ordered child directory leaves the contiguous prefix region.<br/>
    /// </summary>
    /// <param name="shape">The logical composite shape that supplies part types.</param>
    /// <param name="source">The encoded node page bytes.</param>
    /// <param name="tier">The expected composite tier for the node page.</param>
    /// <param name="prefix">The ordinal string prefix to match.</param>
    /// <param name="children">Receives matching child route values and durable child page offsets.</param>
    /// <returns><see langword="true"/> when the page supports directory prefix enumeration; otherwise <see langword="false"/>.</returns>
    internal static bool TryEnumerateChildPrefix(
        LibraDexIndexShapeSpec shape,
        ReadOnlySpan<byte> source,
        int tier,
        string prefix,
        out IReadOnlyList<LibraDexCompositeNodeChildPage> children)
    {
        ArgumentNullException.ThrowIfNull(shape);
        ArgumentNullException.ThrowIfNull(prefix);
        children = Array.Empty<LibraDexCompositeNodeChildPage>();
        if (!TryReadVersion2Directory(shape, source, tier, out int childCount, out int childDirectoryOffset) ||
            shape.CompositeParts[tier].KeyType != typeof(string))
        {
            return false;
        }

        LibraDexCompositeKeyPartSpec part = shape.CompositeParts[tier];
        int first = LowerBound(source, childDirectoryOffset, childCount, part, prefix);
        if (first < 0)
        {
            return false;
        }

        List<LibraDexCompositeNodeChildPage> matches = new();
        for (int i = first; i < childCount; i++)
        {
            if (!TryReadChildEntry(source, childDirectoryOffset, i, part, out object value, out long offset))
            {
                return false;
            }

            string text = value as string ?? throw new InvalidDataException("Composite node page prefix child value is not a string.");
            if (!text.StartsWith(prefix, StringComparison.Ordinal))
            {
                break;
            }

            matches.Add(new LibraDexCompositeNodeChildPage(text, offset));
        }

        children = matches;
        return true;
    }

    /// <summary>
    /// Attempts to enumerate child routes from a version 2 durable node page using one inclusive or exclusive component bound.<br/>
    /// Lower-bound predicates binary-locate the first candidate; upper-bound predicates scan from the beginning and stop as soon as the ordered child directory exceeds the upper bound.<br/>
    /// </summary>
    /// <param name="shape">The logical composite shape that supplies part types.</param>
    /// <param name="source">The encoded node page bytes.</param>
    /// <param name="tier">The expected composite tier for the node page.</param>
    /// <param name="bound">The component bound value.</param>
    /// <param name="isLowerBound">Whether the bound is a lower bound; otherwise it is an upper bound.</param>
    /// <param name="inclusive">Whether the bound comparison is inclusive.</param>
    /// <param name="children">Receives matching child route values and durable child page offsets.</param>
    /// <returns><see langword="true"/> when the page supports directory bound enumeration; otherwise <see langword="false"/>.</returns>
    internal static bool TryEnumerateChildBound(
        LibraDexIndexShapeSpec shape,
        ReadOnlySpan<byte> source,
        int tier,
        object bound,
        bool isLowerBound,
        bool inclusive,
        out IReadOnlyList<LibraDexCompositeNodeChildPage> children)
    {
        ArgumentNullException.ThrowIfNull(shape);
        ArgumentNullException.ThrowIfNull(bound);
        children = Array.Empty<LibraDexCompositeNodeChildPage>();
        if (!TryReadVersion2Directory(shape, source, tier, out int childCount, out int childDirectoryOffset))
        {
            return false;
        }

        LibraDexCompositeKeyPartSpec part = shape.CompositeParts[tier];
        List<LibraDexCompositeNodeChildPage> matches = new();
        int start = 0;
        if (isLowerBound)
        {
            start = LowerBound(source, childDirectoryOffset, childCount, part, bound);
            if (start < 0)
            {
                return false;
            }
        }

        for (int i = start; i < childCount; i++)
        {
            if (!TryReadChildEntry(source, childDirectoryOffset, i, part, out object value, out long offset))
            {
                return false;
            }

            int comparison = LibraDexCompositeKeyValueSemantics.ComparePartValues(part, value, bound);
            if (isLowerBound)
            {
                if (!inclusive && comparison == 0)
                {
                    continue;
                }

                matches.Add(new LibraDexCompositeNodeChildPage(value, offset));
                continue;
            }

            if (comparison > 0 || (!inclusive && comparison == 0))
            {
                break;
            }

            matches.Add(new LibraDexCompositeNodeChildPage(value, offset));
        }

        children = matches;
        return true;
    }

    private static bool TryReadVersion2Directory(
        LibraDexIndexShapeSpec shape,
        ReadOnlySpan<byte> source,
        int tier,
        out int childCount,
        out int childDirectoryOffset)
    {
        childCount = 0;
        childDirectoryOffset = 0;
        if (source.Length < Version2HeaderSize ||
            BinaryPrimitives.ReadUInt32LittleEndian(source[..4]) != Magic ||
            BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(4, 2)) != Version2)
        {
            return false;
        }

        int length = BinaryPrimitives.ReadInt32LittleEndian(source.Slice(6, 4));
        int pageTier = BinaryPrimitives.ReadInt32LittleEndian(source.Slice(10, 4));
        childCount = BinaryPrimitives.ReadInt32LittleEndian(source.Slice(18, 4));
        childDirectoryOffset = BinaryPrimitives.ReadInt32LittleEndian(source.Slice(22, 4));
        int childDirectoryLength = checked(childCount * ChildDirectoryEntrySize);
        return length == source.Length &&
            pageTier == tier &&
            tier >= 0 &&
            tier < shape.CompositeParts.Count &&
            childCount >= 0 &&
            childDirectoryOffset >= Version2HeaderSize &&
            childDirectoryOffset <= source.Length &&
            childDirectoryLength <= source.Length - childDirectoryOffset;
    }

    private static int LowerBound(
        ReadOnlySpan<byte> source,
        int childDirectoryOffset,
        int childCount,
        LibraDexCompositeKeyPartSpec part,
        object lower)
    {
        int low = 0;
        int high = childCount;
        while (low < high)
        {
            int mid = low + ((high - low) / 2);
            if (!TryReadChildEntry(source, childDirectoryOffset, mid, part, out object value, out _))
            {
                return -1;
            }

            if (LibraDexCompositeKeyValueSemantics.ComparePartValues(part, value, lower) < 0)
            {
                low = mid + 1;
            }
            else
            {
                high = mid;
            }
        }

        return low;
    }

    private static bool TryReadChildEntry(
        ReadOnlySpan<byte> source,
        int childDirectoryOffset,
        int index,
        LibraDexCompositeKeyPartSpec part,
        out object value,
        out long offset)
    {
        value = null!;
        offset = 0;
        ReadOnlySpan<byte> entry = source.Slice(childDirectoryOffset + (index * ChildDirectoryEntrySize), ChildDirectoryEntrySize);
        int valueOffset = BinaryPrimitives.ReadInt32LittleEndian(entry[..4]);
        int valueLength = BinaryPrimitives.ReadInt32LittleEndian(entry.Slice(4, 4));
        if (valueOffset < 0 || valueLength <= 0 || valueOffset > source.Length || valueLength > source.Length - valueOffset)
        {
            return false;
        }

        using MemoryStream valueStream = new(source.Slice(valueOffset, valueLength).ToArray());
        using BinaryReader valueReader = new(valueStream, Encoding.UTF8, leaveOpen: false);
        value = LibraDexCompositeKeyValueSemantics.ReadPartValue(valueReader, part);
        offset = BinaryPrimitives.ReadInt64LittleEndian(entry.Slice(8, 8));
        return true;
    }
}

internal readonly record struct LibraDexCompositeNodeChildPage(object Value, long Offset);

internal readonly record struct LibraDexCompositeNodePage(
    int Tier,
    IReadOnlyList<object> Identities,
    IReadOnlyList<LibraDexCompositeNodeChildPage> Children);
