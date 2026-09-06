using LibraDex;

internal static partial class RawHarness
{
    /// <summary>
    /// Proves nonexecuting CLR path checks, canonical method spelling, cached typed execution, key/identity projection, and ordinary-index mutation convenience.<br/>
    /// </summary>
    /// <param name="args">Command arguments; this validation currently has no options.<br/></param>
    /// <returns>Zero when the standalone value-path contract retains exact typed and index behavior.<br/></returns>
    private static int RunValuePathSanity(string[] args)
    {
        _ = args;
        LibraDexValuePathCheck lengthCheck = LibraDexValuePath.Check<ValuePathCustomer, int>("CustomerName.Length");
        if (!lengthCheck.IsValid ||
            lengthCheck.CanonicalPath != ".CustomerName.Length" ||
            lengthCheck.ResultType != typeof(int) ||
            lengthCheck.ContainsMethod)
        {
            throw new InvalidDataException("String.Length did not produce the expected standalone CLR value-path check.");
        }

        LibraDexValuePathCheck methodCheck = LibraDexValuePath.Check<ValuePathCustomer, int>(".Number.ToString.Length()");
        if (!methodCheck.IsValid ||
            methodCheck.CanonicalPath != ".Number.ToString().Length" ||
            !methodCheck.ContainsMethod)
        {
            throw new InvalidDataException("Optional method parentheses did not canonicalize to one standalone CLR value path.");
        }

        LibraDexValuePathCheck blockedCheck = LibraDexValuePath.Check<ValuePathCustomer, object>(".Tags.Clear()");
        if (blockedCheck.IsValid || blockedCheck.FailureKind != LibraDexValuePathFailureKind.BlockedMethod)
            throw new InvalidDataException("A void CLR method was not rejected by the standalone value-path policy.");

        LibraDexValuePathCheck mismatchCheck = LibraDexValuePath.Check<ValuePathCustomer, long>(".CustomerName.Length");
        if (mismatchCheck.IsValid || mismatchCheck.FailureKind != LibraDexValuePathFailureKind.ResultTypeMismatch)
            throw new InvalidDataException("An incompatible requested result type was not rejected before value-path execution.");

        LibraDexValuePath<ValuePathCustomer, int> length = LibraDexValuePath.Create<ValuePathCustomer, int>(".CustomerName.Length");
        LibraDexValuePath<ValuePathCustomer, int> cached = LibraDexValuePath.Create<ValuePathCustomer, int>("CustomerName.Length");
        if (!ReferenceEquals(length, cached))
            throw new InvalidDataException("Canonical-equivalent value paths did not reuse one compiled typed plan.");

        var alpha = new ValuePathCustomer { CustomerName = "Alpha", Number = 123, Tags = ["one"] };
        var northwind = new ValuePathCustomer { CustomerName = "Northwind", Number = 7, Tags = ["two"] };
        if (length.Get(alpha) != 5 ||
            LibraDexValuePath.Create<ValuePathCustomer, int>(".Number.ToString().Length").Get(alpha) != 3)
        {
            throw new InvalidDataException("Compiled standalone value paths returned the wrong CLR values.");
        }

        var customers = new Dictionary<ulong, ValuePathCustomer>
        {
            [101UL] = alpha,
            [102UL] = northwind
        };
        LibraDexValueProjection<ulong, ValuePathCustomer, int> identityLength = length.From<ulong>(id => customers[id]);
        if (identityLength.Get(102UL) != 9)
            throw new InvalidDataException("Identity-to-object projection did not feed the cached CLR member path.");

        using Catalog catalog = Catalog.CreateMemory(CatalogOptions.UInt64Identities);
        using LibraDexIndex<int, ulong> index = catalog.Indexes["customers"]["name-length"].Int32Keys<ulong>().Create();
        if (!index.AddFrom(length, alpha, 101UL).Inserted ||
            !index.AddFrom(identityLength, 102UL, 102UL).Inserted)
        {
            throw new InvalidDataException("Ordinary index AddFrom did not insert both derived keys.");
        }

        LibraDexConditionEndCondition nine = LibraDexCondition.ForGroup("customers")
            .Index("name-length").AsInt32.EqualTo(9).EndCondition;
        if (!index.GetIdentities(nine).SequenceEqual([102UL]))
            throw new InvalidDataException("The ordinary derived-key index did not return the expected identity.");

        index.DeleteFrom(length, alpha, 101UL);
        LibraDexConditionEndCondition five = LibraDexCondition.ForGroup("customers")
            .Index("name-length").AsInt32.EqualTo(5).EndCondition;
        if (index.GetIdentities(five).Any())
            throw new InvalidDataException("Ordinary index DeleteFrom did not remove the exact derived tuple.");

        Console.WriteLine("value-path-sanity ok");
        return 0;
    }

    private sealed class ValuePathCustomer
    {
        public string CustomerName { get; init; } = string.Empty;
        public int Number { get; init; }
        public List<string> Tags { get; init; } = new();
    }
}
