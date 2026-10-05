using LibraDex;

internal static partial class RawHarness
{
    /// <summary>
    /// Proves bounded variable-blob creation, exact/null/empty condition execution, inequality through the identity-universe complement, metadata-driven reopen, and payload-cap validation.<br/>
    /// The fixture uses differently sized byte arrays so it cannot accidentally pass through the fixed-width 8/16/32-byte blob family.<br/>
    /// </summary>
    /// <param name="args">No additional arguments are accepted.<br/></param>
    /// <returns>Zero when all lifecycle and condition assertions pass.</returns>
    private static int RunVariableBlobApiSanity(string[] args)
    {
        string path = Path.Combine(Path.GetTempPath(), $"libradex-variable-blob-{Guid.NewGuid():N}.ldx");
        byte[] one = new byte[] { 0x10 };
        byte[] three = new byte[] { 0x10, 0x20, 0x30 };
        try
        {
            using (Catalog catalog = Catalog.Create(path))
            {
                LibraDexVariableBlobScalar8Index<ulong> index = catalog.Indexes.IndexSet("rows").Define("payload")
                    .Blob.Variable<ulong>(maxKeyBytes: 7)
                    .Create();
                _ = index.Insert(null, 1UL);
                _ = index.Insert(Array.Empty<byte>(), 2UL);
                _ = index.Insert(one, 3UL);
                _ = index.Insert(three, 4UL);

                IReadOnlyList<ulong> exact = catalog.Indexes.IndexSet("rows").GetIdentities<ulong>(
                    LibraDexCondition.ForGroup("rows").Index("payload").AsBinary.EqualTo(three).EndCondition,
                    deduplication: IdentityDeduplication.Preserve);
                IReadOnlyList<ulong> notExact = catalog.Indexes.IndexSet("rows").GetIdentities<ulong>(
                    LibraDexCondition.ForGroup("rows").Index("payload").AsBinary.NotEqualTo(three).EndCondition);
                IReadOnlyList<ulong> nulls = catalog.Indexes.IndexSet("rows").GetIdentities<ulong>(
                    LibraDexCondition.ForGroup("rows").Index("payload").AsBinary.EqualTo(NullKey.Null).EndCondition);
                IReadOnlyList<ulong> empties = catalog.Indexes.IndexSet("rows").GetIdentities<ulong>(
                    LibraDexCondition.ForGroup("rows").Index("payload").AsBinary.EqualTo(Array.Empty<byte>()).EndCondition);
                IReadOnlyList<LibraDexIndexEntry<byte[], ulong>> exactEntries = index.Entries.GetByKey(three);
                IReadOnlyList<ulong> slicedBytes = catalog.Indexes.IndexSet("rows").GetIdentities<ulong>(
                    LibraDexCondition.ForGroup("rows").Index("payload").AsBinary.SliceEqual(1, new byte[] { 0x20, 0x30 }).EndCondition);
                IReadOnlyList<ulong> slicedInt16 = catalog.Indexes.IndexSet("rows").GetIdentities<ulong>(
                    LibraDexCondition.ForGroup("rows").Index("payload").AsBinary.SlicedAsInt16(1).EqualTo(0x3020).EndCondition);

                if (!exact.SequenceEqual(new[] { 4UL }) ||
                    !notExact.OrderBy(static value => value).SequenceEqual(new[] { 1UL, 2UL, 3UL }) ||
                    !nulls.SequenceEqual(new[] { 1UL }) ||
                    !empties.SequenceEqual(new[] { 2UL }) ||
                    exactEntries.Count != 1 ||
                    !exactEntries[0].Key.AsSpan().SequenceEqual(three) ||
                    exactEntries[0].Identity != 4UL ||
                    !slicedBytes.SequenceEqual(new[] { 4UL }) ||
                    !slicedInt16.SequenceEqual(new[] { 4UL }) ||
                    index.Count() != 4 ||
                    index.MaxKeyBytes != 7)
                {
                    throw new InvalidDataException("Variable blob create-time exact, inequality, key-state, slice, count, or maximum-length semantics did not match the public contract.");
                }

                bool rejectedOversize = false;
                try
                {
                    _ = index.Insert(new byte[8], 5UL);
                }
                catch (ArgumentOutOfRangeException)
                {
                    rejectedOversize = true;
                }

                if (!rejectedOversize)
                    throw new InvalidDataException("Variable blob insertion did not reject a payload beyond the persisted logical byte cap.");
            }

            using (Catalog reopened = Catalog.Open(path))
            {
                LibraDexVariableBlobScalar8Index<ulong> index = reopened.Indexes.IndexSet("rows").Define("payload")
                    .Blob.Variable<ulong>(maxKeyBytes: 7)
                    .Open();
                IIndex metadataOpened = reopened.Indexes.IndexSet("rows").Define("payload").Open();
                IReadOnlyList<ulong> reopenedExact = reopened["rows"].GetIdentities<ulong>(
                    LibraDexCondition.ForGroup("rows").Index("payload").AsBinary.EqualTo(one).EndCondition);
                if (!reopenedExact.SequenceEqual(new[] { 3UL }) ||
                    metadataOpened is not LibraDexVariableBlobScalar8Index<ulong> ||
                    index.MaxKeyBytes != 7)
                {
                    throw new InvalidDataException("Variable blob reopen did not preserve exact lookup, public facade type, or logical payload cap.");
                }
            }

            Console.WriteLine("variable-blob-api-sanity ok");
            return 0;
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }
}
