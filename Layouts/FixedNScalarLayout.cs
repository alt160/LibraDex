using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace LibraDex.Layouts;

/// <summary>
/// Defines the programmable fixed-key `FixedNScalar8` (`FSN-8`) shelf byte layout.<br/>
/// Runtime profile values select key width and region math, while each shelf persists enough header data to validate that profile.<br/>
/// </summary>
internal static class FixedNScalar8Layout
{
    public const uint Magic = 0x384E5346U;
    public const ushort FormatVersion = 1;
    public const ushort HeaderSize = 40;
    public const int SlotSize = sizeof(ushort);
    public const ushort DeletedSlotOffset = 0;
    public const int IdentitySize = sizeof(ulong);

    public const int MagicOffset = 0;
    public const int FormatVersionOffset = 4;
    public const int HeaderSizeOffset = 6;
    public const int FlagsOffset = 8;
    public const int ItemCountOffset = 12;
    public const int KeySizeOffset = 14;

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
    public static ushort ReadItemCount(ReadOnlySpan<byte> source)
    {
        return BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(ItemCountOffset, sizeof(ushort)));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteItemCount(Span<byte> target, ushort value)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(target.Slice(ItemCountOffset, sizeof(ushort)), value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ushort ReadKeySize(ReadOnlySpan<byte> source)
    {
        return BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(KeySizeOffset, sizeof(ushort)));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteKeySize(Span<byte> target, ushort value)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(target.Slice(KeySizeOffset, sizeof(ushort)), value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ushort ReadSlot(ReadOnlySpan<byte> source, FixedNScalar8Profile profile, int slotIndex)
    {
        return BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(GetSlotOffset(profile, slotIndex), SlotSize));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteSlot(Span<byte> target, FixedNScalar8Profile profile, int slotIndex, ushort itemOffset)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(target.Slice(GetSlotOffset(profile, slotIndex), SlotSize), itemOffset);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetSlotOffset(FixedNScalar8Profile profile, int slotIndex)
    {
        return checked(profile.SlotRegionOffset + (slotIndex * SlotSize));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetItemOffset(FixedNScalar8Profile profile, int itemIndex)
    {
        return checked(profile.ItemRegionOffset + (itemIndex * profile.ItemSize));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ReadOnlySpan<byte> ReadItemKey(ReadOnlySpan<byte> source, int itemOffset, FixedNScalar8Profile profile)
    {
        return source.Slice(itemOffset, profile.KeySize);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ReadOnlySpan<byte> ReadItemIdentityBytes(ReadOnlySpan<byte> source, int itemOffset, FixedNScalar8Profile profile)
    {
        return source.Slice(itemOffset + profile.KeySize, IdentitySize);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong ReadItemIdentity(ReadOnlySpan<byte> source, int itemOffset, FixedNScalar8Profile profile)
    {
        return BinaryPrimitives.ReadUInt64BigEndian(ReadItemIdentityBytes(source, itemOffset, profile));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteItem(Span<byte> target, int itemOffset, FixedNScalar8Profile profile, ReadOnlySpan<byte> key, ulong encodedIdentity)
    {
        key.CopyTo(target.Slice(itemOffset, profile.KeySize));
        BinaryPrimitives.WriteUInt64BigEndian(target.Slice(itemOffset + profile.KeySize, IdentitySize), encodedIdentity);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int CompareItemKey(ReadOnlySpan<byte> source, int itemOffset, FixedNScalar8Profile profile, ReadOnlySpan<byte> key)
    {
        return ReadItemKey(source, itemOffset, profile).SequenceCompareTo(key);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int CompareItemTuple(ReadOnlySpan<byte> source, int itemOffset, FixedNScalar8Profile profile, ReadOnlySpan<byte> key, ulong encodedIdentity)
    {
        int keyComparison = CompareItemKey(source, itemOffset, profile, key);
        if (keyComparison != 0)
        {
            return keyComparison;
        }

        ulong itemIdentity = ReadItemIdentity(source, itemOffset, profile);
        if (itemIdentity < encodedIdentity)
        {
            return -1;
        }

        return itemIdentity > encodedIdentity ? 1 : 0;
    }
}

/// <summary>
/// Defines the programmable fixed-key `FixedNScalar16` (`FSN-16`) shelf byte layout.<br/>
/// The key-width contract matches `FSN-8`; the identity lane stores sixteen caller-supplied sortable bytes.<br/>
/// </summary>
internal static class FixedNScalar16Layout
{
    public const uint Magic = 0x364E5346U;
    public const ushort FormatVersion = 1;
    public const ushort HeaderSize = FixedNScalar8Layout.HeaderSize;
    public const int SlotSize = FixedNScalar8Layout.SlotSize;
    public const ushort DeletedSlotOffset = FixedNScalar8Layout.DeletedSlotOffset;
    public const int IdentitySize = 16;

    public const int MagicOffset = FixedNScalar8Layout.MagicOffset;
    public const int FormatVersionOffset = FixedNScalar8Layout.FormatVersionOffset;
    public const int HeaderSizeOffset = FixedNScalar8Layout.HeaderSizeOffset;
    public const int FlagsOffset = FixedNScalar8Layout.FlagsOffset;
    public const int ItemCountOffset = FixedNScalar8Layout.ItemCountOffset;
    public const int KeySizeOffset = FixedNScalar8Layout.KeySizeOffset;

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
    public static ushort ReadItemCount(ReadOnlySpan<byte> source)
    {
        return BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(ItemCountOffset, sizeof(ushort)));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteItemCount(Span<byte> target, ushort value)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(target.Slice(ItemCountOffset, sizeof(ushort)), value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ushort ReadKeySize(ReadOnlySpan<byte> source)
    {
        return BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(KeySizeOffset, sizeof(ushort)));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteKeySize(Span<byte> target, ushort value)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(target.Slice(KeySizeOffset, sizeof(ushort)), value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ushort ReadSlot(ReadOnlySpan<byte> source, FixedNScalar16Profile profile, int slotIndex)
    {
        return BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(GetSlotOffset(profile, slotIndex), SlotSize));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteSlot(Span<byte> target, FixedNScalar16Profile profile, int slotIndex, ushort itemOffset)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(target.Slice(GetSlotOffset(profile, slotIndex), SlotSize), itemOffset);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetSlotOffset(FixedNScalar16Profile profile, int slotIndex)
    {
        return checked(profile.SlotRegionOffset + (slotIndex * SlotSize));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetItemOffset(FixedNScalar16Profile profile, int itemIndex)
    {
        return checked(profile.ItemRegionOffset + (itemIndex * profile.ItemSize));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ReadOnlySpan<byte> ReadItemKey(ReadOnlySpan<byte> source, int itemOffset, FixedNScalar16Profile profile)
    {
        return source.Slice(itemOffset, profile.KeySize);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ReadOnlySpan<byte> ReadItemIdentity(ReadOnlySpan<byte> source, int itemOffset, FixedNScalar16Profile profile)
    {
        return source.Slice(itemOffset + profile.KeySize, IdentitySize);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteItem(Span<byte> target, int itemOffset, FixedNScalar16Profile profile, ReadOnlySpan<byte> key, ReadOnlySpan<byte> encodedIdentity)
    {
        key.CopyTo(target.Slice(itemOffset, profile.KeySize));
        encodedIdentity.CopyTo(target.Slice(itemOffset + profile.KeySize, IdentitySize));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int CompareItemKey(ReadOnlySpan<byte> source, int itemOffset, FixedNScalar16Profile profile, ReadOnlySpan<byte> key)
    {
        return ReadItemKey(source, itemOffset, profile).SequenceCompareTo(key);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int CompareItemTuple(ReadOnlySpan<byte> source, int itemOffset, FixedNScalar16Profile profile, ReadOnlySpan<byte> key, ReadOnlySpan<byte> encodedIdentity)
    {
        int keyComparison = CompareItemKey(source, itemOffset, profile, key);
        return keyComparison != 0
            ? keyComparison
            : ReadItemIdentity(source, itemOffset, profile).SequenceCompareTo(encodedIdentity);
    }
}
