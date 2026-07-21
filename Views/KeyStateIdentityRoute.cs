using LibraDex.Layouts;

namespace LibraDex.Views;

/// <summary>
/// Provides a mutable projection over a null-or-empty key-state identity route root.<br/>
/// The current storage kind is inline sorted identities; the root header reserves a child-root pointer so the same metadata offset can later reshape into ranged or routed identity storage.<br/>
/// </summary>
internal ref struct KeyStateIdentityRoute
{
    private Span<byte> bytes;

    public KeyStateIdentityRoute(Span<byte> bytes)
    {
        this.bytes = bytes;
    }

    public int ItemCount => KeyStateIdentityRouteLayout.ReadItemCount(bytes);

    public ushort StorageKind => KeyStateIdentityRouteLayout.ReadStorageKind(bytes);

    public ushort IdentitySizeCode => KeyStateIdentityRouteLayout.ReadIdentitySizeCode(bytes);

    public bool IsScalar8InlineValid => AsReadOnly().IsScalar8InlineValid;

    public bool IsScalar8TerminalIdentityRootValid => AsReadOnly().IsScalar8TerminalIdentityRootValid;

    public bool IsScalar16InlineValid => AsReadOnly().IsScalar16InlineValid;

    public bool IsScalar16TerminalIdentityRootValid => AsReadOnly().IsScalar16TerminalIdentityRootValid;

    public void InitializeScalar8Inline()
    {
        InitializeInline(KeyStateIdentityRouteLayout.Scalar8IdentityCode);
    }

    public void InitializeScalar16Inline()
    {
        InitializeInline(KeyStateIdentityRouteLayout.Scalar16IdentityCode);
    }

    public void InitializeScalar8TerminalIdentityRoot(long childRootOffset, int itemCount)
    {
        InitializeTerminalIdentityRoot(KeyStateIdentityRouteLayout.Scalar8IdentityCode, childRootOffset, itemCount);
    }

    public void InitializeScalar16TerminalIdentityRoot(long childRootOffset, int itemCount)
    {
        InitializeTerminalIdentityRoot(KeyStateIdentityRouteLayout.Scalar16IdentityCode, childRootOffset, itemCount);
    }

    public bool InsertScalar8(ulong encodedIdentity)
    {
        if (!IsScalar8InlineValid)
        {
            throw new InvalidDataException("The key-state identity route is not a valid inline scalar-8 route.");
        }

        int count = ItemCount;
        if (count >= KeyStateIdentityRouteLayout.MaxScalar8InlineItemCount)
        {
            throw new InvalidOperationException("The key-state inline scalar-8 identity route is full; route promotion is not connected yet.");
        }

        int insertIndex = AsReadOnly().LowerBoundScalar8(encodedIdentity);
        if (insertIndex < count && KeyStateIdentityRouteLayout.ReadScalar8Identity(bytes, insertIndex) == encodedIdentity)
        {
            return false;
        }

        if (insertIndex < count)
        {
            Span<byte> tail = bytes.Slice(
                KeyStateIdentityRouteLayout.GetScalar8IdentityOffset(insertIndex),
                checked((count - insertIndex) * KeyStateIdentityRouteLayout.Scalar8IdentitySize));
            tail.CopyTo(bytes.Slice(
                KeyStateIdentityRouteLayout.GetScalar8IdentityOffset(insertIndex + 1),
                tail.Length));
        }

        KeyStateIdentityRouteLayout.WriteScalar8Identity(bytes, insertIndex, encodedIdentity);
        KeyStateIdentityRouteLayout.WriteItemCount(bytes, count + 1);
        return true;
    }

    public bool DeleteScalar8(ulong encodedIdentity)
    {
        if (!IsScalar8InlineValid)
        {
            throw new InvalidDataException("The key-state identity route is not a valid inline scalar-8 route.");
        }

        int count = ItemCount;
        int index = AsReadOnly().LowerBoundScalar8(encodedIdentity);
        if (index >= count || KeyStateIdentityRouteLayout.ReadScalar8Identity(bytes, index) != encodedIdentity)
        {
            return false;
        }

        if (index + 1 < count)
        {
            ReadOnlySpan<byte> tail = bytes.Slice(
                KeyStateIdentityRouteLayout.GetScalar8IdentityOffset(index + 1),
                checked((count - index - 1) * KeyStateIdentityRouteLayout.Scalar8IdentitySize));
            tail.CopyTo(bytes.Slice(
                KeyStateIdentityRouteLayout.GetScalar8IdentityOffset(index),
                tail.Length));
        }

        KeyStateIdentityRouteLayout.WriteItemCount(bytes, count - 1);
        return true;
    }

    public bool InsertScalar16(ulong encodedIdentityHigh, ulong encodedIdentityLow)
    {
        if (!IsScalar16InlineValid)
        {
            throw new InvalidDataException("The key-state identity route is not a valid inline scalar-16 route.");
        }

        int count = ItemCount;
        if (count >= KeyStateIdentityRouteLayout.MaxScalar16InlineItemCount)
        {
            throw new InvalidOperationException("The key-state inline scalar-16 identity route is full; route promotion is not connected yet.");
        }

        int insertIndex = AsReadOnly().LowerBoundScalar16(encodedIdentityHigh, encodedIdentityLow);
        if (insertIndex < count &&
            KeyStateIdentityRouteLayout.ReadScalar16IdentityHigh(bytes, insertIndex) == encodedIdentityHigh &&
            KeyStateIdentityRouteLayout.ReadScalar16IdentityLow(bytes, insertIndex) == encodedIdentityLow)
        {
            return false;
        }

        if (insertIndex < count)
        {
            Span<byte> tail = bytes.Slice(
                KeyStateIdentityRouteLayout.GetScalar16IdentityOffset(insertIndex),
                checked((count - insertIndex) * KeyStateIdentityRouteLayout.Scalar16IdentitySize));
            tail.CopyTo(bytes.Slice(
                KeyStateIdentityRouteLayout.GetScalar16IdentityOffset(insertIndex + 1),
                tail.Length));
        }

        KeyStateIdentityRouteLayout.WriteScalar16Identity(bytes, insertIndex, encodedIdentityHigh, encodedIdentityLow);
        KeyStateIdentityRouteLayout.WriteItemCount(bytes, count + 1);
        return true;
    }

    public bool DeleteScalar16(ulong encodedIdentityHigh, ulong encodedIdentityLow)
    {
        if (!IsScalar16InlineValid)
        {
            throw new InvalidDataException("The key-state identity route is not a valid inline scalar-16 route.");
        }

        int count = ItemCount;
        int index = AsReadOnly().LowerBoundScalar16(encodedIdentityHigh, encodedIdentityLow);
        if (index >= count ||
            KeyStateIdentityRouteLayout.ReadScalar16IdentityHigh(bytes, index) != encodedIdentityHigh ||
            KeyStateIdentityRouteLayout.ReadScalar16IdentityLow(bytes, index) != encodedIdentityLow)
        {
            return false;
        }

        if (index + 1 < count)
        {
            ReadOnlySpan<byte> tail = bytes.Slice(
                KeyStateIdentityRouteLayout.GetScalar16IdentityOffset(index + 1),
                checked((count - index - 1) * KeyStateIdentityRouteLayout.Scalar16IdentitySize));
            tail.CopyTo(bytes.Slice(
                KeyStateIdentityRouteLayout.GetScalar16IdentityOffset(index),
                tail.Length));
        }

        KeyStateIdentityRouteLayout.WriteItemCount(bytes, count - 1);
        return true;
    }

    public KeyStateIdentityRouteReadOnly AsReadOnly()
    {
        return new KeyStateIdentityRouteReadOnly(bytes);
    }

    private void InitializeInline(ushort identitySizeCode)
    {
        bytes.Clear();
        KeyStateIdentityRouteLayout.WriteMagic(bytes, KeyStateIdentityRouteLayout.Magic);
        KeyStateIdentityRouteLayout.WriteFormatVersion(bytes, KeyStateIdentityRouteLayout.FormatVersion);
        KeyStateIdentityRouteLayout.WriteHeaderSize(bytes, KeyStateIdentityRouteLayout.HeaderSize);
        KeyStateIdentityRouteLayout.WriteStorageKind(bytes, KeyStateIdentityRouteLayout.InlineSortedStorageKind);
        KeyStateIdentityRouteLayout.WriteFlags(bytes, 0);
        KeyStateIdentityRouteLayout.WriteItemCount(bytes, 0);
        KeyStateIdentityRouteLayout.WriteIdentitySizeCode(bytes, identitySizeCode);
        KeyStateIdentityRouteLayout.WriteChildRootOffset(bytes, 0);
    }

    private void InitializeTerminalIdentityRoot(ushort identitySizeCode, long childRootOffset, int itemCount)
    {
        if (childRootOffset <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(childRootOffset), childRootOffset, "The promoted key-state child root offset must be positive.");
        }

        if (itemCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(itemCount), itemCount, "The promoted key-state item count cannot be negative.");
        }

        bytes.Clear();
        KeyStateIdentityRouteLayout.WriteMagic(bytes, KeyStateIdentityRouteLayout.Magic);
        KeyStateIdentityRouteLayout.WriteFormatVersion(bytes, KeyStateIdentityRouteLayout.FormatVersion);
        KeyStateIdentityRouteLayout.WriteHeaderSize(bytes, KeyStateIdentityRouteLayout.HeaderSize);
        KeyStateIdentityRouteLayout.WriteStorageKind(bytes, KeyStateIdentityRouteLayout.TerminalIdentityRootStorageKind);
        KeyStateIdentityRouteLayout.WriteFlags(bytes, 0);
        KeyStateIdentityRouteLayout.WriteItemCount(bytes, itemCount);
        KeyStateIdentityRouteLayout.WriteIdentitySizeCode(bytes, identitySizeCode);
        KeyStateIdentityRouteLayout.WriteChildRootOffset(bytes, childRootOffset);
    }
}

