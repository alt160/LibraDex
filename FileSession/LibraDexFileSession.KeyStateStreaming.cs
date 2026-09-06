using System.Buffers.Binary;
using LibraDex.Layouts;
using LibraDex.Views;

namespace LibraDex;

internal sealed partial class LibraDexFileSession
{
    /// <summary>
    /// Streams encoded scalar-eight identities from an inline or promoted key-state route.<br/>
    /// Captures one inline page or visits terminal shelves as needed, without creating an identity array.<br/>
    /// The caller must retain the session and honor its existing read/write coordination contract.<br/>
    /// </summary>
    /// <param name="slotIndex">Owning index directory slot.<br/></param>
    /// <param name="route">Concrete null or empty route.<br/></param>
    /// <returns>Encoded identities in the route's existing order.<br/></returns>
    internal IEnumerable<ulong> IterateScalar8KeyStateIdentities(int slotIndex, KeyStateRoute route)
    {
        long offset = GetKeyStateRouteOffset(slotIndex, route, out _, out _);
        if (offset <= 0) yield break;
        byte[] page = ReadKeyStateRouteBytes(offset);
        if (new KeyStateIdentityRouteReadOnly(page).IsScalar8TerminalIdentityRootValid)
        {
            long rootOffset = new KeyStateIdentityRouteReadOnly(page).ChildRootOffset;
            byte[] root = ReadTerminalIdentityRootBytes(rootOffset);
            if (!IsTerminalIdentityRootForKey(root, CreateKeyStateTerminalIdentityKeyBytes(route), out long shelfOffset))
                throw new InvalidDataException("The terminal identity root does not match the key-state route.");
            while (shelfOffset != 0)
            {
                byte[] shelf = ReadTerminalIdentity8ShelfBytesCached(shelfOffset, KeyStateIdentityRouteLayout.ExtentSize);
                ValidateTerminalIdentity8Shelf(shelf, KeyStateIdentityRouteLayout.ExtentSize);
                int count = TerminalIdentity8ShelfLayout.ReadItemCount(shelf);
                for (int i = 0; i < count; i++)
                    yield return TerminalIdentity8ShelfLayout.ReadIdentity(shelf, i);
                shelfOffset = TerminalIdentity8ShelfLayout.ReadNextShelfOffset(shelf);
            }
            yield break;
        }
        if (!new KeyStateIdentityRouteReadOnly(page).IsScalar8InlineValid)
            throw new InvalidDataException("The key-state route is not a valid scalar-eight route.");
        int inlineCount = new KeyStateIdentityRouteReadOnly(page).ItemCount;
        for (int i = 0; i < inlineCount; i++)
            yield return KeyStateIdentityRouteLayout.ReadScalar8Identity(page, i);
    }

    /// <summary>
    /// Streams encoded scalar-sixteen identities from inline or promoted key-state routes.<br/>
    /// Promoted routes decode one identity from the current shelf instead of building parallel high/low arrays.<br/>
    /// This mirrors scalar-eight traversal and preserves fixed-sixteen key-state validation.<br/>
    /// </summary>
    /// <param name="slotIndex">Owning index directory slot.<br/></param>
    /// <param name="route">Concrete null or empty route.<br/></param>
    /// <returns>Encoded identity halves in route order.<br/></returns>
    internal IEnumerable<(ulong High, ulong Low)> IterateScalar16KeyStateIdentities(int slotIndex, KeyStateRoute route)
    {
        long offset = GetKeyStateRouteOffset(slotIndex, route, out _, out _);
        if (offset <= 0) yield break;
        byte[] page = ReadKeyStateRouteBytes(offset);
        if (new KeyStateIdentityRouteReadOnly(page).IsScalar16TerminalIdentityRootValid)
        {
            byte[] root = ReadTerminalIdentityRootBytes(new KeyStateIdentityRouteReadOnly(page).ChildRootOffset);
            if (TerminalIdentityRootLayout.ReadShape(root) != TerminalIdentityRootLayout.ShapeScalar16VarIdentity ||
                !IsTerminalIdentityRootForKey(root, CreateScalar16KeyStateTerminalIdentityKeyBytes(route), out long shelfOffset))
                throw new InvalidDataException("The promoted scalar-sixteen key-state root is invalid.");
            int extent = TerminalIdentityRootLayout.ReadShelfExtentSize(root);
            while (shelfOffset != 0)
            {
                byte[] shelf = ReadTerminalVarIdentityShelfBytesCached(shelfOffset, extent);
                TerminalVarIdentityShelfLayout.Validate(shelf, extent);
                int count = TerminalVarIdentityShelfLayout.ReadItemCount(shelf);
                for (int i = 0; i < count; i++)
                    yield return DecodeScalar16KeyStateIdentity(shelf, i);
                shelfOffset = TerminalVarIdentityShelfLayout.ReadNextShelfOffset(shelf);
            }
            yield break;
        }
        if (!new KeyStateIdentityRouteReadOnly(page).IsScalar16InlineValid)
            throw new InvalidDataException("The key-state route is not a valid scalar-sixteen route.");
        int inlineCount = new KeyStateIdentityRouteReadOnly(page).ItemCount;
        for (int i = 0; i < inlineCount; i++)
            yield return (KeyStateIdentityRouteLayout.ReadScalar16IdentityHigh(page, i),
                KeyStateIdentityRouteLayout.ReadScalar16IdentityLow(page, i));
    }

    /// <summary>Validates and decodes one promoted fixed-sixteen identity without allocating a byte array.<br/></summary>
    /// <param name="shelf">Current validated variable-identity shelf.<br/></param>
    /// <param name="slot">Identity slot within that shelf.<br/></param>
    /// <returns>Sortable high/low identity halves.<br/></returns>
    private static (ulong High, ulong Low) DecodeScalar16KeyStateIdentity(byte[] shelf, int slot)
    {
        ReadOnlySpan<byte> identity = TerminalVarIdentityShelfLayout.ReadIdentityAt(shelf, slot);
        if (identity.Length != KeyStateIdentityRouteLayout.Scalar16IdentitySize)
            throw new InvalidDataException("The promoted scalar-sixteen key-state identity has an invalid length.");
        return (BinaryPrimitives.ReadUInt64BigEndian(identity), BinaryPrimitives.ReadUInt64BigEndian(identity.Slice(sizeof(ulong))));
    }
}
