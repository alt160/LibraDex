using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace LibraDex.Layouts;

/// <summary>
/// Defines the root page for a null-or-empty key-state identity route.<br/>
/// The route is keyed by encoded identity values rather than by ordinary index keys, so it can start as an inline sorted root and later reshape to ranged or routed identity children without changing catalog metadata.<br/>
/// </summary>
internal static class KeyStateIdentityRouteLayout
{
    public const uint Magic = 0x49524B53U;
    public const ushort FormatVersion = 1;
    public const ushort HeaderSize = 48;
    public const int ExtentSize = 4096;
    public const int Scalar8IdentitySize = sizeof(ulong);
    public const int Scalar16IdentitySize = sizeof(ulong) * 2;

    public const int MagicOffset = 0;
    public const int FormatVersionOffset = 4;
    public const int HeaderSizeOffset = 6;
    public const int StorageKindOffset = 8;
    public const int FlagsOffset = 10;
    public const int ItemCountOffset = 14;
    public const int IdentitySizeOffset = 18;
    public const int ChildRootOffsetOffset = 24;
    public const int Reserved0Offset = 32;
    public const int Reserved1Offset = 40;

    public const ushort InlineSortedStorageKind = 1;
    public const ushort TerminalIdentityRootStorageKind = 2;
    public const ushort Scalar8IdentityCode = 8;
    public const ushort Scalar16IdentityCode = 16;

    public static int MaxScalar8InlineItemCount => (ExtentSize - HeaderSize) / Scalar8IdentitySize;

    public static int MaxScalar16InlineItemCount => (ExtentSize - HeaderSize) / Scalar16IdentitySize;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint ReadMagic(ReadOnlySpan<byte> source)
    {
        return BinaryPrimitives.ReadUInt32LittleEndian(source.Slice(MagicOffset, sizeof(uint)));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteMagic(Span<byte> target, uint value)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(target.Slice(MagicOffset, sizeof(uint)), value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ushort ReadFormatVersion(ReadOnlySpan<byte> source)
    {
        return BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(FormatVersionOffset, sizeof(ushort)));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteFormatVersion(Span<byte> target, ushort value)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(target.Slice(FormatVersionOffset, sizeof(ushort)), value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ushort ReadHeaderSize(ReadOnlySpan<byte> source)
    {
        return BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(HeaderSizeOffset, sizeof(ushort)));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteHeaderSize(Span<byte> target, ushort value)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(target.Slice(HeaderSizeOffset, sizeof(ushort)), value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ushort ReadStorageKind(ReadOnlySpan<byte> source)
    {
        return BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(StorageKindOffset, sizeof(ushort)));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteStorageKind(Span<byte> target, ushort value)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(target.Slice(StorageKindOffset, sizeof(ushort)), value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint ReadFlags(ReadOnlySpan<byte> source)
    {
        return BinaryPrimitives.ReadUInt32LittleEndian(source.Slice(FlagsOffset, sizeof(uint)));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteFlags(Span<byte> target, uint value)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(target.Slice(FlagsOffset, sizeof(uint)), value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int ReadItemCount(ReadOnlySpan<byte> source)
    {
        return BinaryPrimitives.ReadInt32LittleEndian(source.Slice(ItemCountOffset, sizeof(int)));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteItemCount(Span<byte> target, int value)
    {
        BinaryPrimitives.WriteInt32LittleEndian(target.Slice(ItemCountOffset, sizeof(int)), value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ushort ReadIdentitySizeCode(ReadOnlySpan<byte> source)
    {
        return BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(IdentitySizeOffset, sizeof(ushort)));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteIdentitySizeCode(Span<byte> target, ushort value)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(target.Slice(IdentitySizeOffset, sizeof(ushort)), value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long ReadChildRootOffset(ReadOnlySpan<byte> source)
    {
        return BinaryPrimitives.ReadInt64LittleEndian(source.Slice(ChildRootOffsetOffset, sizeof(long)));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteChildRootOffset(Span<byte> target, long value)
    {
        BinaryPrimitives.WriteInt64LittleEndian(target.Slice(ChildRootOffsetOffset, sizeof(long)), value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetScalar8IdentityOffset(int index)
    {
        return checked(HeaderSize + (index * Scalar8IdentitySize));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetScalar16IdentityOffset(int index)
    {
        return checked(HeaderSize + (index * Scalar16IdentitySize));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong ReadScalar8Identity(ReadOnlySpan<byte> source, int index)
    {
        return BinaryPrimitives.ReadUInt64BigEndian(source.Slice(GetScalar8IdentityOffset(index), Scalar8IdentitySize));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteScalar8Identity(Span<byte> target, int index, ulong encodedIdentity)
    {
        BinaryPrimitives.WriteUInt64BigEndian(target.Slice(GetScalar8IdentityOffset(index), Scalar8IdentitySize), encodedIdentity);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong ReadScalar16IdentityHigh(ReadOnlySpan<byte> source, int index)
    {
        return BinaryPrimitives.ReadUInt64BigEndian(source.Slice(GetScalar16IdentityOffset(index), sizeof(ulong)));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong ReadScalar16IdentityLow(ReadOnlySpan<byte> source, int index)
    {
        return BinaryPrimitives.ReadUInt64BigEndian(source.Slice(GetScalar16IdentityOffset(index) + sizeof(ulong), sizeof(ulong)));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteScalar16Identity(Span<byte> target, int index, ulong encodedIdentityHigh, ulong encodedIdentityLow)
    {
        int offset = GetScalar16IdentityOffset(index);
        BinaryPrimitives.WriteUInt64BigEndian(target.Slice(offset, sizeof(ulong)), encodedIdentityHigh);
        BinaryPrimitives.WriteUInt64BigEndian(target.Slice(offset + sizeof(ulong), sizeof(ulong)), encodedIdentityLow);
    }
}
