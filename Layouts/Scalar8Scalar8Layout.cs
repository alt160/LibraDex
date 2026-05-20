using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace LibraDex.Layouts;

/// <summary>
/// Defines the fixed `Scalar8Scalar8` (`SS8-8`) shelf byte layout.<br/>
/// Runtime constants describe the whole shelf extent, but only mutable shelf-local state is persisted in the header.<br/>
/// </summary>
internal static class Scalar8Scalar8Layout
{
    public const uint Magic = 0x38385353U;
    public const ushort FormatVersion = 1;
    public const ushort HeaderSize = 32;
    public const int SlotSize = sizeof(ushort);
    public const int KeySize = sizeof(ulong);
    public const int IdentitySize = sizeof(ulong);
    public const int ItemSize = KeySize + IdentitySize;

    public const int MagicOffset = 0;
    public const int FormatVersionOffset = 4;
    public const int HeaderSizeOffset = 6;
    public const int FlagsOffset = 8;
    public const int ItemCountOffset = 12;

    public const int ItemKeyOffset = 0;
    public const int ItemIdentityOffset = KeySize;

    /// <summary>
    /// Reads the shelf magic value from the persisted header.<br/>
    /// Header metadata uses little-endian engine fields; scalar key and identity payloads use sortable big-endian fields.<br/>
    /// </summary>
    /// <param name="source">The shelf bytes.</param>
    /// <returns>The persisted magic value.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint ReadMagic(ReadOnlySpan<byte> source)
    {
        return BinaryPrimitives.ReadUInt32LittleEndian(source.Slice(MagicOffset, sizeof(uint)));
    }

