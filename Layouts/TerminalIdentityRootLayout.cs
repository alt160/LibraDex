using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace LibraDex.Layouts;

internal static class TerminalIdentityRootLayout
{
    public const int Size = 4096;
    public const uint Magic = 0x52544954U;
    public const ushort FormatVersion = 1;
    public const ushort HeaderSize = 64;
    public const byte ShapeScalar8 = 1;
    public const byte ShapeVarKey = 2;
    public const byte ShapeScalar8VarIdentity = 3;
    public const byte ShapeScalar16VarIdentity = 4;
    public const byte ShapeVarKeyScalar16Identity = 5;
    public const byte ShapeFixedKeyScalar8Identity = 6;
    public const byte ShapeFixedKeyScalar16Identity = 7;
    public const byte ShapeFixedKeyVarIdentity = 8;

    public const int MagicOffset = 0;
    public const int FormatVersionOffset = 4;
    public const int HeaderSizeOffset = 6;
    public const int ShapeOffset = 8;
    public const int KeyLengthOffset = 12;
    public const int FirstShelfOffsetOffset = 16;
    public const int ShelfExtentSizeOffset = 24;
    public const int TailShelfOffsetOffset = 32;
    public const int KeyBytesOffset = HeaderSize;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint ReadMagic(ReadOnlySpan<byte> source) => BinaryPrimitives.ReadUInt32LittleEndian(source.Slice(MagicOffset, sizeof(uint)));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteMagic(Span<byte> target, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(target.Slice(MagicOffset, sizeof(uint)), value);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ushort ReadFormatVersion(ReadOnlySpan<byte> source) => BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(FormatVersionOffset, sizeof(ushort)));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteFormatVersion(Span<byte> target, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(target.Slice(FormatVersionOffset, sizeof(ushort)), value);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ushort ReadHeaderSize(ReadOnlySpan<byte> source) => BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(HeaderSizeOffset, sizeof(ushort)));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteHeaderSize(Span<byte> target, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(target.Slice(HeaderSizeOffset, sizeof(ushort)), value);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static byte ReadShape(ReadOnlySpan<byte> source) => source[ShapeOffset];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteShape(Span<byte> target, byte value) => target[ShapeOffset] = value;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int ReadKeyLength(ReadOnlySpan<byte> source) => BinaryPrimitives.ReadInt32LittleEndian(source.Slice(KeyLengthOffset, sizeof(int)));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteKeyLength(Span<byte> target, int value) => BinaryPrimitives.WriteInt32LittleEndian(target.Slice(KeyLengthOffset, sizeof(int)), value);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long ReadFirstShelfOffset(ReadOnlySpan<byte> source) => BinaryPrimitives.ReadInt64LittleEndian(source.Slice(FirstShelfOffsetOffset, sizeof(long)));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteFirstShelfOffset(Span<byte> target, long value) => BinaryPrimitives.WriteInt64LittleEndian(target.Slice(FirstShelfOffsetOffset, sizeof(long)), value);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int ReadShelfExtentSize(ReadOnlySpan<byte> source) => BinaryPrimitives.ReadInt32LittleEndian(source.Slice(ShelfExtentSizeOffset, sizeof(int)));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteShelfExtentSize(Span<byte> target, int value) => BinaryPrimitives.WriteInt32LittleEndian(target.Slice(ShelfExtentSizeOffset, sizeof(int)), value);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long ReadTailShelfOffset(ReadOnlySpan<byte> source) => BinaryPrimitives.ReadInt64LittleEndian(source.Slice(TailShelfOffsetOffset, sizeof(long)));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteTailShelfOffset(Span<byte> target, long value) => BinaryPrimitives.WriteInt64LittleEndian(target.Slice(TailShelfOffsetOffset, sizeof(long)), value);

    public static void Initialize(Span<byte> target, byte shape, ReadOnlySpan<byte> keyBytes, int shelfExtentSize, long firstShelfOffset)
    {
        if (keyBytes.Length <= 0 || keyBytes.Length > Size - KeyBytesOffset)
        {
            throw new ArgumentOutOfRangeException(nameof(keyBytes), keyBytes.Length, "The terminal identity root key must fit in the root page.");
        }

        target.Clear();
        WriteMagic(target, Magic);
        WriteFormatVersion(target, FormatVersion);
        WriteHeaderSize(target, HeaderSize);
        WriteShape(target, shape);
        WriteKeyLength(target, keyBytes.Length);
        WriteFirstShelfOffset(target, firstShelfOffset);
        WriteShelfExtentSize(target, shelfExtentSize);
        WriteTailShelfOffset(target, 0);
        keyBytes.CopyTo(target.Slice(KeyBytesOffset, keyBytes.Length));
    }
}
