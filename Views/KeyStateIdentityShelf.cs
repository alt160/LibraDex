using LibraDex.Layouts;

namespace LibraDex.Views;

/// <summary>
/// Provides a mutable projection over a compact identity-only key-state shelf.<br/>
/// The route owning the shelf supplies null-or-empty semantics, so this shelf keeps only sorted identities for fast count and enumeration.<br/>
/// </summary>
internal ref struct KeyStateIdentityShelf
{
    private Span<byte> bytes;

    public KeyStateIdentityShelf(Span<byte> bytes)
    {
        this.bytes = bytes;
    }

    public int ItemCount => KeyStateIdentityShelfLayout.ReadItemCount(bytes);

    public ushort IdentitySizeCode => KeyStateIdentityShelfLayout.ReadIdentitySizeCode(bytes);

    public bool IsScalar8Valid => AsReadOnly().IsScalar8Valid;

    public bool IsScalar16Valid => AsReadOnly().IsScalar16Valid;

    public void InitializeScalar8()
    {
        Initialize(KeyStateIdentityShelfLayout.Scalar8IdentityCode);
    }

    public void InitializeScalar16()
    {
        Initialize(KeyStateIdentityShelfLayout.Scalar16IdentityCode);
    }

    public bool InsertScalar8(ulong encodedIdentity)
    {
        if (!IsScalar8Valid)
        {
            throw new InvalidDataException("The key-state shelf is not a valid scalar-8 identity shelf.");
        }

        int count = ItemCount;
        if (count >= KeyStateIdentityShelfLayout.MaxScalar8ItemCount)
        {
            throw new InvalidOperationException("The key-state scalar-8 identity shelf is full.");
        }

        int insertIndex = AsReadOnly().LowerBoundScalar8(encodedIdentity);
        if (insertIndex < count && KeyStateIdentityShelfLayout.ReadScalar8Identity(bytes, insertIndex) == encodedIdentity)
        {
            return false;
        }

        if (insertIndex < count)
        {
            Span<byte> tail = bytes.Slice(
                KeyStateIdentityShelfLayout.GetScalar8IdentityOffset(insertIndex),
                checked((count - insertIndex) * KeyStateIdentityShelfLayout.Scalar8IdentitySize));
            tail.CopyTo(bytes.Slice(
                KeyStateIdentityShelfLayout.GetScalar8IdentityOffset(insertIndex + 1),
                tail.Length));
        }

        KeyStateIdentityShelfLayout.WriteScalar8Identity(bytes, insertIndex, encodedIdentity);
        KeyStateIdentityShelfLayout.WriteItemCount(bytes, count + 1);
        return true;
    }

    public bool InsertScalar16(ulong encodedIdentityHigh, ulong encodedIdentityLow)
    {
        if (!IsScalar16Valid)
        {
            throw new InvalidDataException("The key-state shelf is not a valid scalar-16 identity shelf.");
        }

        int count = ItemCount;
        if (count >= KeyStateIdentityShelfLayout.MaxScalar16ItemCount)
        {
            throw new InvalidOperationException("The key-state scalar-16 identity shelf is full.");
        }

        int insertIndex = AsReadOnly().LowerBoundScalar16(encodedIdentityHigh, encodedIdentityLow);
        if (insertIndex < count &&
            KeyStateIdentityShelfLayout.ReadScalar16IdentityHigh(bytes, insertIndex) == encodedIdentityHigh &&
            KeyStateIdentityShelfLayout.ReadScalar16IdentityLow(bytes, insertIndex) == encodedIdentityLow)
        {
            return false;
        }

        if (insertIndex < count)
        {
            Span<byte> tail = bytes.Slice(
                KeyStateIdentityShelfLayout.GetScalar16IdentityOffset(insertIndex),
                checked((count - insertIndex) * KeyStateIdentityShelfLayout.Scalar16IdentitySize));
            tail.CopyTo(bytes.Slice(
                KeyStateIdentityShelfLayout.GetScalar16IdentityOffset(insertIndex + 1),
                tail.Length));
        }

        KeyStateIdentityShelfLayout.WriteScalar16Identity(bytes, insertIndex, encodedIdentityHigh, encodedIdentityLow);
        KeyStateIdentityShelfLayout.WriteItemCount(bytes, count + 1);
        return true;
    }

    public KeyStateIdentityShelfReadOnly AsReadOnly()
    {
        return new KeyStateIdentityShelfReadOnly(bytes);
    }

    private void Initialize(ushort identitySizeCode)
    {
        bytes.Clear();
        KeyStateIdentityShelfLayout.WriteMagic(bytes, KeyStateIdentityShelfLayout.Magic);
        KeyStateIdentityShelfLayout.WriteFormatVersion(bytes, KeyStateIdentityShelfLayout.FormatVersion);
        KeyStateIdentityShelfLayout.WriteHeaderSize(bytes, KeyStateIdentityShelfLayout.HeaderSize);
        KeyStateIdentityShelfLayout.WriteFlags(bytes, 0);
        KeyStateIdentityShelfLayout.WriteItemCount(bytes, 0);
        KeyStateIdentityShelfLayout.WriteIdentitySizeCode(bytes, identitySizeCode);
    }
}

