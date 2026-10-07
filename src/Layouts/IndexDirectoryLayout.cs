using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Text;

namespace LibraDex.Layouts;

/// <summary>
/// Defines the fixed-size index directory layout used by the initial `.lbdx` superblock model.<br/>
/// The directory is a fixed set of slots and variable-length metadata lives elsewhere by offset.<br/>
/// </summary>
internal static class IndexDirectoryLayout
{
    public const int SlotCount = 64;
    public const int SlotSize = 128;
    public const int Size = SlotCount * SlotSize;

    public const byte EmptyState = 0;
    public const byte ActiveState = 1;

    public const int StateOffset = 0;
    public const int FlagsOffset = 1;
    public const int RootRouterOffsetOffset = 8;
    public const int MetadataOffsetOffset = 16;
    public const int ItemCountOffset = 24;
    public const int GenerationOffset = 32;
    public const int KeyProfileIdOffset = 40;
    public const int IdentityProfileIdOffset = 42;
    public const int RouterProfileIdOffset = 44;
    public const int AllocationClassIdOffset = 46;
    public const int NameOffset = 48;
    public const int NameByteLength = 80;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ReadOnlySpan<byte> GetSlot(ReadOnlySpan<byte> source, int slotIndex)
    {
        return source.Slice(GetSlotOffset(slotIndex), SlotSize);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Span<byte> GetSlot(Span<byte> target, int slotIndex)
    {
        return target.Slice(GetSlotOffset(slotIndex), SlotSize);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetSlotOffset(int slotIndex)
    {
        return checked(slotIndex * SlotSize);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static byte ReadState(ReadOnlySpan<byte> slot)
    {
        return slot[StateOffset];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteState(Span<byte> slot, byte value)
    {
        slot[StateOffset] = value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static byte ReadFlags(ReadOnlySpan<byte> slot)
    {
        return slot[FlagsOffset];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteFlags(Span<byte> slot, byte value)
    {
        slot[FlagsOffset] = value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long ReadRootRouterOffset(ReadOnlySpan<byte> slot)
    {
        return BinaryPrimitives.ReadInt64LittleEndian(slot.Slice(RootRouterOffsetOffset, sizeof(long)));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteRootRouterOffset(Span<byte> slot, long value)
    {
        BinaryPrimitives.WriteInt64LittleEndian(slot.Slice(RootRouterOffsetOffset, sizeof(long)), value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long ReadMetadataOffset(ReadOnlySpan<byte> slot)
    {
        return BinaryPrimitives.ReadInt64LittleEndian(slot.Slice(MetadataOffsetOffset, sizeof(long)));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteMetadataOffset(Span<byte> slot, long value)
    {
        BinaryPrimitives.WriteInt64LittleEndian(slot.Slice(MetadataOffsetOffset, sizeof(long)), value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long ReadItemCount(ReadOnlySpan<byte> slot)
    {
        return BinaryPrimitives.ReadInt64LittleEndian(slot.Slice(ItemCountOffset, sizeof(long)));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteItemCount(Span<byte> slot, long value)
    {
        BinaryPrimitives.WriteInt64LittleEndian(slot.Slice(ItemCountOffset, sizeof(long)), value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long ReadGeneration(ReadOnlySpan<byte> slot)
    {
        return BinaryPrimitives.ReadInt64LittleEndian(slot.Slice(GenerationOffset, sizeof(long)));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteGeneration(Span<byte> slot, long value)
    {
        BinaryPrimitives.WriteInt64LittleEndian(slot.Slice(GenerationOffset, sizeof(long)), value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ushort ReadKeyProfileId(ReadOnlySpan<byte> slot)
    {
        return BinaryPrimitives.ReadUInt16LittleEndian(slot.Slice(KeyProfileIdOffset, sizeof(ushort)));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteKeyProfileId(Span<byte> slot, ushort value)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(slot.Slice(KeyProfileIdOffset, sizeof(ushort)), value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ushort ReadIdentityProfileId(ReadOnlySpan<byte> slot)
    {
        return BinaryPrimitives.ReadUInt16LittleEndian(slot.Slice(IdentityProfileIdOffset, sizeof(ushort)));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteIdentityProfileId(Span<byte> slot, ushort value)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(slot.Slice(IdentityProfileIdOffset, sizeof(ushort)), value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ushort ReadRouterProfileId(ReadOnlySpan<byte> slot)
    {
        return BinaryPrimitives.ReadUInt16LittleEndian(slot.Slice(RouterProfileIdOffset, sizeof(ushort)));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteRouterProfileId(Span<byte> slot, ushort value)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(slot.Slice(RouterProfileIdOffset, sizeof(ushort)), value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ushort ReadAllocationClassId(ReadOnlySpan<byte> slot)
    {
        return BinaryPrimitives.ReadUInt16LittleEndian(slot.Slice(AllocationClassIdOffset, sizeof(ushort)));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteAllocationClassId(Span<byte> slot, ushort value)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(slot.Slice(AllocationClassIdOffset, sizeof(ushort)), value);
    }

    public static string ReadName(ReadOnlySpan<byte> slot)
    {
        return ReadFixedUtf8(slot.Slice(NameOffset, NameByteLength));
    }

    public static void WriteName(Span<byte> slot, string? value)
    {
        WriteFixedUtf8(slot.Slice(NameOffset, NameByteLength), value);
    }

    private static string ReadFixedUtf8(ReadOnlySpan<byte> source)
    {
        int length = source.IndexOf((byte)0);
        if (length < 0)
        {
            length = source.Length;
        }

        return length == 0 ? string.Empty : Encoding.UTF8.GetString(source.Slice(0, length));
    }

    private static void WriteFixedUtf8(Span<byte> target, string? value)
    {
        target.Clear();
        if (string.IsNullOrEmpty(value))
        {
            return;
        }

        int byteCount = Encoding.UTF8.GetByteCount(value);
        if (byteCount > target.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(value), value, $"UTF-8 value requires {byteCount} bytes but field allows {target.Length} bytes.");
        }

        Encoding.UTF8.GetBytes(value, target);
    }
}
