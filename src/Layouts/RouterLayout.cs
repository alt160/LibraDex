using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace LibraDex.Layouts;

/// <summary>
/// Defines the fixed router page layout used by the initial LibraDex router implementation.<br/>
/// The router contract uses the same 10-byte route slot for compressed, expanded, root, and later multi-byte routers.<br/>
/// </summary>
internal static class RouterLayout
{
    public const int Size = 4096;
    public const uint Magic = 0x5258444CU;
    public const ushort FormatVersion = 1;
    public const ushort HeaderSize = 64;
    public const int RouteSlotSize = 10;
    public const int MaxOneByteRouteCount = 256;
    public const uint DirectIndexFlag = 1;
    public const uint ArenaMemberFlag = 2;
    public const uint ArenaLengthFromPageCountFlag = 1;

    public const int MagicOffset = 0;
    public const int FormatVersionOffset = 4;
    public const int HeaderSizeOffset = 6;
    public const int FlagsOffset = 8;
    public const int PrefixByteCountOffset = 12;
    public const int KeyDepthOffset = 14;
    public const int RouteCountOffset = 16;
    public const int MaxRouteCountOffset = 18;
    public const int AllocationClassIdOffset = 20;
    public const int ArenaBaseDeltaOffset = 24;
    public const int ArenaLengthOffset = 28;
    public const int ArenaRouterPageSizeOffset = 30;
    public const int ArenaRouterPageIndexOffset = 32;
    public const int ArenaRouterPageCountOffset = 34;
    public const int ArenaFlagsOffset = 36;
    public const int RoutesOffset = HeaderSize;