/// <summary>
/// Provides a read-only projection over a null-or-empty key-state identity route root.<br/>
/// Inline roots enumerate already-sorted identities and support binary-search exact identity tests; future storage kinds should preserve the same public route semantics.<br/>
/// </summary>
internal readonly ref struct KeyStateIdentityRouteReadOnly
{
    private readonly ReadOnlySpan<byte> bytes;

    public KeyStateIdentityRouteReadOnly(ReadOnlySpan<byte> bytes)
    {
        this.bytes = bytes;
    }

    public uint Magic => KeyStateIdentityRouteLayout.ReadMagic(bytes);

    public ushort FormatVersion => KeyStateIdentityRouteLayout.ReadFormatVersion(bytes);

    public ushort HeaderSize => KeyStateIdentityRouteLayout.ReadHeaderSize(bytes);

    public ushort StorageKind => KeyStateIdentityRouteLayout.ReadStorageKind(bytes);

    public int ItemCount => KeyStateIdentityRouteLayout.ReadItemCount(bytes);

    public ushort IdentitySizeCode => KeyStateIdentityRouteLayout.ReadIdentitySizeCode(bytes);

    public long ChildRootOffset => KeyStateIdentityRouteLayout.ReadChildRootOffset(bytes);

    public bool IsScalar8InlineValid =>
        bytes.Length >= KeyStateIdentityRouteLayout.ExtentSize &&
        Magic == KeyStateIdentityRouteLayout.Magic &&
        FormatVersion == KeyStateIdentityRouteLayout.FormatVersion &&
        HeaderSize == KeyStateIdentityRouteLayout.HeaderSize &&
        StorageKind == KeyStateIdentityRouteLayout.InlineSortedStorageKind &&
        IdentitySizeCode == KeyStateIdentityRouteLayout.Scalar8IdentityCode &&
        ChildRootOffset == 0 &&
        ItemCount >= 0 &&
        ItemCount <= KeyStateIdentityRouteLayout.MaxScalar8InlineItemCount;

    public bool IsScalar16InlineValid =>
        bytes.Length >= KeyStateIdentityRouteLayout.ExtentSize &&
        Magic == KeyStateIdentityRouteLayout.Magic &&
        FormatVersion == KeyStateIdentityRouteLayout.FormatVersion &&
        HeaderSize == KeyStateIdentityRouteLayout.HeaderSize &&
        StorageKind == KeyStateIdentityRouteLayout.InlineSortedStorageKind &&
        IdentitySizeCode == KeyStateIdentityRouteLayout.Scalar16IdentityCode &&
        ChildRootOffset == 0 &&
        ItemCount >= 0 &&
        ItemCount <= KeyStateIdentityRouteLayout.MaxScalar16InlineItemCount;

    public bool IsScalar8TerminalIdentityRootValid =>
        bytes.Length >= KeyStateIdentityRouteLayout.ExtentSize &&
        Magic == KeyStateIdentityRouteLayout.Magic &&
        FormatVersion == KeyStateIdentityRouteLayout.FormatVersion &&
        HeaderSize == KeyStateIdentityRouteLayout.HeaderSize &&
        StorageKind == KeyStateIdentityRouteLayout.TerminalIdentityRootStorageKind &&
        IdentitySizeCode == KeyStateIdentityRouteLayout.Scalar8IdentityCode &&
        ChildRootOffset > 0 &&
        ItemCount >= 0;

    public bool IsScalar16TerminalIdentityRootValid =>
        bytes.Length >= KeyStateIdentityRouteLayout.ExtentSize &&
        Magic == KeyStateIdentityRouteLayout.Magic &&
        FormatVersion == KeyStateIdentityRouteLayout.FormatVersion &&
        HeaderSize == KeyStateIdentityRouteLayout.HeaderSize &&
        StorageKind == KeyStateIdentityRouteLayout.TerminalIdentityRootStorageKind &&
        IdentitySizeCode == KeyStateIdentityRouteLayout.Scalar16IdentityCode &&
        ChildRootOffset > 0 &&
        ItemCount >= 0;

    public int LowerBoundScalar8(ulong encodedIdentity)
    {
        int low = 0;
        int high = ItemCount;
        ReadOnlySpan<byte> localBytes = bytes;
        while (low < high)
        {
            int middle = low + ((high - low) >> 1);
            ulong current = KeyStateIdentityRouteLayout.ReadScalar8Identity(localBytes, middle);
            if (current < encodedIdentity)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        return low;
    }

    public bool ContainsScalar8(ulong encodedIdentity)
    {
        if (!IsScalar8InlineValid)
        {
            throw new InvalidDataException("The key-state identity route is not a valid inline scalar-8 route.");
        }

        int index = LowerBoundScalar8(encodedIdentity);
        return index < ItemCount && KeyStateIdentityRouteLayout.ReadScalar8Identity(bytes, index) == encodedIdentity;
    }

    public int LowerBoundScalar16(ulong encodedIdentityHigh, ulong encodedIdentityLow)
    {
        int low = 0;
        int high = ItemCount;
        ReadOnlySpan<byte> localBytes = bytes;
        while (low < high)
        {
            int middle = low + ((high - low) >> 1);
            ulong currentHigh = KeyStateIdentityRouteLayout.ReadScalar16IdentityHigh(localBytes, middle);
            ulong currentLow = KeyStateIdentityRouteLayout.ReadScalar16IdentityLow(localBytes, middle);
            if (currentHigh < encodedIdentityHigh ||
                (currentHigh == encodedIdentityHigh && currentLow < encodedIdentityLow))
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        return low;
    }

    public bool ContainsScalar16(ulong encodedIdentityHigh, ulong encodedIdentityLow)
    {
        if (!IsScalar16InlineValid)
        {
            throw new InvalidDataException("The key-state identity route is not a valid inline scalar-16 route.");
        }

        int index = LowerBoundScalar16(encodedIdentityHigh, encodedIdentityLow);
        return index < ItemCount &&
            KeyStateIdentityRouteLayout.ReadScalar16IdentityHigh(bytes, index) == encodedIdentityHigh &&
            KeyStateIdentityRouteLayout.ReadScalar16IdentityLow(bytes, index) == encodedIdentityLow;
    }

    public int CopyScalar8Identities(Span<ulong> encodedIdentities)
    {
        if (!IsScalar8InlineValid)
        {
            throw new InvalidDataException("The key-state identity route is not a valid inline scalar-8 route.");
        }

        int count = ItemCount;
        if (encodedIdentities.Length < count)
        {
            throw new ArgumentException("The identity output span is too small for the key-state identity route.", nameof(encodedIdentities));
        }

        for (int i = 0; i < count; i++)
        {
            encodedIdentities[i] = KeyStateIdentityRouteLayout.ReadScalar8Identity(bytes, i);
        }

        return count;
    }

    public int CopyScalar16Identities(Span<ulong> encodedIdentityHighs, Span<ulong> encodedIdentityLows)
    {
        if (!IsScalar16InlineValid)
        {
            throw new InvalidDataException("The key-state identity route is not a valid inline scalar-16 route.");
        }

        int count = ItemCount;
        if (encodedIdentityHighs.Length < count || encodedIdentityLows.Length < count)
        {
            throw new ArgumentException("The identity output spans are too small for the key-state identity route.");
        }

        for (int i = 0; i < count; i++)
        {
            encodedIdentityHighs[i] = KeyStateIdentityRouteLayout.ReadScalar16IdentityHigh(bytes, i);
            encodedIdentityLows[i] = KeyStateIdentityRouteLayout.ReadScalar16IdentityLow(bytes, i);
        }

        return count;
    }
}
