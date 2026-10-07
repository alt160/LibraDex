using System.Buffers.Binary;
using System.Security.Cryptography;

namespace LibraDex.Layouts;

/// <summary>
/// Defines the version-two in-file structural-publication recovery-control page.<br/>
/// The fixed page follows the superblock and is never part of ordinary allocation, payload, publication, or retirement writes.<br/>
/// Two checksummed slots alternate by generation so a torn state transition leaves the preceding state independently readable.<br/>
/// </summary>
internal static class PublicationRecoveryLayout
{
    public const long PageOffset = SuperblockLayout.Size;
    public const int PageSize = 4096;
    public const int SlotSize = 128;
    public const int Slot0Offset = 0;
    public const int Slot1Offset = SlotSize;

    private const ulong Magic = 0x325256435844424CUL;
    private const int LayoutVersion = 1;
    private const int MagicOffset = 0;
    private const int VersionOffset = 8;
    private const int StateOffset = 12;
    private const int GenerationOffset = 16;
    private const int OriginalLengthOffset = 24;
    private const int TargetLengthOffset = 32;
    private const int JournalOffsetOffset = 40;
    private const int JournalLengthOffset = 48;
    private const int JournalHashOffset = 56;
    private const int JournalHashLength = 32;
    private const int SlotHashOffset = 88;
    private const int SlotHashLength = 32;

    /// <summary>
    /// Initializes a new version-two recovery-control page with one valid clean generation and one unused slot.<br/>
    /// The first protected publication therefore writes the alternate slot and never destroys the only valid prior state.<br/>
    /// </summary>
    /// <param name="page">The complete fixed recovery-control page.<br/></param>
    public static void InitializePage(Span<byte> page)
    {
        if (page.Length != PageSize)
            throw new ArgumentException($"A publication recovery page must contain exactly {PageSize} bytes.", nameof(page));

        page.Clear();
        WriteSlot(page.Slice(Slot0Offset, SlotSize), PublicationRecoveryState.Clean, 0, 0, 0, 0, 0, default);
    }

    /// <summary>
    /// Reads both state slots, validates each slot checksum, and returns the valid slot with the greatest generation.<br/>
    /// A false result means neither slot survived validation and recovery must fail closed.<br/>
    /// </summary>
    /// <param name="page">The complete fixed recovery-control page.<br/></param>
    /// <param name="slot">The newest valid recovery slot when successful.<br/></param>
    /// <returns>True when at least one independently valid slot exists.<br/></returns>
    public static bool TryReadLatest(ReadOnlySpan<byte> page, out PublicationRecoverySlot slot)
    {
        if (page.Length != PageSize)
            throw new ArgumentException($"A publication recovery page must contain exactly {PageSize} bytes.", nameof(page));

        bool valid0 = TryReadSlot(page.Slice(Slot0Offset, SlotSize), 0, out PublicationRecoverySlot slot0);
        bool valid1 = TryReadSlot(page.Slice(Slot1Offset, SlotSize), 1, out PublicationRecoverySlot slot1);
        if (!valid0 && !valid1)
        {
            slot = default;
            return false;
        }

        if (valid0 && valid1 && slot0.Generation == slot1.Generation)
        {
            slot = default;
            return false;
        }

        slot = !valid1 || (valid0 && slot0.Generation >= slot1.Generation) ? slot0 : slot1;
        return true;
    }