/// <summary>
/// Provides a read-only projection over a compact identity-only key-state shelf.<br/>
/// Read paths enumerate already-sorted identities and do not need key comparisons because the owning route selected the key state.<br/>
/// </summary>
internal readonly ref struct KeyStateIdentityShelfReadOnly
{
    private readonly ReadOnlySpan<byte> bytes;

    public KeyStateIdentityShelfReadOnly(ReadOnlySpan<byte> bytes)
    {
        this.bytes = bytes;
    }

    public uint Magic => KeyStateIdentityShelfLayout.ReadMagic(bytes);

    public ushort FormatVersion => KeyStateIdentityShelfLayout.ReadFormatVersion(bytes);

    public ushort HeaderSize => KeyStateIdentityShelfLayout.ReadHeaderSize(bytes);

    public int ItemCount => KeyStateIdentityShelfLayout.ReadItemCount(bytes);

    public ushort IdentitySizeCode => KeyStateIdentityShelfLayout.ReadIdentitySizeCode(bytes);

    public bool IsScalar8Valid =>
        bytes.Length >= KeyStateIdentityShelfLayout.ExtentSize &&
        Magic == KeyStateIdentityShelfLayout.Magic &&
        FormatVersion == KeyStateIdentityShelfLayout.FormatVersion &&
        HeaderSize == KeyStateIdentityShelfLayout.HeaderSize &&
        IdentitySizeCode == KeyStateIdentityShelfLayout.Scalar8IdentityCode &&
        ItemCount >= 0 &&
        ItemCount <= KeyStateIdentityShelfLayout.MaxScalar8ItemCount;

    public bool IsScalar16Valid =>
        bytes.Length >= KeyStateIdentityShelfLayout.ExtentSize &&
        Magic == KeyStateIdentityShelfLayout.Magic &&
        FormatVersion == KeyStateIdentityShelfLayout.FormatVersion &&
        HeaderSize == KeyStateIdentityShelfLayout.HeaderSize &&
        IdentitySizeCode == KeyStateIdentityShelfLayout.Scalar16IdentityCode &&
        ItemCount >= 0 &&
        ItemCount <= KeyStateIdentityShelfLayout.MaxScalar16ItemCount;

    public int LowerBoundScalar8(ulong encodedIdentity)
    {
        int low = 0;
        int high = ItemCount;
        ReadOnlySpan<byte> localBytes = bytes;
        while (low < high)
        {
            int middle = low + ((high - low) >> 1);
            ulong current = KeyStateIdentityShelfLayout.ReadScalar8Identity(localBytes, middle);
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

    public int LowerBoundScalar16(ulong encodedIdentityHigh, ulong encodedIdentityLow)
    {
        int low = 0;
        int high = ItemCount;
        ReadOnlySpan<byte> localBytes = bytes;
        while (low < high)
        {
            int middle = low + ((high - low) >> 1);
            ulong currentHigh = KeyStateIdentityShelfLayout.ReadScalar16IdentityHigh(localBytes, middle);
            ulong currentLow = KeyStateIdentityShelfLayout.ReadScalar16IdentityLow(localBytes, middle);
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

    public int CopyScalar8Identities(Span<ulong> encodedIdentities)
    {
        if (!IsScalar8Valid)
        {
            throw new InvalidDataException("The key-state shelf is not a valid scalar-8 identity shelf.");
        }

        int count = ItemCount;
        if (encodedIdentities.Length < count)
        {
            throw new ArgumentException("The identity output span is too small for the key-state shelf.", nameof(encodedIdentities));
        }

        for (int i = 0; i < count; i++)
        {
            encodedIdentities[i] = KeyStateIdentityShelfLayout.ReadScalar8Identity(bytes, i);
        }

        return count;
    }

    public int CopyScalar16Identities(Span<ulong> encodedIdentityHighs, Span<ulong> encodedIdentityLows)
    {
        if (!IsScalar16Valid)
        {
            throw new InvalidDataException("The key-state shelf is not a valid scalar-16 identity shelf.");
        }

        int count = ItemCount;
        if (encodedIdentityHighs.Length < count || encodedIdentityLows.Length < count)
        {
            throw new ArgumentException("The identity output spans are too small for the key-state shelf.");
        }

        for (int i = 0; i < count; i++)
        {
            encodedIdentityHighs[i] = KeyStateIdentityShelfLayout.ReadScalar16IdentityHigh(bytes, i);
            encodedIdentityLows[i] = KeyStateIdentityShelfLayout.ReadScalar16IdentityLow(bytes, i);
        }

        return count;
    }
}
