using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace LibraDex.Layouts;

/// <summary>
/// Defines the fixed `Fixed32Scalar16` (`FS32-16`) shelf byte layout.<br/>
/// Runtime constants describe the whole shelf extent, but only mutable shelf-local state is persisted in the header.<br/>
/// </summary>
internal static class Fixed32Scalar16Layout
{
    public const uint Magic = 0x36323346U;
    public const ushort FormatVersion = 1;
    public const ushort HeaderSize = 32;
    public const int SlotSize = sizeof(ushort);
    public const ushort DeletedSlotOffset = 0;
    public const int KeySize = sizeof(ulong) * 4;
    public const int IdentitySize = sizeof(ulong) * 2;
    public const int ItemSize = KeySize + IdentitySize;
    public const uint DescendingFlag = 1U;

    public const int MagicOffset = 0;
    public const int FormatVersionOffset = 4;
    public const int HeaderSizeOffset = 6;
    public const int FlagsOffset = 8;
    public const int ItemCountOffset = 12;

    public const int ItemKeyPart0Offset = 0;
    public const int ItemKeyPart1Offset = sizeof(ulong);
    public const int ItemKeyPart2Offset = sizeof(ulong) * 2;
    public const int ItemKeyPart3Offset = sizeof(ulong) * 3;
    public const int ItemIdentityHighOffset = KeySize;
    public const int ItemIdentityLowOffset = KeySize + sizeof(ulong);

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
    /// <param name="profile">The index-level shelf profile.</param>
    /// <param name="slotIndex">The zero-based sorted slot index.</param>
    /// <returns>The byte offset of the referenced physical item.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ushort ReadSlot(ReadOnlySpan<byte> source, Fixed32Scalar16Profile profile, int slotIndex)
    {
        return BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(GetSlotOffset(profile, slotIndex), SlotSize));
    }

    /// <summary>
    /// Writes a sorted slot as a byte offset to a physical item payload.<br/>
    /// Slot mutation is the only data movement needed to maintain sorted order for fixed-width items.<br/>
    /// </summary>
    /// <param name="target">The writable shelf bytes.</param>
    /// <param name="profile">The index-level shelf profile.</param>
    /// <param name="slotIndex">The zero-based sorted slot index.</param>
    /// <param name="itemOffset">The byte offset of the referenced physical item.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteSlot(Span<byte> target, Fixed32Scalar16Profile profile, int slotIndex, ushort itemOffset)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(target.Slice(GetSlotOffset(profile, slotIndex), SlotSize), itemOffset);
    }

    /// <summary>
    /// Gets the byte offset for a sorted slot index.<br/>
    /// The slot region is fixed-width and pre-partitioned for the maximum shelf item count.<br/>
    /// </summary>
    /// <param name="profile">The index-level shelf profile.</param>
    /// <param name="slotIndex">The zero-based sorted slot index.</param>
    /// <returns>The byte offset of the slot.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetSlotOffset(Fixed32Scalar16Profile profile, int slotIndex)
    {
        return checked(profile.SlotRegionOffset + (slotIndex * SlotSize));
    }

    /// <summary>
    /// Gets the byte offset for a physical item index.<br/>
    /// Physical item order is append order; sorted logical order is represented by the slot array.<br/>
    /// </summary>
    /// <param name="profile">The index-level shelf profile.</param>
    /// <param name="itemIndex">The zero-based physical item index.</param>
    /// <returns>The byte offset of the item payload.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetItemOffset(Fixed32Scalar16Profile profile, int itemIndex)
    {
        return checked(profile.ItemRegionOffset + (itemIndex * ItemSize));
    }

    /// <summary>
    /// Reads the high sortable scalar half from a physical item key payload.<br/>
    /// The returned value is the canonical encoded high half, not a decoded developer-facing scalar.<br/>
    /// </summary>
    /// <param name="source">The shelf bytes.</param>
    /// <param name="itemOffset">The byte offset of the physical item payload.</param>
    /// <returns>The encoded sortable key part 0.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong ReadItemKeyPart0(ReadOnlySpan<byte> source, int itemOffset)
    {
        return BinaryPrimitives.ReadUInt64BigEndian(source.Slice(itemOffset + ItemKeyPart0Offset, sizeof(ulong)));
    }

    /// <summary>
    /// Reads the low sortable scalar half from a physical item key payload.<br/>
    /// The returned value is the canonical encoded low half, not a decoded developer-facing scalar.<br/>
    /// </summary>
    /// <param name="source">The shelf bytes.</param>
    /// <param name="itemOffset">The byte offset of the physical item payload.</param>
    /// <returns>The encoded sortable key part 1.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong ReadItemKeyPart1(ReadOnlySpan<byte> source, int itemOffset)
    {
        return BinaryPrimitives.ReadUInt64BigEndian(source.Slice(itemOffset + ItemKeyPart1Offset, sizeof(ulong)));
    }

    /// <summary>
    /// Reads the third sortable fixed-key part from a physical item key payload.<br/>
    /// The returned value is the canonical encoded key part, not a decoded developer-facing value.<br/>
    /// </summary>
    /// <param name="source">The shelf bytes.</param>
    /// <param name="itemOffset">The byte offset of the physical item payload.</param>
    /// <returns>The encoded sortable key part 2.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong ReadItemKeyPart2(ReadOnlySpan<byte> source, int itemOffset)
    {
        return BinaryPrimitives.ReadUInt64BigEndian(source.Slice(itemOffset + ItemKeyPart2Offset, sizeof(ulong)));
    }

    /// <summary>
    /// Reads the fourth sortable fixed-key part from a physical item key payload.<br/>
    /// The returned value is the canonical encoded key part, not a decoded developer-facing value.<br/>
    /// </summary>
    /// <param name="source">The shelf bytes.</param>
    /// <param name="itemOffset">The byte offset of the physical item payload.</param>
    /// <returns>The encoded sortable key part 3.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong ReadItemKeyPart3(ReadOnlySpan<byte> source, int itemOffset)
    {
        return BinaryPrimitives.ReadUInt64BigEndian(source.Slice(itemOffset + ItemKeyPart3Offset, sizeof(ulong)));
    }

    /// <summary>
    /// Writes a sortable 32-byte fixed key to a physical item payload.<br/>
    /// The supplied values are already the canonical sortable scalar representation.<br/>
    /// </summary>
    /// <param name="target">The writable shelf bytes.</param>
    /// <param name="itemOffset">The byte offset of the physical item payload.</param>
    /// <param name="key0">The encoded sortable key part 0 to persist.</param>
    /// <param name="key1">The encoded sortable key part 1 to persist.</param>
    /// <param name="key2">The encoded sortable key part 2 to persist.</param>
    /// <param name="key3">The encoded sortable key part 3 to persist.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteItemKey(Span<byte> target, int itemOffset, ulong key0, ulong key1, ulong key2, ulong key3)
    {
        BinaryPrimitives.WriteUInt64BigEndian(target.Slice(itemOffset + ItemKeyPart0Offset, sizeof(ulong)), key0);
        BinaryPrimitives.WriteUInt64BigEndian(target.Slice(itemOffset + ItemKeyPart1Offset, sizeof(ulong)), key1);
        BinaryPrimitives.WriteUInt64BigEndian(target.Slice(itemOffset + ItemKeyPart2Offset, sizeof(ulong)), key2);
        BinaryPrimitives.WriteUInt64BigEndian(target.Slice(itemOffset + ItemKeyPart3Offset, sizeof(ulong)), key3);
    }

    /// <summary>
    /// Reads the first byte-order-preserving 8-byte lane from a fixed 16-byte identity payload.<br/>
    /// The returned value is the canonical encoded lane used after the key as the duplicate-key tie breaker.<br/>
    /// </summary>
    /// <param name="source">The shelf bytes.</param>
    /// <param name="itemOffset">The byte offset of the physical item payload.</param>
    /// <returns>The encoded identity high lane.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong ReadItemIdentityHigh(ReadOnlySpan<byte> source, int itemOffset)
    {
        return BinaryPrimitives.ReadUInt64BigEndian(source.Slice(itemOffset + ItemIdentityHighOffset, sizeof(ulong)));
    }

    /// <summary>
    /// Reads the second byte-order-preserving 8-byte lane from a fixed 16-byte identity payload.<br/>
    /// The returned value is the canonical encoded lane used after the high lane as the final duplicate-key tie breaker.<br/>
    /// </summary>
    /// <param name="source">The shelf bytes.</param>
    /// <param name="itemOffset">The byte offset of the physical item payload.</param>
    /// <returns>The encoded identity low lane.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong ReadItemIdentityLow(ReadOnlySpan<byte> source, int itemOffset)
    {
        return BinaryPrimitives.ReadUInt64BigEndian(source.Slice(itemOffset + ItemIdentityLowOffset, sizeof(ulong)));
    }

    /// <summary>
    /// Writes a fixed 16-byte identity to a physical item payload from two byte-order-preserving 8-byte lanes.<br/>
    /// The supplied lanes are already the canonical persisted identity bytes projected as big-endian integer values.<br/>
    /// </summary>
    /// <param name="target">The writable shelf bytes.</param>
    /// <param name="itemOffset">The byte offset of the physical item payload.</param>
    /// <param name="encodedIdentityHigh">The encoded identity high lane to persist.</param>
    /// <param name="encodedIdentityLow">The encoded identity low lane to persist.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteItemIdentity(Span<byte> target, int itemOffset, ulong encodedIdentityHigh, ulong encodedIdentityLow)
    {
        BinaryPrimitives.WriteUInt64BigEndian(target.Slice(itemOffset + ItemIdentityHighOffset, sizeof(ulong)), encodedIdentityHigh);
        BinaryPrimitives.WriteUInt64BigEndian(target.Slice(itemOffset + ItemIdentityLowOffset, sizeof(ulong)), encodedIdentityLow);
    }

    /// <summary>
    /// Writes a deterministic fixed 16-byte identity using zero for the high lane and the supplied value for the low lane.<br/>
    /// This overload is for deterministic 64-bit identity fixtures that still exercise the physical 16-byte identity layout.<br/>
    /// </summary>
    /// <param name="target">The writable shelf bytes.</param>
    /// <param name="itemOffset">The byte offset of the physical item payload.</param>
    /// <param name="encodedIdentityLow">The encoded identity low lane to persist.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteItemIdentity(Span<byte> target, int itemOffset, ulong encodedIdentityLow)
    {
        WriteItemIdentity(target, itemOffset, 0, encodedIdentityLow);
    }

    /// <summary>
    /// Compares a persisted item tuple to an encoded `(key0, key1, key2, key3, identityHigh, identityLow)` tuple.<br/>
    /// The comparison uses byte-order-preserving lanes over the persisted fixed bytes and does not decode developer-facing values in the hot path.<br/>
    /// </summary>
    /// <param name="source">The shelf bytes.</param>
    /// <param name="itemOffset">The byte offset of the physical item payload.</param>
    /// <param name="key0">The encoded sortable key part 0 to compare.</param>
    /// <param name="key1">The encoded sortable key part 1 to compare.</param>
    /// <param name="key2">The encoded sortable key part 2 to compare.</param>
    /// <param name="key3">The encoded sortable key part 3 to compare.</param>
    /// <param name="encodedIdentityHigh">The encoded identity high lane to compare.</param>
    /// <param name="encodedIdentityLow">The encoded identity low lane to compare.</param>
    /// <returns>A negative value, zero, or a positive value according to tuple order.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int CompareItemTuple(
        ReadOnlySpan<byte> source,
        int itemOffset,
        ulong key0,
        ulong key1,
        ulong key2,
        ulong key3,
        ulong encodedIdentityHigh,
        ulong encodedIdentityLow)
    {
        int keyComparison = CompareItemKey(source, itemOffset, key0, key1, key2, key3);
        if (keyComparison != 0)
        {
            return keyComparison;
        }

        ulong itemIdentityHigh = ReadItemIdentityHigh(source, itemOffset);
        if (itemIdentityHigh < encodedIdentityHigh)
        {
            return -1;
        }

        if (itemIdentityHigh > encodedIdentityHigh)
        {
            return 1;
        }

        ulong itemIdentityLow = ReadItemIdentityLow(source, itemOffset);
        if (itemIdentityLow < encodedIdentityLow)
        {
            return -1;
        }

        return itemIdentityLow > encodedIdentityLow ? 1 : 0;
    }

    /// <summary>
    /// Compares a persisted item tuple to a deterministic fixed 16-byte identity whose high lane is zero.<br/>
    /// This overload is for deterministic 64-bit identity fixtures that still exercise the physical 16-byte identity layout.<br/>
    /// </summary>
    /// <param name="source">The shelf bytes.</param>
    /// <param name="itemOffset">The byte offset of the physical item payload.</param>
    /// <param name="key0">The encoded sortable key part 0 to compare.</param>
    /// <param name="key1">The encoded sortable key part 1 to compare.</param>
    /// <param name="key2">The encoded sortable key part 2 to compare.</param>
    /// <param name="key3">The encoded sortable key part 3 to compare.</param>
    /// <param name="encodedIdentityLow">The encoded identity low lane to compare.</param>
    /// <returns>A negative value, zero, or a positive value according to tuple order.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int CompareItemTuple(
        ReadOnlySpan<byte> source,
        int itemOffset,
        ulong key0,
        ulong key1,
        ulong key2,
        ulong key3,
        ulong encodedIdentityLow)
    {
        return CompareItemTuple(source, itemOffset, key0, key1, key2, key3, 0, encodedIdentityLow);
    }

    /// <summary>
    /// Compares a persisted item key to an encoded 32-byte fixed key.<br/>
    /// Parts are compared in persisted order so the big-endian representation remains lexicographically sortable.<br/>
    /// </summary>
    /// <param name="source">The shelf bytes.</param>
    /// <param name="itemOffset">The byte offset of the physical item payload.</param>
    /// <param name="key0">The encoded sortable key part 0 to compare.</param>
    /// <param name="key1">The encoded sortable key part 1 to compare.</param>
    /// <param name="key2">The encoded sortable key part 2 to compare.</param>
    /// <param name="key3">The encoded sortable key part 3 to compare.</param>
    /// <returns>A negative value, zero, or a positive value according to key order.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int CompareItemKey(ReadOnlySpan<byte> source, int itemOffset, ulong key0, ulong key1, ulong key2, ulong key3)
    {
        ulong itemKey0 = ReadItemKeyPart0(source, itemOffset);
        if (itemKey0 < key0)
        {
            return -1;
        }

        if (itemKey0 > key0)
        {
            return 1;
        }

        ulong itemKey1 = ReadItemKeyPart1(source, itemOffset);
        if (itemKey1 < key1)
        {
            return -1;
        }

        if (itemKey1 > key1)
        {
            return 1;
        }

        ulong itemKey2 = ReadItemKeyPart2(source, itemOffset);
        if (itemKey2 < key2)
        {
            return -1;
        }

        if (itemKey2 > key2)
        {
            return 1;
        }

        ulong itemKey3 = ReadItemKeyPart3(source, itemOffset);
        if (itemKey3 < key3)
        {
            return -1;
        }

        return itemKey3 > key3 ? 1 : 0;
    }

    /// <summary>
    /// Encodes an unsigned 64-bit scalar into the low part of the canonical sortable 32-byte key representation.<br/>
    /// The leading parts are zero so early validation can use simple unsigned inputs while still exercising fixed 32-byte key storage.<br/>
    /// </summary>
    /// <param name="value">The unsigned scalar value.</param>
    /// <param name="key0">Receives encoded sortable key part 0.</param>
    /// <param name="key1">Receives encoded sortable key part 1.</param>
    /// <param name="key2">Receives encoded sortable key part 2.</param>
    /// <param name="key3">Receives encoded sortable key part 3.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void EncodeFixed32FromUInt64(ulong value, out ulong key0, out ulong key1, out ulong key2, out ulong key3)
    {
        key0 = 0;
        key1 = 0;
        key2 = 0;
        key3 = value;
    }

    /// <summary>
    /// Encodes an unsigned 64-bit value into the low lane of the canonical fixed 16-byte identity representation.<br/>
    /// The high lane is zero so validation can use simple unsigned inputs while still exercising 16-byte identity storage.<br/>
    /// </summary>
    /// <param name="value">The unsigned scalar value.</param>
    /// <param name="encodedHigh">Receives the encoded identity high lane.</param>
    /// <param name="encodedLow">Receives the encoded identity low lane.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void EncodeFixed16FromUInt64(ulong value, out ulong encodedHigh, out ulong encodedLow)
    {
        encodedHigh = 0;
        encodedLow = value;
    }

    /// <summary>
    /// Encodes an unsigned 64-bit value as the low lane of deterministic `FS32-16` validation data.<br/>
    /// The returned value is intended to be passed to overloads that use zero for the fixed 16-byte identity high lane.<br/>
    /// </summary>
    /// <param name="value">The unsigned scalar value.</param>
    /// <returns>The encoded identity low lane.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong EncodeUnsignedScalar8(ulong value)
    {
        return value;
    }
}
