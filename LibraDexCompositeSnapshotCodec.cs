using System.Buffers.Binary;
using System.Text;

namespace LibraDex;

internal static class LibraDexCompositeSnapshotCodec
{
    private const uint Magic = 0x5350434C; // LCPS
    private const ushort Version3 = 3;
    private const int HeaderSize = 16;

    /// <summary>
    /// Encodes composite entries into a routed node-record durable snapshot format.<br/>
    /// Repeated leading component values are represented once per node record, and child links are explicit so the byte format is one step closer to localized tier-node pages.<br/>
    /// </summary>
    /// <param name="shape">The logical composite shape that supplies part and identity types.</param>
    /// <param name="entries">The composite entries to encode.</param>
    /// <returns>An encoded snapshot byte array.</returns>
    internal static byte[] Encode(LibraDexIndexShapeSpec shape, IReadOnlyList<LibraDexCompositeEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(shape);
        ArgumentNullException.ThrowIfNull(entries);
        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream, Encoding.UTF8, leaveOpen: true);
        writer.Write(Magic);
        writer.Write(Version3);
        writer.Write(0);
        writer.Write(checked((ushort)shape.CompositeParts.Count));
        writer.Write(checked((int)entries.Count));
        SnapshotNode root = BuildTree(shape, entries);
        List<SnapshotNode> nodes = FlattenNodes(root);
        writer.Write(checked((int)nodes.Count));
        for (int i = 0; i < nodes.Count; i++)
        {
            WriteNodeRecord(writer, shape, nodes[i]);
        }