    /// <summary>
    /// Writes one complete checksummed state slot into caller-owned bytes.<br/>
    /// The caller chooses the inactive slot and durably flushes the positional write before relying on the transition.<br/>
    /// </summary>
    /// <param name="slot">The exact slot-sized destination.<br/></param>
    /// <param name="state">The recovery state being published.<br/></param>
    /// <param name="generation">The strictly increasing state generation.<br/></param>
    /// <param name="originalLength">The authoritative file length before the protected publication.<br/></param>
    /// <param name="targetLength">The authoritative file length after the protected publication.<br/></param>
    /// <param name="journalOffset">The temporary in-file redo-image offset.<br/></param>
    /// <param name="journalLength">The complete redo-image byte length.<br/></param>
    /// <param name="journalHash">The SHA-256 hash stored at the end of the redo image, or an empty span for a clean state.<br/></param>
    public static void WriteSlot(
        Span<byte> slot,
        PublicationRecoveryState state,
        long generation,
        long originalLength,
        long targetLength,
        long journalOffset,
        int journalLength,
        ReadOnlySpan<byte> journalHash)
    {
        if (slot.Length != SlotSize)
            throw new ArgumentException($"A publication recovery slot must contain exactly {SlotSize} bytes.", nameof(slot));
        if (!Enum.IsDefined(state))
            throw new ArgumentOutOfRangeException(nameof(state), state, "The publication recovery state is not defined.");
        if (generation < 0)
            throw new ArgumentOutOfRangeException(nameof(generation), generation, "A publication recovery generation cannot be negative.");
        if (state != PublicationRecoveryState.Clean && journalHash.Length != JournalHashLength)
            throw new ArgumentException($"A non-clean publication recovery slot requires a {JournalHashLength}-byte journal hash.", nameof(journalHash));

        slot.Clear();
        BinaryPrimitives.WriteUInt64LittleEndian(slot.Slice(MagicOffset), Magic);
        BinaryPrimitives.WriteInt32LittleEndian(slot.Slice(VersionOffset), LayoutVersion);
        BinaryPrimitives.WriteInt32LittleEndian(slot.Slice(StateOffset), (int)state);
        BinaryPrimitives.WriteInt64LittleEndian(slot.Slice(GenerationOffset), generation);
        BinaryPrimitives.WriteInt64LittleEndian(slot.Slice(OriginalLengthOffset), originalLength);
        BinaryPrimitives.WriteInt64LittleEndian(slot.Slice(TargetLengthOffset), targetLength);
        BinaryPrimitives.WriteInt64LittleEndian(slot.Slice(JournalOffsetOffset), journalOffset);
        BinaryPrimitives.WriteInt32LittleEndian(slot.Slice(JournalLengthOffset), journalLength);
        if (!journalHash.IsEmpty)
            journalHash.CopyTo(slot.Slice(JournalHashOffset, JournalHashLength));
        SHA256.HashData(slot.Slice(0, SlotHashOffset), slot.Slice(SlotHashOffset, SlotHashLength));
    }

    private static bool TryReadSlot(ReadOnlySpan<byte> bytes, int slotIndex, out PublicationRecoverySlot slot)
    {
        slot = default;
        if (BinaryPrimitives.ReadUInt64LittleEndian(bytes.Slice(MagicOffset)) != Magic ||
            BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(VersionOffset)) != LayoutVersion)
        {
            return false;
        }

        Span<byte> actualHash = stackalloc byte[SlotHashLength];
        SHA256.HashData(bytes.Slice(0, SlotHashOffset), actualHash);
        if (!CryptographicOperations.FixedTimeEquals(bytes.Slice(SlotHashOffset, SlotHashLength), actualHash))
            return false;

        int rawState = BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(StateOffset));
        if (!Enum.IsDefined(typeof(PublicationRecoveryState), rawState))
            return false;

        long generation = BinaryPrimitives.ReadInt64LittleEndian(bytes.Slice(GenerationOffset));
        if (generation < 0)
            return false;

        PublicationRecoveryState state = (PublicationRecoveryState)rawState;
        long originalLength = BinaryPrimitives.ReadInt64LittleEndian(bytes.Slice(OriginalLengthOffset));
        long targetLength = BinaryPrimitives.ReadInt64LittleEndian(bytes.Slice(TargetLengthOffset));
        long journalOffset = BinaryPrimitives.ReadInt64LittleEndian(bytes.Slice(JournalOffsetOffset));
        int journalLength = BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(JournalLengthOffset));
        ReadOnlySpan<byte> journalHash = bytes.Slice(JournalHashOffset, JournalHashLength);
        if (state == PublicationRecoveryState.Clean &&
            (originalLength != 0 || targetLength != 0 || journalOffset != 0 || journalLength != 0 || journalHash.IndexOfAnyExcept((byte)0) >= 0))
        {
            return false;
        }

        slot = new PublicationRecoverySlot(
            state,
            generation,
            originalLength,
            targetLength,
            journalOffset,
            journalLength,
            journalHash.ToArray(),
            slotIndex);
        return true;
    }
}

/// <summary>
/// Identifies the durable state of one protected version-two structural publication.<br/>
/// </summary>
internal enum PublicationRecoveryState
{
    Clean = 0,
    Preparing = 1,
    Active = 2,
    Complete = 3
}

/// <summary>
/// Represents one validated recovery-control slot selected from the fixed version-two control page.<br/>
/// </summary>
internal readonly record struct PublicationRecoverySlot(
    PublicationRecoveryState State,
    long Generation,
    long OriginalLength,
    long TargetLength,
    long JournalOffset,
    int JournalLength,
    byte[] JournalHash,
    int SlotIndex);