    public const int RoutePrefixStartOffset = 0;
    public const int RoutePrefixEndOffset = 1;
    public const int RouteTargetOffsetOffset = 2;

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
    public static byte ReadPrefixByteCount(ReadOnlySpan<byte> source)
    {
        return source[PrefixByteCountOffset];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WritePrefixByteCount(Span<byte> target, byte value)
    {
        target[PrefixByteCountOffset] = value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ushort ReadKeyDepth(ReadOnlySpan<byte> source)
    {
        return BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(KeyDepthOffset, sizeof(ushort)));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteKeyDepth(Span<byte> target, ushort value)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(target.Slice(KeyDepthOffset, sizeof(ushort)), value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ushort ReadRouteCount(ReadOnlySpan<byte> source)
    {
        return BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(RouteCountOffset, sizeof(ushort)));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteRouteCount(Span<byte> target, ushort value)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(target.Slice(RouteCountOffset, sizeof(ushort)), value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ushort ReadMaxRouteCount(ReadOnlySpan<byte> source)
    {
        return BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(MaxRouteCountOffset, sizeof(ushort)));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteMaxRouteCount(Span<byte> target, ushort value)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(target.Slice(MaxRouteCountOffset, sizeof(ushort)), value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ushort ReadAllocationClassId(ReadOnlySpan<byte> source)
    {
        return BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(AllocationClassIdOffset, sizeof(ushort)));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteAllocationClassId(Span<byte> target, ushort value)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(target.Slice(AllocationClassIdOffset, sizeof(ushort)), value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int ReadArenaBaseDelta(ReadOnlySpan<byte> source)
    {
        return BinaryPrimitives.ReadInt32LittleEndian(source.Slice(ArenaBaseDeltaOffset, sizeof(int)));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteArenaBaseDelta(Span<byte> target, int value)
    {
        BinaryPrimitives.WriteInt32LittleEndian(target.Slice(ArenaBaseDeltaOffset, sizeof(int)), value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int ReadArenaLength(ReadOnlySpan<byte> source)
    {
        ushort persistedLength = BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(ArenaLengthOffset, sizeof(ushort)));
        return persistedLength == 0 ? ushort.MaxValue + 1 : persistedLength;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteArenaLength(Span<byte> target, int value)
    {
        if (value <= 0 || value > ushort.MaxValue + 1)
        {
            throw new ArgumentOutOfRangeException(nameof(value), value, "Router arena length must fit the 16-bit persisted arena-length field, with zero reserved for 65536 bytes.");
        }

        ushort persistedLength = value == ushort.MaxValue + 1 ? (ushort)0 : checked((ushort)value);
        BinaryPrimitives.WriteUInt16LittleEndian(target.Slice(ArenaLengthOffset, sizeof(ushort)), persistedLength);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ushort ReadArenaRouterPageSize(ReadOnlySpan<byte> source)
    {
        return BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(ArenaRouterPageSizeOffset, sizeof(ushort)));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteArenaRouterPageSize(Span<byte> target, ushort value)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(target.Slice(ArenaRouterPageSizeOffset, sizeof(ushort)), value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ushort ReadArenaRouterPageIndex(ReadOnlySpan<byte> source)
    {
        return BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(ArenaRouterPageIndexOffset, sizeof(ushort)));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteArenaRouterPageIndex(Span<byte> target, ushort value)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(target.Slice(ArenaRouterPageIndexOffset, sizeof(ushort)), value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ushort ReadArenaRouterPageCount(ReadOnlySpan<byte> source)
    {
        return BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(ArenaRouterPageCountOffset, sizeof(ushort)));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteArenaRouterPageCount(Span<byte> target, ushort value)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(target.Slice(ArenaRouterPageCountOffset, sizeof(ushort)), value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint ReadArenaFlags(ReadOnlySpan<byte> source)
    {
        return BinaryPrimitives.ReadUInt32LittleEndian(source.Slice(ArenaFlagsOffset, sizeof(uint)));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteArenaFlags(Span<byte> target, uint value)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(target.Slice(ArenaFlagsOffset, sizeof(uint)), value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ReadOnlySpan<byte> GetRoute(ReadOnlySpan<byte> source, int routeIndex)
    {
        return source.Slice(GetRouteOffset(routeIndex), RouteSlotSize);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Span<byte> GetRoute(Span<byte> target, int routeIndex)
    {
        return target.Slice(GetRouteOffset(routeIndex), RouteSlotSize);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetRouteOffset(int routeIndex)
    {
        return checked(RoutesOffset + (routeIndex * RouteSlotSize));
    }

    public static int GetMultiByteRouteStemOffset(int maxRouteCount, int prefixByteCount, int routeIndex)
    {
        if (prefixByteCount <= 1)
        {
            throw new ArgumentOutOfRangeException(nameof(prefixByteCount), prefixByteCount, "Multi-byte route stems require at least two consumed prefix bytes.");
        }

        return checked(RoutesOffset + (maxRouteCount * RouteSlotSize) + (routeIndex * (prefixByteCount - 1)));
    }

    public static int GetMultiByteRouteStorageLength(int maxRouteCount, int prefixByteCount)
    {
        if (maxRouteCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxRouteCount), maxRouteCount, "Route count cannot be negative.");
        }

        if (prefixByteCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(prefixByteCount), prefixByteCount, "Router prefix byte count must be positive.");
        }

        return checked((maxRouteCount * RouteSlotSize) + (maxRouteCount * Math.Max(0, prefixByteCount - 1)));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static byte ReadRoutePrefixStart(ReadOnlySpan<byte> route)
    {
        return route[RoutePrefixStartOffset];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteRoutePrefixStart(Span<byte> route, byte value)
    {
        route[RoutePrefixStartOffset] = value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static byte ReadRoutePrefixEnd(ReadOnlySpan<byte> route)
    {
        return route[RoutePrefixEndOffset];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteRoutePrefixEnd(Span<byte> route, byte value)
    {
        route[RoutePrefixEndOffset] = value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long ReadRouteTargetOffset(ReadOnlySpan<byte> route)
    {
        return BinaryPrimitives.ReadInt64LittleEndian(route.Slice(RouteTargetOffsetOffset, sizeof(long)));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteRouteTargetOffset(Span<byte> route, long value)
    {
        BinaryPrimitives.WriteInt64LittleEndian(route.Slice(RouteTargetOffsetOffset, sizeof(long)), value);
    }
}