        writer.Flush();
        byte[] bytes = stream.ToArray();
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(6, 4), bytes.Length);
        return bytes;
    }

    /// <summary>
    /// Attempts to read the total snapshot length from a fixed-size snapshot header.<br/>
    /// Returning false lets callers distinguish older metadata-only root offsets or other page types from composite snapshot bytes.<br/>
    /// </summary>
    /// <param name="header">The candidate snapshot header bytes.</param>
    /// <param name="length">Receives the total encoded snapshot length when the header is valid.</param>
    /// <returns><see langword="true"/> when the header is a supported composite snapshot header; otherwise <see langword="false"/>.</returns>
    internal static bool TryReadLength(ReadOnlySpan<byte> header, out int length)
    {
        length = 0;
        if (header.Length < HeaderSize ||
            BinaryPrimitives.ReadUInt32LittleEndian(header[..4]) != Magic)
        {
            return false;
        }

        ushort version = BinaryPrimitives.ReadUInt16LittleEndian(header.Slice(4, 2));
        if (version != Version3)
        {
            return false;
        }

        length = BinaryPrimitives.ReadInt32LittleEndian(header.Slice(6, 4));
        return length >= HeaderSize;
    }

    /// <summary>
    /// Decodes composite entries from the routed node-record durable snapshot format.<br/>
    /// Decoding validates the stored part count against the logical shape, reads explicit node records, then walks node links to rebuild explicit composite-key containers.<br/>
    /// </summary>
    /// <param name="shape">The logical composite shape that supplies part and identity types.</param>
    /// <param name="source">The encoded snapshot bytes.</param>
    /// <returns>The decoded composite entries.</returns>
    internal static IReadOnlyList<LibraDexCompositeEntry> Decode(LibraDexIndexShapeSpec shape, ReadOnlySpan<byte> source)
    {
        ArgumentNullException.ThrowIfNull(shape);
        if (source.Length < HeaderSize ||
            BinaryPrimitives.ReadUInt32LittleEndian(source[..4]) != Magic)
        {
            throw new InvalidDataException("Composite snapshot does not contain the expected magic value.");
        }

        ushort version = BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(4, 2));
        if (version != Version3)
        {
            throw new InvalidDataException($"Composite snapshot version {version} is not supported.");
        }

        int length = BinaryPrimitives.ReadInt32LittleEndian(source.Slice(6, 4));
        if (length != source.Length)
        {
            throw new InvalidDataException("Composite snapshot length does not match the supplied bytes.");
        }

        int partCount = BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(10, 2));
        if (partCount != shape.CompositeParts.Count)
        {
            throw new InvalidDataException("Composite snapshot part count does not match the logical shape.");
        }

        int entryCount = BinaryPrimitives.ReadInt32LittleEndian(source.Slice(12, 4));
        using MemoryStream stream = new(source.ToArray());
        using BinaryReader reader = new(stream, Encoding.UTF8, leaveOpen: false);
        _ = reader.ReadUInt32();
        _ = reader.ReadUInt16();
        _ = reader.ReadInt32();
        _ = reader.ReadUInt16();
        _ = reader.ReadInt32();
        int nodeCount = reader.ReadInt32();
        if (nodeCount <= 0)
        {
            throw new InvalidDataException("Composite snapshot must contain at least the root node.");
        }

        SnapshotRecord[] records = new SnapshotRecord[nodeCount];
        for (int i = 0; i < records.Length; i++)
        {
            records[i] = ReadNodeRecord(reader, shape);
        }

        List<LibraDexCompositeEntry> entries = new(entryCount);
        object?[] values = new object?[shape.CompositeParts.Count];
        ReadNode(records, nodeId: 0, shape, values, entries);
        if (entries.Count != entryCount)
        {
            throw new InvalidDataException("Composite snapshot entry count does not match the decoded routed tree.");
        }

        return entries;
    }

    /// <summary>
    /// Builds an in-memory routed tree from flat composite entries before encoding.<br/>
    /// This keeps the durable snapshot aligned with component routing and avoids duplicating repeated leading values in the persisted body.<br/>
    /// </summary>
    /// <param name="shape">The logical composite shape that supplies part count and ordering.</param>
    /// <param name="entries">The flat entry list produced by the current routed composite tree.</param>
    /// <returns>The root snapshot node.</returns>
    private static SnapshotNode BuildTree(LibraDexIndexShapeSpec shape, IReadOnlyList<LibraDexCompositeEntry> entries)
    {
        SnapshotNode root = new(value: null);
        for (int i = 0; i < entries.Count; i++)
        {
            LibraDexCompositeEntry entry = entries[i];
            entry.Key.ValidateAgainst(shape);
            SnapshotNode node = root;
            for (int partIndex = 0; partIndex < shape.CompositeParts.Count; partIndex++)
            {
                object partValue = entry.Key.Values[partIndex].Value ?? throw new InvalidDataException("Composite snapshot part values cannot be null.");
                node = node.GetOrAdd(partValue);
            }

            node.Identities.Add(entry.Identity);
        }

        root.SortRecursively();
        return root;
    }

    /// <summary>
    /// Flattens a routed snapshot tree into preorder node records.<br/>
    /// The node identifiers assigned here become child-link targets in the encoded snapshot, giving the durable body an explicit node-addressable shape.<br/>
    /// </summary>
    /// <param name="root">The root routed snapshot node.</param>
    /// <returns>The flattened node list.</returns>
    private static List<SnapshotNode> FlattenNodes(SnapshotNode root)
    {
        List<SnapshotNode> nodes = new();
        AddNode(root, nodes);
        return nodes;
    }

    /// <summary>
    /// Adds one routed snapshot node and descendants to the flattened preorder list.<br/>
    /// </summary>
    /// <param name="node">The node to add.</param>
    /// <param name="nodes">The flattened node list under construction.</param>
    private static void AddNode(SnapshotNode node, List<SnapshotNode> nodes)
    {
        node.Id = nodes.Count;
        nodes.Add(node);
        for (int i = 0; i < node.Children.Count; i++)
        {
            AddNode(node.Children[i], nodes);
        }
    }

    /// <summary>
    /// Writes one routed node record.<br/>
    /// Each record stores its tier, optional component value, terminal identities, and child node identifiers; this is still snapshot-based but uses the same core unit needed by later localized node-page updates.<br/>
    /// </summary>
    /// <param name="writer">The binary writer receiving the node record.</param>
    /// <param name="shape">The logical composite shape that supplies value types.</param>
    /// <param name="node">The node to write.</param>
    private static void WriteNodeRecord(BinaryWriter writer, LibraDexIndexShapeSpec shape, SnapshotNode node)
    {
        writer.Write(node.Id);
        writer.Write(node.Tier);
        writer.Write(node.Value is not null);
        if (node.Value is not null)
        {
            WriteValue(writer, shape.CompositeParts[node.Tier - 1].KeyType, node.Value);
        }

        writer.Write(checked((int)node.Identities.Count));
        for (int i = 0; i < node.Identities.Count; i++)
        {
            WriteValue(writer, shape.IdentityType, node.Identities[i]);
        }

        writer.Write(checked((int)node.Children.Count));
        for (int i = 0; i < node.Children.Count; i++)
        {
            writer.Write(node.Children[i].Id);
        }
    }

    /// <summary>
    /// Reads one routed node record from the snapshot body.<br/>
    /// The record keeps component value and child links separate, matching the eventual tier-node page model more closely than recursive inline child records.<br/>
    /// </summary>
    /// <param name="reader">The binary reader positioned at the record.</param>
    /// <param name="shape">The logical composite shape that supplies value types.</param>
    /// <returns>The decoded routed node record.</returns>
    private static SnapshotRecord ReadNodeRecord(BinaryReader reader, LibraDexIndexShapeSpec shape)
    {
        int id = reader.ReadInt32();
        int tier = reader.ReadInt32();
        bool hasValue = reader.ReadBoolean();
        if (tier < 0 || tier > shape.CompositeParts.Count)
        {
            throw new InvalidDataException("Composite snapshot node tier is outside the logical shape depth.");
        }

        object? value = hasValue
            ? ReadValue(reader, shape.CompositeParts[tier - 1].KeyType)
            : null;
        if (tier == 0 && value is not null)
        {
            throw new InvalidDataException("Composite snapshot root node cannot have a component value.");
        }

        if (tier > 0 && value is null)
        {
            throw new InvalidDataException("Composite snapshot non-root node requires a component value.");
        }

        int identityCount = reader.ReadInt32();
        if (identityCount < 0)
        {
            throw new InvalidDataException("Composite snapshot contains a negative identity count.");
        }

        if (identityCount > 0 && tier != shape.CompositeParts.Count)
        {
            throw new InvalidDataException("Composite snapshot contains identities before the terminal composite tier.");
        }

        object[] identities = new object[identityCount];
        for (int i = 0; i < identityCount; i++)
        {
            identities[i] = ReadValue(reader, shape.IdentityType) ?? throw new InvalidDataException("Composite snapshot identity cannot be null.");
        }

        int childCount = reader.ReadInt32();
        if (childCount < 0)
        {
            throw new InvalidDataException("Composite snapshot contains a negative child count.");
        }

        if (childCount > 0 && tier >= shape.CompositeParts.Count)
        {
            throw new InvalidDataException("Composite snapshot contains children beyond the shape depth.");
        }

        int[] children = new int[childCount];
        for (int i = 0; i < childCount; i++)
        {
            children[i] = reader.ReadInt32();
        }

        return new SnapshotRecord(id, tier, value, identities, children);
    }

    /// <summary>
    /// Traverses decoded node records and appends terminal composite entries.<br/>
    /// The traversal validates child identifiers and tier transitions so malformed snapshots cannot silently produce incorrect keys.<br/>
    /// </summary>
    /// <param name="records">The decoded node record table.</param>
    /// <param name="nodeId">The current node identifier.</param>
    /// <param name="shape">The logical composite shape.</param>
    /// <param name="values">The reusable composite path buffer.</param>
    /// <param name="entries">The decoded entries accumulated so far.</param>
    private static void ReadNode(SnapshotRecord[] records, int nodeId, LibraDexIndexShapeSpec shape, object?[] values, List<LibraDexCompositeEntry> entries)
    {
        if ((uint)nodeId >= records.Length)
        {
            throw new InvalidDataException("Composite snapshot child link points outside the node table.");
        }

        SnapshotRecord record = records[nodeId];
        if (record.Id != nodeId)
        {
            throw new InvalidDataException("Composite snapshot node identifier does not match its table ordinal.");
        }

        if (record.Tier > 0)
        {
            values[record.Tier - 1] = record.Value;
        }

        for (int i = 0; i < record.Identities.Length; i++)
        {
            entries.Add(new LibraDexCompositeEntry(LibraDexCompositeKey.Of(values.ToArray()), record.Identities[i]));
        }

        for (int i = 0; i < record.Children.Length; i++)
        {
            int childId = record.Children[i];
            if ((uint)childId >= records.Length)
            {
                throw new InvalidDataException("Composite snapshot child link points outside the node table.");
            }

            if (records[childId].Tier != record.Tier + 1)
            {
                throw new InvalidDataException("Composite snapshot child tier does not follow its parent tier.");
            }

            ReadNode(records, childId, shape, values, entries);
        }

        if (record.Tier > 0)
        {
            values[record.Tier - 1] = null;
        }
    }

    /// <summary>
    /// Writes one supported composite part or identity value using the CLR type recorded in the shape.<br/>
    /// This avoids runtime formatter serialization and keeps the snapshot constrained to LibraDex-supported round-trippable scalar, GUID, string, and date/time values.<br/>
    /// </summary>
    /// <param name="writer">The binary writer receiving the value.</param>
    /// <param name="type">The expected CLR type.</param>
    /// <param name="value">The non-null runtime value.</param>
    internal static void WriteValue(BinaryWriter writer, Type type, object? value)
    {
        if (value is null)
        {
            throw new InvalidDataException("Composite snapshot values cannot be null.");
        }

        if (type == typeof(string))
        {
            writer.Write((string)value);
        }
        else if (type == typeof(Guid))
        {
            writer.Write(((Guid)value).ToByteArray());
        }
        else if (type == typeof(long))
        {
            writer.Write((long)value);
        }
        else if (type == typeof(int))
        {
            writer.Write((int)value);
        }
        else if (type == typeof(short))
        {
            writer.Write((short)value);
        }
        else if (type == typeof(ushort))
        {
            writer.Write((ushort)value);
        }
        else if (type == typeof(byte))
        {
            writer.Write((byte)value);
        }
        else if (type == typeof(sbyte))
        {
            writer.Write((sbyte)value);
        }
        else if (type == typeof(uint))
        {
            writer.Write((uint)value);
        }
        else if (type == typeof(ulong))
        {
            writer.Write((ulong)value);
        }
        else if (type == typeof(bool))
        {
            writer.Write((bool)value);
        }
        else if (type == typeof(char))
        {
            writer.Write((char)value);
        }
        else if (type == typeof(DateTime))
        {
            DateTime dateTime = (DateTime)value;
            writer.Write(dateTime.Ticks);
            writer.Write((byte)dateTime.Kind);
        }
        else if (type == typeof(DateTimeOffset))
        {
            DateTimeOffset valueOffset = (DateTimeOffset)value;
            writer.Write(valueOffset.Ticks);
            writer.Write(valueOffset.Offset.Ticks);
        }
        else if (type == typeof(DateOnly))
        {
            writer.Write(((DateOnly)value).DayNumber);
        }
        else if (type == typeof(TimeOnly))
        {
            writer.Write(((TimeOnly)value).Ticks);
        }
        else if (type == typeof(TimeSpan))
        {
            writer.Write(((TimeSpan)value).Ticks);
        }
        else
        {
            throw new NotSupportedException($"Composite snapshot value type {type.FullName} is not supported.");
        }
    }

    /// <summary>
    /// Reads one supported composite part or identity value using the CLR type recorded in the shape.<br/>
    /// The supported type set mirrors <see cref="WriteValue(BinaryWriter, Type, object?)"/> so durable snapshots round-trip without general object serialization.<br/>
    /// </summary>
    /// <param name="reader">The binary reader positioned at the value.</param>
    /// <param name="type">The expected CLR type.</param>
    /// <returns>The decoded runtime value.</returns>
    internal static object? ReadValue(BinaryReader reader, Type type)
    {
        if (type == typeof(string))
        {
            return reader.ReadString();
        }

        if (type == typeof(Guid))
        {
            return new Guid(reader.ReadBytes(16));
        }

        if (type == typeof(long))
        {
            return reader.ReadInt64();
        }

        if (type == typeof(int))
        {
            return reader.ReadInt32();
        }

        if (type == typeof(short))
        {
            return reader.ReadInt16();
        }

        if (type == typeof(ushort))
        {
            return reader.ReadUInt16();
        }

        if (type == typeof(byte))
        {
            return reader.ReadByte();
        }

        if (type == typeof(sbyte))
        {
            return reader.ReadSByte();
        }

        if (type == typeof(uint))
        {
            return reader.ReadUInt32();
        }

        if (type == typeof(ulong))
        {
            return reader.ReadUInt64();
        }

        if (type == typeof(bool))
        {
            return reader.ReadBoolean();
        }

        if (type == typeof(char))
        {
            return reader.ReadChar();
        }

        if (type == typeof(DateTime))
        {
            long ticks = reader.ReadInt64();
            DateTimeKind kind = (DateTimeKind)reader.ReadByte();
            return new DateTime(ticks, kind);
        }

        if (type == typeof(DateTimeOffset))
        {
            long ticks = reader.ReadInt64();
            TimeSpan offset = TimeSpan.FromTicks(reader.ReadInt64());
            return new DateTimeOffset(ticks, offset);
        }

        if (type == typeof(DateOnly))
        {
            return DateOnly.FromDayNumber(reader.ReadInt32());
        }

        if (type == typeof(TimeOnly))
        {
            return TimeOnly.FromTimeSpan(TimeSpan.FromTicks(reader.ReadInt64()));
        }

        if (type == typeof(TimeSpan))
        {
            return TimeSpan.FromTicks(reader.ReadInt64());
        }

        throw new NotSupportedException($"Composite snapshot value type {type.FullName} is not supported.");
    }

    /// <summary>
    /// Represents one node in the transient tree used to encode routed composite snapshots.<br/>
    /// The class exists only during encode; persisted bytes store the same child/identity shape without retaining this object graph.<br/>
    /// </summary>
    private sealed class SnapshotNode
    {
        /// <summary>
        /// Creates one transient routed snapshot node.<br/>
        /// Root nodes use a null value; child nodes store the component value selected by their parent tier.<br/>
        /// </summary>
        /// <param name="value">The component value represented by this node, or null for the root.</param>
        internal SnapshotNode(object? value)
            : this(value, tier: 0)
        {
        }

        /// <summary>
        /// Creates one transient routed snapshot node.<br/>
        /// Root nodes use a null value at tier zero; child nodes store the component value selected by their parent tier.<br/>
        /// </summary>
        /// <param name="value">The component value represented by this node, or null for the root.</param>
        /// <param name="tier">The composite tier represented by this node.</param>
        internal SnapshotNode(object? value, int tier)
        {
            Value = value;
            Tier = tier;
        }

        /// <summary>
        /// Gets or sets the flattened node identifier assigned during encode.<br/>
        /// The identifier becomes the child-link target in the durable node-record table.<br/>
        /// </summary>
        internal int Id { get; set; }

        /// <summary>
        /// Gets the component value represented by this node.<br/>
        /// Root nodes have no component value because they are the entry point before tier zero.<br/>
        /// </summary>
        internal object? Value { get; }

        /// <summary>
        /// Gets the composite tier represented by this node.<br/>
        /// The root is tier zero, and a child at tier `n` stores the value for composite part `n - 1`.<br/>
        /// </summary>
        internal int Tier { get; }

        /// <summary>
        /// Gets the identities attached to this exact composite path.<br/>
        /// In the current format only terminal nodes should contain identities, but the collection remains local to the node so uniqueness and duplicate-key semantics are explicit.<br/>
        /// </summary>
        internal List<object> Identities { get; } = new();

        /// <summary>
        /// Gets child component routes below this node.<br/>
        /// Each child corresponds to one distinct value at the next composite tier.<br/>
        /// </summary>
        internal List<SnapshotNode> Children { get; } = new();

        /// <summary>
        /// Gets an existing child route for a component value or adds one when missing.<br/>
        /// This is the transient encode-time equivalent of routing a component value to the next mini-router tier.<br/>
        /// </summary>
        /// <param name="value">The component value to route below this node.</param>
        /// <returns>The existing or newly created child node.</returns>
        internal SnapshotNode GetOrAdd(object value)
        {
            for (int i = 0; i < Children.Count; i++)
            {
                if (Equals(Children[i].Value, value))
                {
                    return Children[i];
                }
            }

            SnapshotNode child = new(value, Tier + 1);
            Children.Add(child);
            return child;
        }

        /// <summary>
        /// Sorts child routes recursively by component value.<br/>
        /// Stable encoded ordering keeps reopen deterministic and matches the in-memory traversal behavior used by condition execution.<br/>
        /// </summary>
        internal void SortRecursively()
        {
            Children.Sort(static (left, right) => CompareValues(left.Value, right.Value));
            for (int i = 0; i < Children.Count; i++)
            {
                Children[i].SortRecursively();
            }
        }

        /// <summary>
        /// Compares two routed component values for deterministic snapshot ordering.<br/>
        /// Supported LibraDex key values normally implement <see cref="IComparable"/>; the string fallback exists only for unusual supported values that do not expose a comparable contract.<br/>
        /// </summary>
        /// <param name="left">The left component value.</param>
        /// <param name="right">The right component value.</param>
        /// <returns>A negative, zero, or positive comparison result.</returns>
        private static int CompareValues(object? left, object? right)
        {
            if (ReferenceEquals(left, right))
            {
                return 0;
            }

            if (left is null)
            {
                return -1;
            }

            if (right is null)
            {
                return 1;
            }

            return left is IComparable comparable
                ? comparable.CompareTo(right)
                : string.CompareOrdinal(left.ToString(), right.ToString());
        }
    }

    /// <summary>
    /// Represents one decoded durable routed node record.<br/>
    /// The record table is the snapshot bridge toward future individually addressable composite mini-router node pages.<br/>
    /// </summary>
    /// <param name="Id">The node identifier, equal to its ordinal in the decoded table.</param>
    /// <param name="Tier">The composite tier represented by the node.</param>
    /// <param name="Value">The component value for non-root nodes, or null for the root.</param>
    /// <param name="Identities">The terminal identities attached to this node.</param>
    /// <param name="Children">The child node identifiers below this node.</param>
    private readonly record struct SnapshotRecord(
        int Id,
        int Tier,
        object? Value,
        object[] Identities,
        int[] Children);
}

internal readonly record struct LibraDexCompositeEntry(LibraDexCompositeKey Key, object Identity);