    /// <summary>
    /// Writes the shelf magic value to the persisted header.<br/>
    /// This is header metadata, not a sortable scalar payload, so it uses the engine's little-endian header convention.<br/>
    /// </summary>
    /// <param name="target">The writable shelf bytes.</param>
    /// <param name="value">The magic value to persist.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteMagic(Span<byte> target, uint value)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(target.Slice(MagicOffset, sizeof(uint)), value);
    }

    /// <summary>
    /// Reads the shelf format version from the persisted header.<br/>
    /// The format version validates the local shelf byte contract selected by the index profile.<br/>
    /// </summary>
    /// <param name="source">The shelf bytes.</param>
    /// <returns>The persisted format version.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ushort ReadFormatVersion(ReadOnlySpan<byte> source)
    {
        return BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(FormatVersionOffset, sizeof(ushort)));
    }

    /// <summary>
    /// Writes the shelf format version to the persisted header.<br/>
    /// This value is per-format validation metadata and is not repeated shape policy.<br/>
    /// </summary>
    /// <param name="target">The writable shelf bytes.</param>
    /// <param name="value">The format version to persist.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteFormatVersion(Span<byte> target, ushort value)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(target.Slice(FormatVersionOffset, sizeof(ushort)), value);
    }

    /// <summary>
    /// Reads the persisted shelf header size.<br/>
    /// The value exists for local validation while runtime shape constants still own fixed slot and item math.<br/>
    /// </summary>
    /// <param name="source">The shelf bytes.</param>
    /// <returns>The persisted header size.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ushort ReadHeaderSize(ReadOnlySpan<byte> source)
    {
        return BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(HeaderSizeOffset, sizeof(ushort)));
    }

    /// <summary>
    /// Writes the persisted shelf header size.<br/>
    /// This keeps reopen validation local without storing every runtime shape constant in each shelf.<br/>
    /// </summary>
    /// <param name="target">The writable shelf bytes.</param>
    /// <param name="value">The header size to persist.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteHeaderSize(Span<byte> target, ushort value)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(target.Slice(HeaderSizeOffset, sizeof(ushort)), value);
    }

    /// <summary>
    /// Reads shelf-local flags from the persisted header.<br/>
    /// Flags are reserved for local shelf state; index-level uniqueness is stored in the index profile, not here.<br/>
    /// </summary>
    /// <param name="source">The shelf bytes.</param>
    /// <returns>The persisted flags.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint ReadFlags(ReadOnlySpan<byte> source)
    {
        return BinaryPrimitives.ReadUInt32LittleEndian(source.Slice(FlagsOffset, sizeof(uint)));
    }

    /// <summary>
    /// Writes shelf-local flags to the persisted header.<br/>
    /// The first implementation writes zero and reserves the field for later local shelf state.<br/>
    /// </summary>
    /// <param name="target">The writable shelf bytes.</param>
    /// <param name="value">The flags to persist.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteFlags(Span<byte> target, uint value)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(target.Slice(FlagsOffset, sizeof(uint)), value);
    }

    /// <summary>
    /// Reads the number of physical items currently used in the shelf.<br/>
    /// Sorted order is represented by the first `itemCount` slots in the slot array.<br/>
    /// </summary>
    /// <param name="source">The shelf bytes.</param>
    /// <returns>The persisted item count.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ushort ReadItemCount(ReadOnlySpan<byte> source)
    {
        return BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(ItemCountOffset, sizeof(ushort)));
    }

    /// <summary>
    /// Writes the number of physical items currently used in the shelf.<br/>
    /// The count also selects the next physical item append position for this fixed-width shelf.<br/>
    /// </summary>
    /// <param name="target">The writable shelf bytes.</param>
    /// <param name="value">The item count to persist.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteItemCount(Span<byte> target, ushort value)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(target.Slice(ItemCountOffset, sizeof(ushort)), value);
    }

    /// <summary>
    /// Reads a sorted slot as a byte offset to a physical item payload.<br/>
    /// Only slots below the persisted item count are logically active.<br/>
    /// </summary>
    /// <param name="source">The shelf bytes.</param>
    /// <param name="slotIndex">The zero-based sorted slot index.</param>
    /// <returns>The byte offset of the referenced physical item.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ushort ReadSlot(ReadOnlySpan<byte> source, Scalar8Scalar8Profile profile, int slotIndex)
    {
        return BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(GetSlotOffset(profile, slotIndex), SlotSize));
    }

    /// <summary>
    /// Writes a sorted slot as a byte offset to a physical item payload.<br/>
    /// Slot mutation is the only data movement needed to maintain sorted order for fixed-width items.<br/>
    /// </summary>
    /// <param name="target">The writable shelf bytes.</param>
    /// <param name="slotIndex">The zero-based sorted slot index.</param>
    /// <param name="itemOffset">The byte offset of the referenced physical item.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteSlot(Span<byte> target, Scalar8Scalar8Profile profile, int slotIndex, ushort itemOffset)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(target.Slice(GetSlotOffset(profile, slotIndex), SlotSize), itemOffset);
    }

    /// <summary>
    /// Gets the byte offset for a sorted slot index.<br/>
    /// The slot region is fixed-width and pre-partitioned for the maximum shelf item count.<br/>
    /// </summary>
    /// <param name="slotIndex">The zero-based sorted slot index.</param>
    /// <returns>The byte offset of the slot.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetSlotOffset(Scalar8Scalar8Profile profile, int slotIndex)
    {
        return checked(profile.SlotRegionOffset + (slotIndex * SlotSize));
    }

    /// <summary>
    /// Gets the byte offset for a physical item index.<br/>
    /// Physical item order is append order; sorted logical order is represented by the slot array.<br/>
    /// </summary>
    /// <param name="itemIndex">The zero-based physical item index.</param>
    /// <returns>The byte offset of the item payload.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetItemOffset(Scalar8Scalar8Profile profile, int itemIndex)
    {
        return checked(profile.ItemRegionOffset + (itemIndex * ItemSize));
    }

    /// <summary>
    /// Reads a sortable scalar key from a physical item payload.<br/>
    /// The returned value is the canonical encoded value, not a decoded developer-facing scalar.<br/>
    /// </summary>
    /// <param name="source">The shelf bytes.</param>
    /// <param name="itemOffset">The byte offset of the physical item payload.</param>
    /// <returns>The encoded sortable key.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong ReadItemKey(ReadOnlySpan<byte> source, int itemOffset)
    {
        return BinaryPrimitives.ReadUInt64BigEndian(source.Slice(itemOffset + ItemKeyOffset, KeySize));
    }

    /// <summary>
    /// Writes a sortable scalar key to a physical item payload.<br/>
    /// The supplied value is already the canonical sortable scalar representation.<br/>
    /// </summary>
    /// <param name="target">The writable shelf bytes.</param>
    /// <param name="itemOffset">The byte offset of the physical item payload.</param>
    /// <param name="encodedKey">The encoded sortable key to persist.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteItemKey(Span<byte> target, int itemOffset, ulong encodedKey)
    {
        BinaryPrimitives.WriteUInt64BigEndian(target.Slice(itemOffset + ItemKeyOffset, KeySize), encodedKey);
    }

    /// <summary>
    /// Reads a sortable scalar identity from a physical item payload.<br/>
    /// The returned value is the canonical encoded value used as the duplicate-key tie breaker.<br/>
    /// </summary>
    /// <param name="source">The shelf bytes.</param>
    /// <param name="itemOffset">The byte offset of the physical item payload.</param>
    /// <returns>The encoded sortable identity.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong ReadItemIdentity(ReadOnlySpan<byte> source, int itemOffset)
    {
        return BinaryPrimitives.ReadUInt64BigEndian(source.Slice(itemOffset + ItemIdentityOffset, IdentitySize));
    }

    /// <summary>
    /// Writes a sortable scalar identity to a physical item payload.<br/>
    /// The supplied value is already the canonical sortable scalar representation.<br/>
    /// </summary>
    /// <param name="target">The writable shelf bytes.</param>
    /// <param name="itemOffset">The byte offset of the physical item payload.</param>
    /// <param name="encodedIdentity">The encoded sortable identity to persist.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteItemIdentity(Span<byte> target, int itemOffset, ulong encodedIdentity)
    {
        BinaryPrimitives.WriteUInt64BigEndian(target.Slice(itemOffset + ItemIdentityOffset, IdentitySize), encodedIdentity);
    }

    /// <summary>
    /// Compares a persisted item tuple to an encoded `(key, identity)` tuple.<br/>
    /// The comparison uses encoded scalar values and therefore does not decode developer-facing values in the hot path.<br/>
    /// </summary>
    /// <param name="source">The shelf bytes.</param>
    /// <param name="itemOffset">The byte offset of the physical item payload.</param>
    /// <param name="encodedKey">The encoded sortable key to compare.</param>
    /// <param name="encodedIdentity">The encoded sortable identity to compare.</param>
    /// <returns>A negative value, zero, or a positive value according to tuple order.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int CompareItemTuple(ReadOnlySpan<byte> source, int itemOffset, ulong encodedKey, ulong encodedIdentity)
    {
        ulong itemKey = ReadItemKey(source, itemOffset);
        if (itemKey < encodedKey)
        {
            return -1;
        }

        if (itemKey > encodedKey)
        {
            return 1;
        }

        ulong itemIdentity = ReadItemIdentity(source, itemOffset);
        if (itemIdentity < encodedIdentity)
        {
            return -1;
        }

        return itemIdentity > encodedIdentity ? 1 : 0;
    }

    /// <summary>
    /// Encodes an unsigned 64-bit scalar into the canonical sortable representation for this shape.<br/>
    /// Unsigned scalar-8 values are already monotonic as integers and are persisted as big-endian bytes by item writers.<br/>
    /// </summary>
    /// <param name="value">The unsigned scalar value.</param>
    /// <returns>The encoded sortable scalar.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong EncodeUnsignedScalar8(ulong value)
    {
        return value;
    }
}
