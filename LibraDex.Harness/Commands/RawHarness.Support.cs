using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Text;
using LibraDex;
using LibraDex.Layouts;
using LibraDex.Views;
using Microsoft.Data.Sqlite;

internal static partial class RawHarness
{

    /// <summary>
    /// Validates deterministic composition and terminal rows over adopted conditions.<br/>
    /// The fixture keeps index sizing out of query semantics and proves that cross-index composition, composite parts, paging/take, existence, count, and grouping terminals materialize expected identities through opened index actors.<br/>
    /// </summary>
    private static void ValidateDeterministicCompositionTerminalCoverage()
    {
        static void AssertSet<T>(IEnumerable<T> actual, IEnumerable<T> expected, string context)
        {
            T[] actualOrdered = actual.OrderBy(static value => value).ToArray();
            T[] expectedOrdered = expected.OrderBy(static value => value).ToArray();
            if (!actualOrdered.SequenceEqual(expectedOrdered))
            {
                throw new InvalidDataException($"{context} expected set [{string.Join(", ", expectedOrdered)}] but returned [{string.Join(", ", actualOrdered)}].");
            }
        }

        static void AssertSequence<T>(IEnumerable<T> actual, IEnumerable<T> expected, string context)
        {
            T[] actualArray = actual.ToArray();
            T[] expectedArray = expected.ToArray();
            if (!actualArray.SequenceEqual(expectedArray))
            {
                throw new InvalidDataException($"{context} expected sequence [{string.Join(", ", expectedArray)}] but returned [{string.Join(", ", actualArray)}].");
            }
        }

        static IReadOnlyList<ulong> UIDs(LibraDexConditionEndCondition condition, Func<string, IIndex> resolve)
        {
            return condition.ToList<ulong>(resolve, deduplication: IdentityDeduplication.Preserve);
        }

        using Catalog catalog = Catalog.CreateMemory();

        LibraDexIndex<int, ulong> age = catalog.Indexes["users"]["age"].Int32Keys<ulong>().Create();
        LibraDexIndex<int, ulong> status = catalog.Indexes["users"]["status"].Int32Keys<ulong>().Create();
        ValidateGenericInsert(age.Insert(20, 1UL), "composition age 20 insert");
        ValidateGenericInsert(age.Insert(17, 2UL), "composition age 17 insert");
        ValidateGenericInsert(age.Insert(30, 3UL), "composition age 30 insert");
        ValidateGenericInsert(age.Insert(40, 4UL), "composition age 40 insert");
        ValidateGenericInsert(status.Insert(1, 1UL), "composition status active 1 insert");
        ValidateGenericInsert(status.Insert(2, 2UL), "composition status pending insert");
        ValidateGenericInsert(status.Insert(1, 3UL), "composition status active 3 insert");
        ValidateGenericInsert(status.Insert(0, 4UL), "composition status inactive insert");
        Func<string, IIndex> userResolver = indexName => indexName switch
        {
            "age" => age,
            "status" => status,
            _ => throw new KeyNotFoundException(indexName)
        };
        AssertSet(
            UIDs(LibraDexCondition.ForGroup("users").Index("age").AsInt32.GreaterOrEqual(18).AND.Index("status").AsInt32.EqualTo(1).EndCondition, userResolver),
            new[] { 1UL, 3UL },
            "proof row 167 cross-index AND");
        AssertSet(
            UIDs(LibraDexCondition.ForGroup("users").Index("status").AsInt32.InSet(new[] { 1, 2 }).EndCondition, userResolver),
            new[] { 1UL, 2UL, 3UL },
            "proof row 168 same-index OR membership");
        AssertSequence(
            LibraDexCondition.ForGroup("users").Index("status").AsInt32.EqualTo(1).EndCondition.ToList<ulong>(userResolver, deduplication: IdentityDeduplication.Preserve, take: 1),
            new[] { 1UL },
            "proof row 199 take terminal");
        using LibraDexStringScalar8Index pagingLastName = catalog.Indexes["paging"]["lastName"].String.Create(stringKeys: StringKeys.Exact);
        ValidateGenericInsert(pagingLastName.Insert("Smith", 2001UL), "composition paging Smith insert");
        ValidateGenericInsert(pagingLastName.Insert("Stone", 2002UL), "composition paging Stone insert");
        ValidateGenericInsert(pagingLastName.Insert("Swan", 2003UL), "composition paging Swan insert");
        Func<string, IIndex> pagingResolver = indexName => indexName == "lastName" ? pagingLastName : throw new KeyNotFoundException(indexName);
        LibraDexConditionEndCondition sLastNames = LibraDexCondition.ForGroup("paging").Index("lastName").AsString.StartsWith("S").EndCondition;
        AssertSequence(
            sLastNames.ToList<ulong>(pagingResolver, deduplication: IdentityDeduplication.Preserve, take: 1),
            new[] { 2001UL },
            "proof row 200 bookmark first page");
        AssertSequence(
            sLastNames.ToList<ulong>(pagingResolver, deduplication: IdentityDeduplication.Preserve, take: 2, bookmark: LibraDexBookmark.Legacy(0, 1)),
            new[] { 2002UL, 2003UL },
            "proof row 200 bookmark next page");
        if (!LibraDexCondition.ForGroup("users").Index("age").AsInt32.EqualTo(20).EndCondition.Exists(userResolver, IdentityDeduplication.Preserve) ||
            LibraDexCondition.ForGroup("users").Index("age").AsInt32.EqualTo(99).EndCondition.Exists(userResolver, IdentityDeduplication.Preserve) ||
            LibraDexCondition.ForGroup("users").Index("status").AsInt32.EqualTo(1).EndCondition.Count(userResolver, IdentityDeduplication.Preserve) != 2)
        {
            throw new InvalidDataException("Proof rows 203-204 Exists/Count terminals did not return expected results.");
        }

        LibraDexIndex<int, ulong> deferredPrimary = catalog.Indexes["deferred"]["primary"].Int32Keys<ulong>().Create();
        LibraDexIndex<int, ulong> deferredAlternate = catalog.Indexes["deferred"]["alternate"].Int32Keys<ulong>().Create();
        ValidateGenericInsert(deferredPrimary.Insert(9, 1761UL), "composition deferred primary 9 insert");
        ValidateGenericInsert(deferredPrimary.Insert(12, 1762UL), "composition deferred primary 12 insert");
        ValidateGenericInsert(deferredAlternate.Insert(15, 1763UL), "composition deferred alternate 15 insert");
        Func<string, IIndex> deferredResolver = indexName => indexName switch
        {
            "primary" => deferredPrimary,
            "alternate" => deferredAlternate,
            _ => throw new KeyNotFoundException(indexName)
        };
        string selectedIndexName = "primary";
        LibraDexConditionEndCondition deferredIndexCondition = LibraDexCondition.ForGroup("deferred").Index(() => selectedIndexName, "selectedIndex").AsInt32.GreaterThan(10).EndCondition;
        AssertSet(
            UIDs(deferredIndexCondition, deferredResolver),
            new[] { 1762UL },
            "proof row 176 deferred selector primary");
        selectedIndexName = "alternate";
        AssertSet(
            UIDs(deferredIndexCondition, deferredResolver),
            new[] { 1763UL },
            "proof row 176 deferred selector alternate");
        LibraDexIndexShapeSpec selectedCreatedShape = catalog.Indexes["deferredDate"]["created"].Shape.Date<DateTime, ulong>(
            DateKeys.ExactAndStructured,
            keys: IndexKeys.NonUnique);
        LibraDexIndexShapeSpec selectedUpdatedShape = catalog.Indexes["deferredDate"]["updated"].Shape.Date<DateTime, ulong>(
            DateKeys.ExactAndStructured,
            keys: IndexKeys.NonUnique);
        IIndex selectedCreated = catalog.Indexes.Create(selectedCreatedShape);
        IIndex selectedUpdated = catalog.Indexes.Create(selectedUpdatedShape);
        ValidateGenericInsert(selectedCreated.Insert(new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc), 1771UL), "composition deferred date created 2026 insert");
        ValidateGenericInsert(selectedCreated.Insert(new DateTime(2025, 1, 2, 0, 0, 0, DateTimeKind.Utc), 1772UL), "composition deferred date created 2025 insert");
        ValidateGenericInsert(selectedUpdated.Insert(new DateTime(2026, 6, 2, 0, 0, 0, DateTimeKind.Utc), 1773UL), "composition deferred date updated 2026 insert");
        Func<string, IIndex> deferredDateResolver = indexName => indexName switch
        {
            "created" => selectedCreated,
            "updated" => selectedUpdated,
            _ => throw new KeyNotFoundException(indexName)
        };
        string selectedDateIndex = "created";
        LibraDexConditionEndCondition deferredDateCondition = LibraDexCondition.ForGroup("deferredDate").Index(() => selectedDateIndex, "selectedDate").AsDateTime.YearEqual(2026).EndCondition;
        AssertSet(
            UIDs(deferredDateCondition, deferredDateResolver),
            new[] { 1771UL },
            "proof row 177 deferred date selector created");
        selectedDateIndex = "updated";
        AssertSet(
            UIDs(deferredDateCondition, deferredDateResolver),
            new[] { 1773UL },
            "proof row 177 deferred date selector updated");

        using LibraDexStringScalar8Index email = catalog.Indexes["contacts"]["email"].String.Create(stringKeys: StringKeys.Exact);
        using LibraDexStringScalar8Index phone = catalog.Indexes["contacts"]["phone"].String.Create(stringKeys: StringKeys.Exact);
        ValidateGenericInsert(email.Insert("support@example.com", 21UL), "composition email support insert");
        ValidateGenericInsert(email.Insert("info@example.com", 22UL), "composition email info insert");
        ValidateGenericInsert(phone.Insert("800-555-0100", 23UL), "composition phone 800 insert");
        ValidateGenericInsert(phone.Insert("602-555-0100", 24UL), "composition phone 602 insert");
        Func<string, IIndex> contactResolver = indexName => indexName switch
        {
            "email" => email,
            "phone" => phone,
            _ => throw new KeyNotFoundException(indexName)
        };
        AssertSet(
            UIDs(LibraDexCondition.ForGroup("contacts").Index("email").AsString.StartsWith("support").OR.Index("phone").AsString.StartsWith("800").EndCondition, contactResolver),
            new[] { 21UL, 23UL },
            "proof row 169 cross-index OR");

        using LibraDexStringScalar8Index geohash = catalog.Indexes["places"]["geohash"].String.Create(stringKeys: StringKeys.Exact);
        using LibraDexStringScalar8Index phoneNormalized = catalog.Indexes["people"]["phoneNormalized"].String.Create(stringKeys: StringKeys.Exact);
        ValidateGenericInsert(geohash.Insert("9tbqzn", 1911UL), "composition geohash Phoenix insert");
        ValidateGenericInsert(geohash.Insert("9q5ctr", 1912UL), "composition geohash Bay Area insert");
        static string NormalizePhone(string value)
        {
            return new string(value.Where(static ch => ch >= '0' && ch <= '9').ToArray());
        }

        ValidateGenericInsert(phoneNormalized.Insert(NormalizePhone("(602) 555-0100"), 1921UL), "composition normalized phone insert");
        Func<string, IIndex> codecResolver = indexName => indexName switch
        {
            "geohash" => geohash,
            "phoneNormalized" => phoneNormalized,
            _ => throw new KeyNotFoundException(indexName)
        };
        AssertSet(
            UIDs(LibraDexCondition.ForGroup("places").Index("geohash").AsString.StartsWith("9tb").EndCondition, codecResolver),
            new[] { 1911UL },
            "proof row 191 geohash prefix custom codec");
        AssertSet(
            UIDs(LibraDexCondition.ForGroup("people").Index("phoneNormalized").AsString.EqualTo(NormalizePhone("602.555.0100")).EndCondition, codecResolver),
            new[] { 1921UL },
            "proof row 192 normalized phone custom codec");

        using LibraDexStringScalar8Index role = catalog.Indexes["roles"]["role"].String.Create(stringKeys: StringKeys.Exact);
        LibraDexIndex<int, ulong> roleStatus = catalog.Indexes["roles"]["status"].Int32Keys<ulong>().Create();
        ValidateGenericInsert(role.Insert("admin", 31UL), "composition role admin insert");
        ValidateGenericInsert(role.Insert("owner", 32UL), "composition role owner insert");
        ValidateGenericInsert(role.Insert("user", 33UL), "composition role user insert");
        ValidateGenericInsert(roleStatus.Insert(1, 31UL), "composition role status admin insert");
        ValidateGenericInsert(roleStatus.Insert(1, 32UL), "composition role status owner insert");
        ValidateGenericInsert(roleStatus.Insert(1, 33UL), "composition role status user insert");
        Func<string, IIndex> roleResolver = indexName => indexName switch
        {
            "role" => role,
            "status" => roleStatus,
            _ => throw new KeyNotFoundException(indexName)
        };
        LibraDexConditionEndCondition roles = LibraDexCondition.ForGroup("roles").Index("role").AsString.InSet(new[] { "admin", "owner" }).EndCondition;
        AssertSet(
            UIDs(LibraDexCondition.ForGroup("roles").Index("status").AsInt32.EqualTo(1).AND.Group(roles).EndCondition, roleResolver),
            new[] { 31UL, 32UL },
            "proof row 170 grouped role logic");

        IIndex lastFirst = catalog.Indexes["people"]["lastFirst"].Composite<ulong>(C.Text("lastName"), C.Text("firstName")).Create();
        using LibraDexStringScalar8Index peopleEmail = catalog.Indexes["people"]["email"].String.Create(stringKeys: StringKeys.Exact);
        ValidateGenericInsert(lastFirst.Insert(Key.Of("Smith", "Jane"), 41UL), "composition composite Smith Jane insert");
        ValidateGenericInsert(lastFirst.Insert(Key.Of("Smith", "Bob"), 42UL), "composition composite Smith Bob insert");
        ValidateGenericInsert(lastFirst.Insert(Key.Of("Stone", "Jill"), 43UL), "composition composite Stone Jill insert");
        ValidateGenericInsert(peopleEmail.Insert("sj@example.com", 44UL), "composition people email sj insert");
        Func<string, IIndex> peopleResolver = indexName => indexName switch
        {
            "lastFirst" => lastFirst,
            "email" => peopleEmail,
            _ => throw new KeyNotFoundException(indexName)
        };
        LibraDexConditionEndCondition lastAndFirst = LibraDexCondition.ForGroup("people").Index("lastFirst").Where(
            LibraDexCompositePart.String("lastName").StartsWith("S"),
            LibraDexCompositePart.String("firstName").StartsWith("J")).EndCondition;
        AssertSet(
            UIDs(lastAndFirst, peopleResolver),
            new[] { 41UL, 43UL },
            "proof row 158 composite later prefix");
        AssertSet(
            UIDs(LibraDexCondition.ForGroup("people").Group(lastAndFirst).OR.Index("email").AsString.StartsWith("sj").EndCondition, peopleResolver),
            new[] { 41UL, 43UL, 44UL },
            "proof row 174 grouped composite OR");

        IIndex tenantUser = catalog.Indexes["people"]["tenantUser"].Composite<ulong>(C.Text("tenantId"), C.Text("username")).Create();
        ValidateGenericInsert(tenantUser.Insert(Key.Of("tenant-a", "admin"), 1611UL), "composition tenant/admin insert");
        ValidateGenericInsert(tenantUser.Insert(Key.Of("tenant-b", "reader"), 1612UL), "composition tenant/reader insert");
        ValidateGenericInsert(tenantUser.Insert(Key.Of("tenant-a", "auditor"), 1613UL), "composition tenant/auditor insert");
        Func<string, IIndex> tenantUserResolver = indexName => indexName == "tenantUser" ? tenantUser : throw new KeyNotFoundException(indexName);
        AssertSet(
            UIDs(LibraDexCondition.ForGroup("people").Index("tenantUser").Where(
                LibraDexCompositePart.FullKey("/").AsString.Matches("tenant-a/ad*")).EndCondition, tenantUserResolver),
            new[] { 1611UL },
            "proof row 161 composite full-key delimiter pattern");
        AssertSet(
            UIDs(LibraDexCondition.ForGroup("people").Index("tenantUser").Where(
                LibraDexCompositePart.FullKey("|").Excluding("tenantId").AsString.Contains("admin")).EndCondition, tenantUserResolver),
            new[] { 1611UL },
            "proof row 163 composite excluding full-key contains");

        IIndex accountOrder = catalog.Indexes["orders"]["accountOrder"].Composite<ulong>(C.Int64("accountId"), C.Int32("orderNumber")).Create();
        ValidateGenericInsert(accountOrder.Insert(Key.Of(10L, 1001), 51UL), "composition account order 1001 insert");
        ValidateGenericInsert(accountOrder.Insert(Key.Of(10L, 2001), 52UL), "composition account order 2001 insert");
        ValidateGenericInsert(accountOrder.Insert(Key.Of(11L, 1500), 53UL), "composition account order 1500 insert");
        Func<string, IIndex> orderResolver = indexName => indexName == "accountOrder" ? accountOrder : throw new KeyNotFoundException(indexName);
        AssertSet(
            UIDs(LibraDexCondition.ForGroup("orders").Index("accountOrder").Where(
                LibraDexCompositePart.Scalar<long>("accountId").EqualTo(10L),
                LibraDexCompositePart.Scalar<int>("orderNumber").Between(1000, 2000)).EndCondition, orderResolver),
            new[] { 51UL },
            "proof row 155 composite scalar range");

        IIndex currencyAmount = catalog.Indexes["prices"]["currencyAmount"].Composite<ulong>(C.Text("currency"), C.Int64("amountMinor")).Create();
        ValidateGenericInsert(currencyAmount.Insert(Key.Of("USD", 1001L), 1931UL), "composition USD amount over threshold insert");
        ValidateGenericInsert(currencyAmount.Insert(Key.Of("USD", 999L), 1932UL), "composition USD amount under threshold insert");
        ValidateGenericInsert(currencyAmount.Insert(Key.Of("EUR", 2000L), 1933UL), "composition EUR amount insert");
        Func<string, IIndex> priceResolver = indexName => indexName == "currencyAmount" ? currencyAmount : throw new KeyNotFoundException(indexName);
        AssertSet(
            UIDs(LibraDexCondition.ForGroup("prices").Index("currencyAmount").Where(
                LibraDexCompositePart.String("currency").EqualTo("USD"),
                LibraDexCompositePart.Scalar<long>("amountMinor").GreaterThan(1000L)).EndCondition, priceResolver),
            new[] { 1931UL },
            "proof row 193 composite caller-owned currency amount codec");

        IIndex countryStateCity = catalog.Indexes["places"]["countryStateCity"].Composite<ulong>(C.Text("country"), C.Text("state"), C.Text("city")).Create();
        ValidateGenericInsert(countryStateCity.Insert(Key.Of("US", "WA", "Seattle"), 54UL), "composition place US WA Seattle insert");
        ValidateGenericInsert(countryStateCity.Insert(Key.Of("US", "OR", "Salem"), 55UL), "composition place US OR Salem insert");
        ValidateGenericInsert(countryStateCity.Insert(Key.Of("CA", "BC", "Vancouver"), 56UL), "composition place CA BC Vancouver insert");
        Func<string, IIndex> placeResolver = indexName => indexName == "countryStateCity" ? countryStateCity : throw new KeyNotFoundException(indexName);
        AssertSet(
            UIDs(LibraDexCondition.ForGroup("places").Index("countryStateCity").Where(
                LibraDexCompositePart.String("country").EqualTo("US"),
                LibraDexCompositePart.String("state").EqualTo("WA"),
                LibraDexCompositePart.String("city").StartsWith("Sea")).EndCondition, placeResolver),
            new[] { 54UL },
            "proof row 156 composite multi-part");
        AssertSet(
            UIDs(LibraDexCondition.ForGroup("places").Index("countryStateCity").Where(
                LibraDexCompositePart.String("city").StartsWith("Sea")).EndCondition, placeResolver),
            new[] { 54UL },
            "proof row 157 composite missing lead");

        using LibraDexStringScalar8Index country = catalog.Indexes["demographics"]["country"].String.Create(stringKeys: StringKeys.Exact);
        LibraDexIndex<int, ulong> demographicAge = catalog.Indexes["demographics"]["age"].Int32Keys<ulong>().Create();
        ValidateGenericInsert(country.Insert("US", 71UL), "composition demographics country US insert");
        ValidateGenericInsert(country.Insert("CA", 72UL), "composition demographics country CA insert");
        ValidateGenericInsert(country.Insert("MX", 73UL), "composition demographics country MX insert");
        ValidateGenericInsert(demographicAge.Insert(19, 71UL), "composition demographics age 19 insert");
        ValidateGenericInsert(demographicAge.Insert(21, 72UL), "composition demographics age 21 insert");
        ValidateGenericInsert(demographicAge.Insert(17, 73UL), "composition demographics age 17 insert");
        Func<string, IIndex> demographicResolver = indexName => indexName switch
        {
            "country" => country,
            "age" => demographicAge,
            _ => throw new KeyNotFoundException(indexName)
        };
        LibraDexConditionEndCondition countries = LibraDexCondition.ForGroup("demographics").Index("country").AsString.InSet(new[] { "US", "CA" }).EndCondition;
        AssertSet(
            UIDs(LibraDexCondition.ForGroup("demographics").Group(countries).AND.Index("age").AsInt32.GreaterOrEqual(18).EndCondition, demographicResolver),
            new[] { 71UL, 72UL },
            "proof row 171 grouped country plus age");

        LibraDexIndexShapeSpec rowCreatedShape = catalog.Indexes["rows"]["created"].Shape.Date<DateTime, ulong>(
            DateKeys.ExactAndStructured,
            keys: IndexKeys.NonUnique);
        LibraDexIndexShapeSpec rowUpdatedShape = catalog.Indexes["rows"]["updated"].Shape.Date<DateTime, ulong>(
            DateKeys.ExactAndStructured,
            keys: IndexKeys.NonUnique);
        IIndex rowCreated = catalog.Indexes.Create(rowCreatedShape);
        IIndex rowUpdated = catalog.Indexes.Create(rowUpdatedShape);
        LibraDexIndex<bool, ulong> rowDeleted = catalog.Indexes["rows"]["deleted"].Create<bool, ulong>();
        DateTime today = DateTime.UtcNow.Date;
        ValidateGenericInsert(rowDeleted.Insert(false, 1721UL), "composition row deleted false 1721 insert");
        ValidateGenericInsert(rowDeleted.Insert(false, 1722UL), "composition row deleted false 1722 insert");
        ValidateGenericInsert(rowDeleted.Insert(true, 1723UL), "composition row deleted true 1723 insert");
        ValidateGenericInsert(rowDeleted.Insert(false, 1724UL), "composition row deleted false 1724 insert");
        ValidateGenericInsert(rowCreated.Insert(today, 1721UL), "composition row created today 1721 insert");
        ValidateGenericInsert(rowUpdated.Insert(today, 1722UL), "composition row updated today 1722 insert");
        ValidateGenericInsert(rowCreated.Insert(today, 1723UL), "composition row created today deleted insert");
        ValidateGenericInsert(rowCreated.Insert(today.AddDays(-3), 1724UL), "composition row old insert");
        Func<string, IIndex> rowResolver = indexName => indexName switch
        {
            "created" => rowCreated,
            "updated" => rowUpdated,
            "deleted" => rowDeleted,
            _ => throw new KeyNotFoundException(indexName)
        };
        LibraDexConditionEndCondition freshRows = LibraDexCondition.ForGroup("rows").Index("created").AsDateTime.IsToday().OR.Index("updated").AsDateTime.IsToday().EndCondition;
        AssertSet(
            UIDs(LibraDexCondition.ForGroup("rows").Index("deleted").AsBoolean.EqualTo(false).AND.Group(freshRows).EndCondition, rowResolver),
            new[] { 1721UL, 1722UL },
            "proof row 172 grouped deleted plus created/updated today");

        using LibraDexStringScalar8Index invoiceType = catalog.Indexes["invoices"]["type"].String.Create(stringKeys: StringKeys.Exact);
        LibraDexIndex<long, ulong> amountCents = catalog.Indexes["invoices"]["amountCents"].Int64Keys<ulong>().Create();
        LibraDexIndex<int, ulong> overdue = catalog.Indexes["invoices"]["overdue"].Int32Keys<ulong>().Create();
        ValidateGenericInsert(invoiceType.Insert("invoice", 81UL), "composition invoice type invoice 81 insert");
        ValidateGenericInsert(invoiceType.Insert("invoice", 82UL), "composition invoice type invoice 82 insert");
        ValidateGenericInsert(invoiceType.Insert("receipt", 83UL), "composition invoice type receipt 83 insert");
        ValidateGenericInsert(amountCents.Insert(200000L, 81UL), "composition invoice amount 81 insert");
        ValidateGenericInsert(amountCents.Insert(500L, 82UL), "composition invoice amount 82 insert");
        ValidateGenericInsert(amountCents.Insert(200000L, 83UL), "composition invoice amount 83 insert");
        ValidateGenericInsert(overdue.Insert(0, 81UL), "composition invoice overdue 81 insert");
        ValidateGenericInsert(overdue.Insert(1, 82UL), "composition invoice overdue 82 insert");
        ValidateGenericInsert(overdue.Insert(1, 83UL), "composition invoice overdue 83 insert");
        Func<string, IIndex> invoiceResolver = indexName => indexName switch
        {
            "type" => invoiceType,
            "amountCents" => amountCents,
            "overdue" => overdue,
            _ => throw new KeyNotFoundException(indexName)
        };
        LibraDexConditionEndCondition highOrOverdue = LibraDexCondition.ForGroup("invoices").Index("amountCents").AsInt64.GreaterThan(100000L).OR.Index("overdue").AsInt32.EqualTo(1).EndCondition;
        AssertSet(
            UIDs(LibraDexCondition.ForGroup("invoices").Index("type").AsString.EqualTo("invoice").AND.Group(highOrOverdue).EndCondition, invoiceResolver),
            new[] { 81UL, 82UL },
            "proof row 173 grouped invoice logic");

        using LibraDexStringScalar8Index tenant = catalog.Indexes["security"]["tenant"].String.Create(stringKeys: StringKeys.Exact);
        LibraDexIndex<int, ulong> failedLoginCount = catalog.Indexes["security"]["failedLoginCount"].Int32Keys<ulong>().Create();
        LibraDexIndex<int, ulong> locked = catalog.Indexes["security"]["locked"].Int32Keys<ulong>().Create();
        ValidateGenericInsert(tenant.Insert("T1", 61UL), "composition security tenant 61 insert");
        ValidateGenericInsert(tenant.Insert("T1", 62UL), "composition security tenant 62 insert");
        ValidateGenericInsert(tenant.Insert("T2", 63UL), "composition security tenant 63 insert");
        ValidateGenericInsert(failedLoginCount.Insert(6, 61UL), "composition failed login 61 insert");
        ValidateGenericInsert(failedLoginCount.Insert(1, 62UL), "composition failed login 62 insert");
        ValidateGenericInsert(failedLoginCount.Insert(9, 63UL), "composition failed login 63 insert");
        ValidateGenericInsert(locked.Insert(0, 61UL), "composition locked false 61 insert");
        ValidateGenericInsert(locked.Insert(1, 62UL), "composition locked true 62 insert");
        ValidateGenericInsert(locked.Insert(1, 63UL), "composition locked true 63 insert");
        Func<string, IIndex> securityResolver = indexName => indexName switch
        {
            "tenant" => tenant,
            "failedLoginCount" => failedLoginCount,
            "locked" => locked,
            _ => throw new KeyNotFoundException(indexName)
        };
        LibraDexConditionEndCondition risky = LibraDexCondition.ForGroup("security").Index("failedLoginCount").AsInt32.GreaterThan(5).OR.Index("locked").AsInt32.EqualTo(1).EndCondition;
        AssertSet(
            UIDs(LibraDexCondition.ForGroup("security").Index("tenant").AsString.EqualTo("T1").AND.Group(risky).EndCondition, securityResolver),
            new[] { 61UL, 62UL },
            "proof row 175 grouped security logic");

        LibraDexIndex<int, long> groupStatus = catalog.Indexes["grouping"]["status"].Int32Keys<long>().Create();
        LibraDexIndex<int, long> groupTenant = catalog.Indexes["grouping"]["tenant"].Int32Keys<long>().Create();
        LibraDexIndex<int, long> groupEmail = catalog.Indexes["grouping"]["email"].Int32Keys<long>().Create();
        LibraDexIndex<int, long> groupCategory = catalog.Indexes["grouping"]["category"].Int32Keys<long>().Create();
        for (long id = 1; id <= 6; id++)
        {
            ValidateGenericInsert(groupStatus.Insert(1, id), $"composition grouping status {id} insert");
        }

        ValidateGenericInsert(groupTenant.Insert(100, 1L), "composition grouping tenant 100/1 insert");
        ValidateGenericInsert(groupTenant.Insert(100, 2L), "composition grouping tenant 100/2 insert");
        ValidateGenericInsert(groupTenant.Insert(200, 3L), "composition grouping tenant 200/3 insert");
        ValidateGenericInsert(groupTenant.Insert(300, 4L), "composition grouping tenant 300/4 insert");
        ValidateGenericInsert(groupEmail.Insert(10, 1L), "composition grouping email 10/1 insert");
        ValidateGenericInsert(groupEmail.Insert(10, 2L), "composition grouping email 10/2 insert");
        ValidateGenericInsert(groupEmail.Insert(20, 3L), "composition grouping email 20/3 insert");
        ValidateGenericInsert(groupEmail.Insert(30, 4L), "composition grouping email 30/4 insert");
        ValidateGenericInsert(groupCategory.Insert(5, 1L), "composition grouping category 5/1 insert");
        ValidateGenericInsert(groupCategory.Insert(5, 2L), "composition grouping category 5/2 insert");
        ValidateGenericInsert(groupCategory.Insert(5, 3L), "composition grouping category 5/3 insert");
        ValidateGenericInsert(groupCategory.Insert(6, 4L), "composition grouping category 6/4 insert");
        ValidateGenericInsert(groupCategory.Insert(6, 5L), "composition grouping category 6/5 insert");
        ValidateGenericInsert(groupCategory.Insert(7, 6L), "composition grouping category 7/6 insert");
        Func<string, IIndex> groupingResolver = indexName => indexName == "status" ? groupStatus : throw new KeyNotFoundException(indexName);
        LibraDexConditionEndCondition allGrouping = LibraDexCondition.ForGroup("grouping").Index("status").AsInt32.EqualTo(1).EndCondition;
        IReadOnlyDictionary<int, long> tenantCounts = allGrouping.Groups(groupingResolver).By(groupTenant).Counts();
        IReadOnlyList<LibraDexGroup<int, long>> duplicateEmails = allGrouping.Groups(groupingResolver).By(groupEmail).Duplicates().ToList();
        IReadOnlyList<LibraDexGroup<int, long>> singletonEmails = allGrouping.Groups(groupingResolver).By(groupEmail).Singletons().ToList();
        IReadOnlyDictionary<int, long> tenantRepresentatives = allGrouping.Groups(groupingResolver).By(groupTenant).Representatives();
        IReadOnlyList<LibraDexGroup<int, long>> topCategories = allGrouping.Groups(groupingResolver).By(groupCategory).OrderBy(LibraDexGroupOrder.CountDescending).Take(2).ToList();
        if (tenantCounts[100] != 2 ||
            tenantCounts[200] != 1 ||
            duplicateEmails.Count != 1 ||
            duplicateEmails[0].Key != 10 ||
            singletonEmails.Count != 2 ||
            !singletonEmails.Select(static group => group.Key).Order().SequenceEqual(new[] { 20, 30 }) ||
            tenantRepresentatives[100] != 1L ||
            tenantRepresentatives[200] != 3L ||
            !topCategories.Select(static group => group.Key).SequenceEqual(new[] { 5, 6 }))
        {
            throw new InvalidDataException("Proof rows 205-208/210 grouping terminals did not return expected groups.");
        }

        static byte[] Id(int suffix) => new byte[] { 0, 0, 0, 0, 0x41, 0x42, 0x43, (byte)suffix };

        static bool ContainsByteIdentity(IReadOnlyList<byte[]> identities, byte[] expected)
        {
            for (int i = 0; i < identities.Count; i++)
            {
                if (identities[i].SequenceEqual(expected))
                {
                    return true;
                }
            }

            return false;
        }

        using Catalog binaryIdentityCatalog = Catalog.CreateMemory();
        LibraDexIndex<int, byte[]> binaryIdentityAge = binaryIdentityCatalog.CreateGenericIndex<int, byte[]>(
            "binaryIdentityAge",
            slotIndex: 0,
            new IndexOptions { Keys = IndexKeys.NonUnique },
            keyWidth: null,
            identityWidth: LibraDexScalarWidth.Bytes8,
            group: "binaryIdentities",
            identityFamily: CatalogIndexIdentityFamily.Blob);
        LibraDexIndex<int, byte[]> binaryIdentityStatus = binaryIdentityCatalog.CreateGenericIndex<int, byte[]>(
            "binaryIdentityStatus",
            slotIndex: 1,
            new IndexOptions { Keys = IndexKeys.NonUnique },
            keyWidth: null,
            identityWidth: LibraDexScalarWidth.Bytes8,
            group: "binaryIdentities",
            identityFamily: CatalogIndexIdentityFamily.Blob);
        byte[] binaryIdentityA = Id(1);
        byte[] binaryIdentityB = Id(2);
        byte[] binaryIdentityC = Id(3);
        ValidateGenericInsert(binaryIdentityAge.Insert(30, binaryIdentityA), "composition binary identity age A insert");
        ValidateGenericInsert(binaryIdentityAge.Insert(40, binaryIdentityB), "composition binary identity age B insert");
        ValidateGenericInsert(binaryIdentityAge.Insert(30, binaryIdentityC), "composition binary identity age C insert");
        ValidateGenericInsert(binaryIdentityStatus.Insert(1, Id(1)), "composition binary identity status A clone insert");
        ValidateGenericInsert(binaryIdentityStatus.Insert(1, Id(3)), "composition binary identity status C clone insert");
        ValidateGenericInsert(binaryIdentityStatus.Insert(0, Id(2)), "composition binary identity status B clone insert");
        Func<string, IIndex> binaryIdentityResolver = indexName => indexName switch
        {
            "age" => binaryIdentityAge,
            "status" => binaryIdentityStatus,
            _ => throw new KeyNotFoundException(indexName)
        };
        IReadOnlyList<byte[]> binaryIdentityAndIds = LibraDexCondition
            .ForGroup("binaryIdentities")
            .Index("age").AsInt32.EqualTo(30)
            .AND.Index("status").AsInt32.EqualTo(1)
            .EndCondition
            .ToList<byte[]>(binaryIdentityResolver, deduplication: IdentityDeduplication.Distinct);
        IReadOnlyList<byte[]> binaryIdentityOrIds = LibraDexCondition
            .ForGroup("binaryIdentities")
            .Index("age").AsInt32.EqualTo(40)
            .OR.Index("status").AsInt32.EqualTo(1)
            .EndCondition
            .ToList<byte[]>(binaryIdentityResolver, deduplication: IdentityDeduplication.Distinct);
        if (binaryIdentityAndIds.Count != 2 ||
            !ContainsByteIdentity(binaryIdentityAndIds, binaryIdentityA) ||
            !ContainsByteIdentity(binaryIdentityAndIds, binaryIdentityC) ||
            binaryIdentityOrIds.Count != 3 ||
            !ContainsByteIdentity(binaryIdentityOrIds, binaryIdentityA) ||
            !ContainsByteIdentity(binaryIdentityOrIds, binaryIdentityB) ||
            !ContainsByteIdentity(binaryIdentityOrIds, binaryIdentityC))
        {
            throw new InvalidDataException("Binary byte[] identity composition did not use structural identity equality across AND/OR retrieval.");
        }
    }


    private static void ValidateShelfMetadataContract()
    {
        Scalar8Scalar8Profile ss88Profile = Scalar8Scalar8Profile.Default32KiB;
        byte[] ss88Bytes = new byte[ss88Profile.ShelfExtentSize];
        Scalar8Scalar8 ss88 = new(ss88Bytes, ss88Profile);
        ss88.Initialize();
        _ = ss88.Insert(1, 11, allowDuplicateKeys: true);
        Scalar8Scalar8ReadOnly ss88ReadOnly = ss88.AsReadOnly();
        if (ss88.PhysicalItemCount != 1 ||
            ss88.LiveItemCount != 1 ||
            ss88.DeletedItemCount != 0 ||
            ss88ReadOnly.PhysicalItemCount != 1 ||
            ss88ReadOnly.LiveItemCount != 1 ||
            Scalar8Scalar8Layout.DeletedSlotOffset != 0)
        {
            throw new InvalidDataException("SS8-8 shelf metadata contract did not expose compact live/physical counts.");
        }

        _ = ss88.MarkSlotRangeDeleted(0, 1);
        ss88ReadOnly = ss88.AsReadOnly();
        if (ss88.PhysicalItemCount != 1 ||
            ss88.LiveItemCount != 0 ||
            ss88.DeletedItemCount != 1 ||
            ss88ReadOnly.PhysicalItemCount != 1 ||
            ss88ReadOnly.LiveItemCount != 0 ||
            ss88ReadOnly.DeletedItemCount != 1 ||
            ss88.NormalizeDeletedSlotsForPublication() != 1 ||
            ss88.PhysicalItemCount != 0 ||
            ss88.LiveItemCount != 0 ||
            ss88.DeletedItemCount != 0)
        {
            throw new InvalidDataException("SS8-8 shelf metadata contract did not expose tombstone live/deleted counts.");
        }

        Scalar16Scalar8Profile ss168Profile = Scalar16Scalar8Profile.Default32KiB;
        byte[] ss168Bytes = new byte[ss168Profile.ShelfExtentSize];
        Scalar16Scalar8 ss168 = new(ss168Bytes, ss168Profile);
        ss168.Initialize();
        _ = ss168.Insert(1, 2, 11, allowDuplicateKeys: true);
        if (ss168.PhysicalItemCount != 1 ||
            ss168.LiveItemCount != 1 ||
            ss168.DeletedItemCount != 0 ||
            ss168.AsReadOnly().LiveItemCount != 1 ||
            Scalar16Scalar8Layout.DeletedSlotOffset != 0)
        {
            throw new InvalidDataException("SS16-8 shelf metadata contract did not expose compact live/physical counts.");
        }

        _ = ss168.MarkSlotRangeDeleted(0, 1);
        if (ss168.PhysicalItemCount != 1 ||
            ss168.LiveItemCount != 0 ||
            ss168.DeletedItemCount != 1 ||
            ss168.AsReadOnly().DeletedItemCount != 1 ||
            ss168.NormalizeDeletedSlotsForPublication() != 1 ||
            ss168.PhysicalItemCount != 0)
        {
            throw new InvalidDataException("SS16-8 shelf metadata contract did not expose tombstone live/deleted counts.");
        }

        Scalar8Scalar16Profile ss816Profile = Scalar8Scalar16Profile.Default32KiB;
        byte[] ss816Bytes = new byte[ss816Profile.ShelfExtentSize];
        Scalar8Scalar16 ss816 = new(ss816Bytes, ss816Profile);
        ss816.Initialize();
        _ = ss816.Insert(1, 11, 12, allowDuplicateKeys: true);
        if (ss816.PhysicalItemCount != 1 ||
            ss816.LiveItemCount != 1 ||
            ss816.DeletedItemCount != 0 ||
            ss816.AsReadOnly().LiveItemCount != 1 ||
            Scalar8Scalar16Layout.DeletedSlotOffset != 0)
        {
            throw new InvalidDataException("SS8-16 shelf metadata contract did not expose compact live/physical counts.");
        }

        _ = ss816.MarkSlotRangeDeleted(0, 1);
        if (ss816.PhysicalItemCount != 1 ||
            ss816.LiveItemCount != 0 ||
            ss816.DeletedItemCount != 1 ||
            ss816.AsReadOnly().DeletedItemCount != 1 ||
            ss816.NormalizeDeletedSlotsForPublication() != 1 ||
            ss816.PhysicalItemCount != 0)
        {
            throw new InvalidDataException("SS8-16 shelf metadata contract did not expose tombstone live/deleted counts.");
        }

        Scalar16Scalar16Profile ss1616Profile = Scalar16Scalar16Profile.Default32KiB;
        byte[] ss1616Bytes = new byte[ss1616Profile.ShelfExtentSize];
        Scalar16Scalar16 ss1616 = new(ss1616Bytes, ss1616Profile);
        ss1616.Initialize();
        _ = ss1616.Insert(1, 2, 11, 12, allowDuplicateKeys: true);
        if (ss1616.PhysicalItemCount != 1 ||
            ss1616.LiveItemCount != 1 ||
            ss1616.DeletedItemCount != 0 ||
            ss1616.AsReadOnly().LiveItemCount != 1 ||
            Scalar16Scalar16Layout.DeletedSlotOffset != 0)
        {
            throw new InvalidDataException("SS16-16 shelf metadata contract did not expose compact live/physical counts.");
        }

        _ = ss1616.MarkSlotRangeDeleted(0, 1);
        if (ss1616.PhysicalItemCount != 1 ||
            ss1616.LiveItemCount != 0 ||
            ss1616.DeletedItemCount != 1 ||
            ss1616.AsReadOnly().DeletedItemCount != 1 ||
            ss1616.NormalizeDeletedSlotsForPublication() != 1 ||
            ss1616.PhysicalItemCount != 0)
        {
            throw new InvalidDataException("SS16-16 shelf metadata contract did not expose tombstone live/deleted counts.");
        }

        Fixed32Scalar8Profile fs328Profile = Fixed32Scalar8Profile.Default32KiB;
        byte[] fs328Bytes = new byte[fs328Profile.ShelfExtentSize];
        Fixed32Scalar8 fs328 = new(fs328Bytes, fs328Profile);
        fs328.Initialize();
        _ = fs328.Insert(1, 2, 3, 4, 11, allowDuplicateKeys: true);
        if (fs328.PhysicalItemCount != 1 ||
            fs328.LiveItemCount != 1 ||
            fs328.DeletedItemCount != 0 ||
            fs328.AsReadOnly().LiveItemCount != 1 ||
            Fixed32Scalar8Layout.DeletedSlotOffset != 0)
        {
            throw new InvalidDataException("FS32-8 shelf metadata contract did not expose compact live/physical counts.");
        }

        _ = fs328.MarkSlotRangeDeleted(0, 1);
        if (fs328.PhysicalItemCount != 1 ||
            fs328.LiveItemCount != 0 ||
            fs328.DeletedItemCount != 1 ||
            fs328.AsReadOnly().DeletedItemCount != 1 ||
            fs328.NormalizeDeletedSlotsForPublication() != 1 ||
            fs328.PhysicalItemCount != 0)
        {
            throw new InvalidDataException("FS32-8 shelf metadata contract did not expose tombstone live/deleted counts.");
        }

        Fixed32Scalar16Profile fs3216Profile = Fixed32Scalar16Profile.Default32KiB;
        byte[] fs3216Bytes = new byte[fs3216Profile.ShelfExtentSize];
        Fixed32Scalar16 fs3216 = new(fs3216Bytes, fs3216Profile);
        fs3216.Initialize();
        _ = fs3216.Insert(1, 2, 3, 4, 11, 12, allowDuplicateKeys: true);
        if (fs3216.PhysicalItemCount != 1 ||
            fs3216.LiveItemCount != 1 ||
            fs3216.DeletedItemCount != 0 ||
            fs3216.AsReadOnly().LiveItemCount != 1 ||
            Fixed32Scalar16Layout.DeletedSlotOffset != 0)
        {
            throw new InvalidDataException("FS32-16 shelf metadata contract did not expose compact live/physical counts.");
        }

        _ = fs3216.MarkSlotRangeDeleted(0, 1);
        if (fs3216.PhysicalItemCount != 1 ||
            fs3216.LiveItemCount != 0 ||
            fs3216.DeletedItemCount != 1 ||
            fs3216.AsReadOnly().DeletedItemCount != 1 ||
            fs3216.NormalizeDeletedSlotsForPublication() != 1 ||
            fs3216.PhysicalItemCount != 0)
        {
            throw new InvalidDataException("FS32-16 shelf metadata contract did not expose tombstone live/deleted counts.");
        }

        byte[] key = Encoding.UTF8.GetBytes("alpha");
        byte[] identity = Encoding.UTF8.GetBytes("identity");

        byte[] vs8Bytes = new byte[VarKeyScalar8Profile.Default16KiB.ShelfExtentSize];
        VarKeyScalar8Layout.Initialize(vs8Bytes, VarKeyScalar8Profile.Default16KiB);
        if (!VarKeyScalar8MutableShelf.TryCreate(vs8Bytes, VarKeyScalar8Profile.Default16KiB, ownsBytes: false, rentSidecars: false, out VarKeyScalar8MutableShelf vs8) ||
            vs8.InsertWithMutationHint(key, 11, true, 0, 4, out _) != VarKeyScalar8InsertResult.Inserted ||
            vs8.PhysicalItemCount != 1 ||
            vs8.LiveItemCount != 1 ||
            vs8.DeletedItemCount != 0 ||
            vs8.PayloadBytesUsed <= 0 ||
            vs8.PayloadBytesLive != vs8.PayloadBytesUsed ||
            vs8.PayloadBytesDeleted != 0)
        {
            throw new InvalidDataException("VS8 shelf metadata contract did not expose compact live/physical/payload counts.");
        }

        _ = vs8.MarkSlotRangeDeleted(0, 1);
        if (vs8.PhysicalItemCount != 1 ||
            vs8.LiveItemCount != 0 ||
            vs8.DeletedItemCount != 1 ||
            vs8.PayloadBytesDeleted <= 0 ||
            vs8.PayloadBytesLive != 0 ||
            vs8.NormalizeDeletedSlotsForPublication() != 1 ||
            vs8.PhysicalItemCount != 0 ||
            vs8.DeletedItemCount != 0 ||
            vs8.PayloadBytesDeleted <= 0)
        {
            throw new InvalidDataException("VS8 shelf metadata contract did not expose slot-deleted and payload-deleted counts.");
        }
        if (!VarKeyScalar8MutableShelf.TryCreate(vs8Bytes, VarKeyScalar8Profile.Default16KiB, ownsBytes: false, rentSidecars: false, out VarKeyScalar8MutableShelf reopenedVs8) ||
            reopenedVs8.PayloadBytesDeleted != vs8.PayloadBytesDeleted)
        {
            throw new InvalidDataException("VS8 shelf metadata contract did not restore its persisted orphaned-payload byte count.");
        }

        byte[] vs8RepackBytes = new byte[VarKeyScalar8Profile.Default16KiB.ShelfExtentSize];
        VarKeyScalar8Layout.Initialize(vs8RepackBytes, VarKeyScalar8Profile.Default16KiB);
        if (!VarKeyScalar8MutableShelf.TryCreate(vs8RepackBytes, VarKeyScalar8Profile.Default16KiB, ownsBytes: false, rentSidecars: false, out VarKeyScalar8MutableShelf vs8Repack))
        {
            throw new InvalidDataException("VS8 repack shelf setup failed.");
        }

        for (int i = 0; i < 12; i++)
        {
            if (vs8Repack.InsertWithMutationHint(CreateVarKeyRepackHarnessKey(i), (ulong)i, true, 0, 4, out _) != VarKeyScalar8InsertResult.Inserted)
            {
                throw new InvalidDataException("VS8 repack shelf setup insert failed.");
            }
        }

        int vs8PayloadBeforeRepack = vs8Repack.PayloadBytesUsed;
        _ = vs8Repack.MarkSlotRangeDeleted(0, 8);
        if (!vs8Repack.RepackPayloadIfWorthwhile(4096, 25) ||
            vs8Repack.PhysicalItemCount != 4 ||
            vs8Repack.DeletedItemCount != 0 ||
            vs8Repack.PayloadBytesDeleted != 0 ||
            vs8Repack.PayloadBytesUsed >= vs8PayloadBeforeRepack)
        {
            throw new InvalidDataException("VS8 payload repack did not compact orphaned record bytes after threshold crossing.");
        }

        byte[] vs16Bytes = new byte[VarKeyScalar16Profile.Default16KiB.ShelfExtentSize];
        VarKeyScalar16Layout.Initialize(vs16Bytes, VarKeyScalar16Profile.Default16KiB);
        if (!VarKeyScalar16MutableShelf.TryCreate(vs16Bytes, VarKeyScalar16Profile.Default16KiB, ownsBytes: false, rentSidecars: false, out VarKeyScalar16MutableShelf vs16) ||
            vs16.InsertWithMutationHint(key, 11, 12, true, 0, 4, out _) != VarKeyScalar16InsertResult.Inserted ||
            vs16.PhysicalItemCount != 1 ||
            vs16.LiveItemCount != 1 ||
            vs16.DeletedItemCount != 0 ||
            vs16.PayloadBytesUsed <= 0 ||
            vs16.PayloadBytesLive != vs16.PayloadBytesUsed ||
            vs16.PayloadBytesDeleted != 0)
        {
            throw new InvalidDataException("VS16 shelf metadata contract did not expose compact live/physical/payload counts.");
        }

        _ = vs16.MarkSlotRangeDeleted(0, 1);
        if (vs16.PhysicalItemCount != 1 ||
            vs16.LiveItemCount != 0 ||
            vs16.DeletedItemCount != 1 ||
            vs16.PayloadBytesDeleted <= 0 ||
            vs16.PayloadBytesLive != 0 ||
            vs16.NormalizeDeletedSlotsForPublication() != 1 ||
            vs16.PhysicalItemCount != 0 ||
            vs16.DeletedItemCount != 0 ||
            vs16.PayloadBytesDeleted <= 0)
        {
            throw new InvalidDataException("VS16 shelf metadata contract did not expose slot-deleted and payload-deleted counts.");
        }
        if (!VarKeyScalar16MutableShelf.TryCreate(vs16Bytes, VarKeyScalar16Profile.Default16KiB, ownsBytes: false, rentSidecars: false, out VarKeyScalar16MutableShelf reopenedVs16) ||
            reopenedVs16.PayloadBytesDeleted != vs16.PayloadBytesDeleted)
        {
            throw new InvalidDataException("VS16 shelf metadata contract did not restore its persisted orphaned-payload byte count.");
        }

        byte[] vs16RepackBytes = new byte[VarKeyScalar16Profile.Default16KiB.ShelfExtentSize];
        VarKeyScalar16Layout.Initialize(vs16RepackBytes, VarKeyScalar16Profile.Default16KiB);
        if (!VarKeyScalar16MutableShelf.TryCreate(vs16RepackBytes, VarKeyScalar16Profile.Default16KiB, ownsBytes: false, rentSidecars: false, out VarKeyScalar16MutableShelf vs16Repack))
        {
            throw new InvalidDataException("VS16 repack shelf setup failed.");
        }

        for (int i = 0; i < 12; i++)
        {
            if (vs16Repack.InsertWithMutationHint(CreateVarKeyRepackHarnessKey(i), (ulong)i, (ulong)(i + 1), true, 0, 4, out _) != VarKeyScalar16InsertResult.Inserted)
            {
                throw new InvalidDataException("VS16 repack shelf setup insert failed.");
            }
        }

        int vs16PayloadBeforeRepack = vs16Repack.PayloadBytesUsed;
        _ = vs16Repack.MarkSlotRangeDeleted(0, 8);
        if (!vs16Repack.RepackPayloadIfWorthwhile(4096, 25) ||
            vs16Repack.PhysicalItemCount != 4 ||
            vs16Repack.DeletedItemCount != 0 ||
            vs16Repack.PayloadBytesDeleted != 0 ||
            vs16Repack.PayloadBytesUsed >= vs16PayloadBeforeRepack)
        {
            throw new InvalidDataException("VS16 payload repack did not compact orphaned record bytes after threshold crossing.");
        }

        byte[] vvBytes = new byte[VarKeyVarIdentityProfile.Default16KiB.ShelfExtentSize];
        VarKeyVarIdentityLayout.Initialize(vvBytes, VarKeyVarIdentityProfile.Default16KiB);
        if (!VarKeyVarIdentityMutableShelf.TryCreate(vvBytes, VarKeyVarIdentityProfile.Default16KiB, ownsBytes: false, rentSidecars: false, out VarKeyVarIdentityMutableShelf vv) ||
            vv.InsertWithMutationHint(key, identity, true, 0, 4, out _) != VarKeyVarIdentityInsertResult.Inserted ||
            vv.PhysicalItemCount != 1 ||
            vv.LiveItemCount != 1 ||
            vv.DeletedItemCount != 0 ||
            vv.PayloadBytesUsed <= 0 ||
            vv.PayloadBytesLive != vv.PayloadBytesUsed ||
            vv.PayloadBytesDeleted != 0)
        {
            throw new InvalidDataException("VV shelf metadata contract did not expose compact live/physical/payload counts.");
        }

        _ = vv.MarkSlotRangeDeleted(0, 1);
        if (vv.PhysicalItemCount != 1 ||
            vv.LiveItemCount != 0 ||
            vv.DeletedItemCount != 1 ||
            vv.PayloadBytesDeleted <= 0 ||
            vv.PayloadBytesLive != 0 ||
            vv.NormalizeDeletedSlotsForPublication() != 1 ||
            vv.PhysicalItemCount != 0 ||
            vv.DeletedItemCount != 0 ||
            vv.PayloadBytesDeleted <= 0)
        {
            throw new InvalidDataException("VV shelf metadata contract did not expose slot-deleted and payload-deleted counts.");
        }
        if (!VarKeyVarIdentityMutableShelf.TryCreate(vvBytes, VarKeyVarIdentityProfile.Default16KiB, ownsBytes: false, rentSidecars: false, out VarKeyVarIdentityMutableShelf reopenedVv) ||
            reopenedVv.PayloadBytesDeleted != vv.PayloadBytesDeleted)
        {
            throw new InvalidDataException("VV shelf metadata contract did not restore its persisted orphaned-payload byte count.");
        }

        byte[] sv8Bytes = new byte[Scalar8VarIdentityProfile.Default16KiB.ShelfExtentSize];
        Scalar8VarIdentityLayout.Initialize(sv8Bytes, Scalar8VarIdentityProfile.Default16KiB);
        if (!Scalar8VarIdentityMutableShelfView.TryCreate(sv8Bytes, Scalar8VarIdentityProfile.Default16KiB, out Scalar8VarIdentityMutableShelfView sv8) ||
            sv8.Insert(1, identity, true) != Scalar8VarIdentityInsertResult.Inserted ||
            sv8.PhysicalItemCount != 1 ||
            sv8.LiveItemCount != 1 ||
            sv8.DeletedItemCount != 0 ||
            sv8.PayloadBytesUsed <= 0 ||
            sv8.PayloadBytesLive != sv8.PayloadBytesUsed ||
            sv8.PayloadBytesDeleted != 0)
        {
            throw new InvalidDataException("SV8 shelf metadata contract did not expose compact live/physical/payload counts.");
        }

        _ = sv8.MarkSlotRangeDeleted(0, 1);
        if (sv8.PhysicalItemCount != 1 ||
            sv8.LiveItemCount != 0 ||
            sv8.DeletedItemCount != 1 ||
            sv8.PayloadBytesDeleted <= 0 ||
            sv8.PayloadBytesLive != 0 ||
            sv8.NormalizeDeletedSlotsForPublication() != 1 ||
            sv8.PhysicalItemCount != 0 ||
            sv8.DeletedItemCount != 0 ||
            sv8.PayloadBytesDeleted <= 0)
        {
            throw new InvalidDataException("SV8 shelf metadata contract did not expose slot-deleted and payload-deleted counts.");
        }
        if (!Scalar8VarIdentityMutableShelfView.TryCreate(sv8Bytes, Scalar8VarIdentityProfile.Default16KiB, out Scalar8VarIdentityMutableShelfView reopenedSv8) ||
            reopenedSv8.PayloadBytesDeleted != sv8.PayloadBytesDeleted)
        {
            throw new InvalidDataException("SV8 shelf metadata contract did not restore its persisted orphaned-payload byte count.");
        }

        byte[] sv16Bytes = new byte[Scalar16VarIdentityProfile.Default16KiB.ShelfExtentSize];
        Scalar16VarIdentityLayout.Initialize(sv16Bytes, Scalar16VarIdentityProfile.Default16KiB);
        if (!Scalar16VarIdentityMutableShelfView.TryCreate(sv16Bytes, Scalar16VarIdentityProfile.Default16KiB, out Scalar16VarIdentityMutableShelfView sv16) ||
            sv16.Insert(1, 2, identity, true) != Scalar16VarIdentityInsertResult.Inserted ||
            sv16.PhysicalItemCount != 1 ||
            sv16.LiveItemCount != 1 ||
            sv16.DeletedItemCount != 0 ||
            sv16.PayloadBytesUsed <= 0 ||
            sv16.PayloadBytesLive != sv16.PayloadBytesUsed ||
            sv16.PayloadBytesDeleted != 0)
        {
            throw new InvalidDataException("SV16 shelf metadata contract did not expose compact live/physical/payload counts.");
        }

        _ = sv16.MarkSlotRangeDeleted(0, 1);
        if (sv16.PhysicalItemCount != 1 ||
            sv16.LiveItemCount != 0 ||
            sv16.DeletedItemCount != 1 ||
            sv16.PayloadBytesDeleted <= 0 ||
            sv16.PayloadBytesLive != 0 ||
            sv16.NormalizeDeletedSlotsForPublication() != 1 ||
            sv16.PhysicalItemCount != 0 ||
            sv16.DeletedItemCount != 0 ||
            sv16.PayloadBytesDeleted <= 0)
        {
            throw new InvalidDataException("SV16 shelf metadata contract did not expose slot-deleted and payload-deleted counts.");
        }
        if (!Scalar16VarIdentityMutableShelfView.TryCreate(sv16Bytes, Scalar16VarIdentityProfile.Default16KiB, out Scalar16VarIdentityMutableShelfView reopenedSv16) ||
            reopenedSv16.PayloadBytesDeleted != sv16.PayloadBytesDeleted)
        {
            throw new InvalidDataException("SV16 shelf metadata contract did not restore its persisted orphaned-payload byte count.");
        }
    }


    private readonly record struct QualityScenarioResult(
        string Command,
        TimeSpan Elapsed,
        long AllocatedBytes,
        long FileBytes,
        string? Path);


    private sealed class ClassificationOnlyIndex : IIndex
    {
        private readonly Catalog catalog;
        private readonly LibraDexIndexShapeSpec shape;
        private readonly string group;

        internal ClassificationOnlyIndex(Catalog catalog, LibraDexIndexShapeSpec shape, string? groupOverride = null)
        {
            this.catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
            this.shape = shape;
            group = groupOverride ?? shape.Group;
        }

        public Catalog Catalog => catalog;

        public string Name => shape.Name;

        public string Group => group;

        public Type KeyType => shape.KeyType;

        public Type IdentityType => shape.IdentityType;

        public IndexKeys KeyContract => shape.KeyContract;

        public IdentityKeyMultiplicity IdentityKeyMultiplicity => shape.IdentityKeyMultiplicity;

        public CatalogIndexKeyFamily KeyFamily => shape.KeyFamily;

        public CatalogIndexIdentityFamily IdentityFamily => shape.IdentityFamily;

        public LibraDexIndexShapeSpec? LogicalShape => shape;

        public LibraDexGenericInsertResult Insert(object? key, object identity)
        {
            throw new NotSupportedException("Classification-only indexes do not insert.");
        }

        public LibraDexPreparedObjectSet PrepareInSet(IEnumerable<object> keys)
        {
            throw new NotSupportedException("Classification-only indexes do not prepare membership sets.");
        }
    }


    private static double ElapsedNsPerIdentity(long elapsedTicks, long identityCount)
    {
        return elapsedTicks * 1_000_000_000d / Stopwatch.Frequency / Math.Max(identityCount, 1);
    }


    private static double ElapsedNsPerCall(long elapsedTicks, int callCount)
    {
        return elapsedTicks * 1_000_000_000d / Stopwatch.Frequency / Math.Max(callCount, 1);
    }


    /// <summary>
    /// Produces a small stable checksum over a raw identity byte sequence.<br/>
    /// The helper intentionally avoids allocating or decoding text because varlen identities are raw LibraDex bytes, not strings.<br/>
    /// </summary>
    /// <param name="source">The raw identity bytes.</param>
    /// <returns>The checksum contribution for the supplied byte sequence.</returns>
    private static long ChecksumBytes(ReadOnlySpan<byte> source)
    {
        long checksum = 1469598103934665603L;
        for (int i = 0; i < source.Length; i++)
        {
            checksum ^= source[i];
            checksum *= 1099511628211L;
        }

        return checksum;
    }


    /// <summary>
    /// Produces a small stable checksum over one 8-byte scalar value without allocating a byte array.<br/>
    /// This supports key-only range iteration measurements where touching identity bytes would change the workload being measured.<br/>
    /// </summary>
    /// <param name="value">The scalar value.</param>
    /// <returns>The checksum contribution for the supplied scalar.</returns>
    private static long ChecksumScalar8(ulong value)
    {
        long checksum = 1469598103934665603L;
        for (int shift = 56; shift >= 0; shift -= 8)
        {
            checksum ^= (byte)(value >> shift);
            checksum *= 1099511628211L;
        }

        return checksum;
    }


    /// <summary>
    /// Produces a small stable checksum over one 16-byte scalar key without allocating a byte array.<br/>
    /// The high and low halves are folded in big-endian order to match SQLite's fixed BLOB representation.<br/>
    /// </summary>
    /// <param name="high">The high 64-bit key half.</param>
    /// <param name="low">The low 64-bit key half.</param>
    /// <returns>The checksum contribution for the supplied scalar key.</returns>
    private static long ChecksumScalar16Key(ulong high, ulong low)
    {
        long checksum = 1469598103934665603L;
        for (int shift = 56; shift >= 0; shift -= 8)
        {
            checksum ^= (byte)(high >> shift);
            checksum *= 1099511628211L;
        }

        for (int shift = 56; shift >= 0; shift -= 8)
        {
            checksum ^= (byte)(low >> shift);
            checksum *= 1099511628211L;
        }

        return checksum;
    }


    /// <summary>
    /// Creates a sequential insertion order for generated key arrays.<br/>
    /// The returned indexes preserve generation order and let callers keep key and identity generation tied to the same source index.<br/>
    /// </summary>
    /// <param name="count">The number of item indexes to create.</param>
    /// <returns>A sequential order array.</returns>
    private static int[] CreateSequentialOrder(int count)
    {
        int[] order = new int[count];
        for (int i = 0; i < order.Length; i++)
        {
            order[i] = i;
        }

        return order;
    }


    /// <summary>
    /// Creates a deterministic shuffled insertion order for repeatable random-write comparisons.<br/>
    /// A fixed seed keeps benchmark rows comparable across runs while still breaking append-like key locality.<br/>
    /// </summary>
    /// <param name="count">The number of item indexes to create.</param>
    /// <param name="seed">The deterministic shuffle seed.</param>
    /// <returns>A shuffled order array.</returns>
    private static int[] CreateDeterministicShuffledOrder(int count, int seed)
    {
        int[] order = CreateSequentialOrder(count);
        Random random = new(seed);
        for (int i = order.Length - 1; i > 0; i--)
        {
            int selected = random.Next(i + 1);
            (order[i], order[selected]) = (order[selected], order[i]);
        }

        return order;
    }


    /// <summary>
    /// Converts Stopwatch ticks to milliseconds for benchmark attribution output.<br/>
    /// Keeping the conversion centralized avoids mixing Stopwatch ticks with TimeSpan ticks in wide matrix rows.<br/>
    /// </summary>
    /// <param name="ticks">The Stopwatch tick count.</param>
    /// <returns>The equivalent elapsed milliseconds.</returns>
    private static double TicksToMilliseconds(long ticks)
    {
        return ticks * 1000.0 / Stopwatch.Frequency;
    }


    /// <summary>
    /// Validates that reopened index-directory slots still point at the expected independent root routers.<br/>
    /// This catches the core multi-index failure mode where a later slot rewrite accidentally drops or aliases an earlier index root.<br/>
    /// </summary>
    /// <param name="session">The reopened file session.</param>
    /// <param name="scalar8Scalar8SlotIndex">The expected `SS8-8` slot index.</param>
    /// <param name="scalar16Scalar8SlotIndex">The expected `SS16-8` slot index.</param>
    /// <param name="scalar8Scalar8RootOffset">The expected `SS8-8` root-router offset.</param>
    /// <param name="scalar16Scalar8RootOffset">The expected `SS16-8` root-router offset.</param>
    private static void ValidateMultiIndexDirectory(
        LibraDexFileSession session,
        int scalar8Scalar8SlotIndex,
        int scalar16Scalar8SlotIndex,
        long scalar8Scalar8RootOffset,
        long scalar16Scalar8RootOffset)
    {
        bool foundScalar8Scalar8 = false;
        bool foundScalar16Scalar8 = false;
        ReadOnlySpan<IndexDirectorySlotSnapshot> slots = session.IndexDirectory.ActiveSlots;
        for (int i = 0; i < slots.Length; i++)
        {
            IndexDirectorySlotSnapshot slot = slots[i];
            if (slot.SlotIndex == scalar8Scalar8SlotIndex)
            {
                foundScalar8Scalar8 = slot.RootRouterOffset == scalar8Scalar8RootOffset;
            }

            if (slot.SlotIndex == scalar16Scalar8SlotIndex)
            {
                foundScalar16Scalar8 = slot.RootRouterOffset == scalar16Scalar8RootOffset;
            }
        }

        if (!foundScalar8Scalar8 || !foundScalar16Scalar8)
        {
            throw new InvalidDataException("Multi-index same-identities sanity did not reopen both expected index-directory slots with their original root-router offsets.");
        }

        if (scalar8Scalar8RootOffset == scalar16Scalar8RootOffset)
        {
            throw new InvalidDataException("Multi-index same-identities sanity reopened aliased root-router offsets.");
        }
    }


    /// <summary>
    /// Creates the fixed eight-index same-identities validation plan.<br/>
    /// Slot order is stable for easy inspection, while callers use separate create/write/validate order arrays to exercise non-sequential behavior.<br/>
    /// </summary>
    /// <returns>The eight planned indexes.</returns>
    private static MultiIndexSameIdentitiesPlan[] CreateEightIndexSameIdentitiesPlans()
    {
        return
        [
            new(0, "ss88a", MultiIndexSameIdentitiesShape.SS88, 8),
            new(1, "s168a", MultiIndexSameIdentitiesShape.SS168, 8),
            new(2, "s816a", MultiIndexSameIdentitiesShape.SS816, 16),
            new(3, "s1616a", MultiIndexSameIdentitiesShape.SS1616, 16),
            new(4, "ss88b", MultiIndexSameIdentitiesShape.SS88, 8),
            new(5, "s168b", MultiIndexSameIdentitiesShape.SS168, 8),
            new(6, "s816b", MultiIndexSameIdentitiesShape.SS816, 16),
            new(7, "s1616b", MultiIndexSameIdentitiesShape.SS1616, 16)
        ];
    }


    /// <summary>
    /// Validates all planned roots are non-zero and distinct.<br/>
    /// Root aliasing would mean separate slots are not actually independent index graphs.<br/>
    /// </summary>
    /// <param name="plans">The planned indexes.</param>
    private static void ValidateDistinctEightIndexRoots(ReadOnlySpan<MultiIndexSameIdentitiesPlan> plans)
    {
        HashSet<long> roots = [];
        for (int i = 0; i < plans.Length; i++)
        {
            if (plans[i].RootOffset == 0 || !roots.Add(plans[i].RootOffset))
            {
                throw new InvalidDataException("Eight-index same-identities sanity created a missing or duplicate root-router offset.");
            }
        }
    }


    /// <summary>
    /// Validates that every planned slot survived reopen with its expected root offset.<br/>
    /// The directory check is independent of route walking so slot publication errors fail with a direct message.<br/>
    /// </summary>
    /// <param name="session">The reopened file session.</param>
    /// <param name="plans">The planned indexes.</param>
    private static void ValidateEightIndexDirectory(LibraDexFileSession session, ReadOnlySpan<MultiIndexSameIdentitiesPlan> plans)
    {
        for (int planIndex = 0; planIndex < plans.Length; planIndex++)
        {
            bool found = false;
            ReadOnlySpan<IndexDirectorySlotSnapshot> slots = session.IndexDirectory.ActiveSlots;
            for (int slotIndex = 0; slotIndex < slots.Length; slotIndex++)
            {
                IndexDirectorySlotSnapshot slot = slots[slotIndex];
                if (slot.SlotIndex == plans[planIndex].SlotIndex && slot.RootRouterOffset == plans[planIndex].RootOffset)
                {
                    found = true;
                    break;
                }
            }

            if (!found)
            {
                throw new InvalidDataException($"Eight-index same-identities sanity did not reopen slot {plans[planIndex].SlotIndex} with its expected root-router offset.");
            }
        }
    }


    /// <summary>
    /// Creates the `DataKernel` options used by isolated `SS8-8` design-performance samples.<br/>
    /// The options intentionally match the checkpoint harness defaults so syscall shape stays comparable to validation output.<br/>
    /// </summary>
    /// <returns>The `DataKernel` options for design-performance samples.</returns>
    private static DataKernelOptions CreateDesignPerfOptions()
    {
        return new DataKernelOptions(
            AppendBufferSize: DefaultAppendBufferSize,
            ReservedPrefixBytes: 0,
            FlushToDiskOnCommit: false,
            MaxCommitGapCoalesceBytes: 512);
    }


    /// <summary>
    /// Creates the `DataKernel` options used by isolated `SS8-8` design-performance samples with an explicit file commit gap-coalescing threshold.<br/>
    /// This supports narrow ladder probes without changing the default public or harness storage policy.<br/>
    /// </summary>
    /// <param name="maxCommitGapCoalesceBytes">The maximum unchanged gap bytes that file-backed commit may bridge between final commit slices.</param>
    /// <returns>The `DataKernel` options for design-performance samples.</returns>
    private static DataKernelOptions CreateDesignPerfOptions(int maxCommitGapCoalesceBytes)
    {
        return new DataKernelOptions(
            AppendBufferSize: DefaultAppendBufferSize,
            ReservedPrefixBytes: 0,
            FlushToDiskOnCommit: false,
            MaxCommitGapCoalesceBytes: maxCommitGapCoalesceBytes);
    }


    /// <summary>
    /// Creates deterministic superblock developer metadata for one `SS8-8` design-performance scenario.<br/>
    /// The seed keeps scenario files easy to distinguish without adding timing-dependent metadata to the hot measured operation.<br/>
    /// </summary>
    /// <param name="seed">The deterministic scenario seed.</param>
    /// <returns>The developer metadata used when initializing the sample file.</returns>
    private static SuperblockDeveloperMetadata CreateDesignPerfMetadata(long seed)
    {
        return new SuperblockDeveloperMetadata(
            DevIdentity: $"LibraDexSS88DesignPerf{seed}",
            DevCustomText: "ss8-8 design perf",
            DevGuid: new Guid(unchecked((int)0xBADC0DE), 0x5518, 0x0008, 0x88, 0x00, 0x00, 0x00, 0x00, 0x00, (byte)seed, (byte)(seed >> 8)),
            DevDate1UtcTicks: seed,
            DevDate2UtcTicks: seed + 1,
            DevNumber: (ulong)seed);
    }


    private static void ValidateFormatSession(
        LibraDexFileSession session,
        SuperblockDeveloperMetadata expected,
        IndexDirectorySlotSnapshot? expectedSlot = null)
    {
        SuperblockSnapshot superblock = session.Superblock;
        if (superblock.IndexDirectoryOffset != SuperblockLayout.Size)
        {
            throw new InvalidDataException("Index directory offset mismatch.");
        }

        if (superblock.IndexDirectoryLength != IndexDirectoryLayout.Size || superblock.IndexSlotCount != IndexDirectoryLayout.SlotCount)
        {
            throw new InvalidDataException("Index directory sizing mismatch.");
        }

        if (expectedSlot.HasValue)
        {
            ValidateIndexSlot(session, expectedSlot.GetValueOrDefault());
        }
        else if (!session.IndexDirectory.AllSlotsEmpty)
        {
            throw new InvalidDataException("Index directory was not empty.");
        }

        if (superblock.DeveloperMetadata != expected)
        {
            throw new InvalidDataException("Developer metadata mismatch.");
        }
    }


    private static void ValidateIndexSlot(LibraDexFileSession session, IndexDirectorySlotSnapshot expected)
    {
        ReadOnlySpan<IndexDirectorySlotSnapshot> activeSlots = session.IndexDirectory.ActiveSlots;
        if (activeSlots.Length != 1)
        {
            throw new InvalidDataException("Expected exactly one active index-directory slot.");
        }

        if (activeSlots[0] != expected)
        {
            throw new InvalidDataException("Index-directory slot mismatch.");
        }
    }


    private static void ValidateRoutingVectors(LibraDexFileSession session, long rootOffset, long childOffset)
    {
        if (childOffset <= 0)
        {
            throw new InvalidDataException("Child router offset must be nonzero.");
        }

        RouterSnapshot root = session.ReadRouterSnapshot(rootOffset);
        RouterSnapshot child = session.ReadRouterSnapshot(childOffset);
        if (!root.HasDirectIndex || child.HasDirectIndex || child.KeyDepth != 1 || child.RouteCount != 3)
        {
            throw new InvalidDataException("Routing router shape mismatch.");
        }

        ValidateRouteVector(session, rootOffset, [], 0);
        ValidateRouteVector(session, rootOffset, [0x41], 0);
        ValidateRouteVector(session, rootOffset, [0x40, 0x7F], 0);
        ValidateRouteVector(session, rootOffset, [0x42, 0x7F], 0);
        ValidateRouteVector(session, rootOffset, [0x41, 0x00], 0x0010_0000);
        ValidateRouteVector(session, rootOffset, [0x41, 0x3F], 0x0010_0000);
        ValidateRouteVector(session, rootOffset, [0x41, 0x40], 0x0020_0000);
        ValidateRouteVector(session, rootOffset, [0x41, 0x7F], 0x0020_0000);
        ValidateRouteVector(session, rootOffset, [0x41, 0x80], 0x0030_0000);
        ValidateRouteVector(session, rootOffset, [0x41, 0xFF], 0x0030_0000);
    }


    /// <summary>
    /// Creates a deterministic fixed 16-byte identity from a compact ordinal for primitive shelf validation.<br/>
    /// The high lane changes slowly while the low lane preserves a dense sortable suffix for simple point and range checks.<br/>
    /// </summary>
    /// <param name="ordinal">The compact ordinal used to derive the fixed identity.</param>
    /// <param name="identityHigh">Receives identity lane 0.</param>
    /// <param name="identityLow">Receives identity lane 1.</param>
    private static void CreateFixed16Identity(int ordinal, out ulong identityHigh, out ulong identityLow)
    {
        identityHigh = (ulong)(ordinal / 4096);
        identityLow = (ulong)ordinal;
    }


    private static int GreatestCommonDivisor(int left, int right)
    {
        while (right != 0)
        {
            int remainder = left % right;
            left = right;
            right = remainder;
        }

        return Math.Abs(left);
    }


    private static void AddCommit(DataKernelCommitTelemetry commit, ref int commits, ref long writes, ref long bytes, ref long setLength)
    {
        commits++;
        writes += commit.WriteCallCount;
        bytes += commit.BytesWritten;
        setLength += commit.SetLengthCallCount;
    }


    private static byte[] CreateRepeatedKey(int length, byte value)
    {
        byte[] key = new byte[length];
        Array.Fill(key, value);
        return key;
    }


    private readonly record struct ChangedSpan(int Offset, int Length);


    /// <summary>
    /// Prints the multi-index same-identities sanity result to the console.<br/>
    /// The result focuses on root separation, directory survival, item validation count, and write telemetry shape.<br/>
    /// </summary>
    /// <param name="result">The multi-index sanity result.</param>
    private static void PrintMultiIndexSameIdentitiesSanityResult(MultiIndexSameIdentitiesResult result)
    {
        Console.WriteLine("| index | slot | root | commits | writes | bytes | validated items | distinct shelves |");
        Console.WriteLine("|---|---:|---:|---:|---:|---:|---:|---:|");
        Console.WriteLine($"| SS8-8 | {result.Scalar8Scalar8SlotIndex} | {result.Scalar8Scalar8RootOffset} | {result.Scalar8Scalar8CommitCount} | {result.Scalar8Scalar8WriteCallCount} | {result.Scalar8Scalar8BytesWritten} | {result.Scalar8Scalar8ValidatedItems} | {result.Scalar8Scalar8DistinctShelves} |");
        Console.WriteLine($"| SS16-8 | {result.Scalar16Scalar8SlotIndex} | {result.Scalar16Scalar8RootOffset} | {result.Scalar16Scalar8CommitCount} | {result.Scalar16Scalar8WriteCallCount} | {result.Scalar16Scalar8BytesWritten} | {result.Scalar16Scalar8ValidatedItems} | {result.Scalar16Scalar8DistinctShelves} |");
        Console.WriteLine($"rootCreateWrites {result.RootCreateWriteCallCount:N0}");
        Console.WriteLine($"rootCreateBytes {result.RootCreateBytesWritten:N0}");
        Console.WriteLine($"identityChecksum {result.IdentityChecksum}");
        Console.WriteLine($"finalFileBytes {result.FinalFileBytes:N0}");
        Console.WriteLine($"setLength {result.SetLengthCallCount:N0}");
    }


    /// <summary>
    /// Prints the eight-index same-identities sanity result to the console.<br/>
    /// The table is slot ordered so duplicate shape instances can be compared without opening the markdown report.<br/>
    /// </summary>
    /// <param name="rootCreateWrites">The aggregate root-create write-call count.</param>
    /// <param name="rootCreateBytes">The aggregate root-create bytes written.</param>
    /// <param name="identityChecksum">The shared generated identity checksum.</param>
    /// <param name="finalFileBytes">The final file length.</param>
    /// <param name="plans">The validated index plans.</param>
    private static void PrintMultiIndexEightSameIdentitiesSanityResult(
        long rootCreateWrites,
        long rootCreateBytes,
        long identityChecksum,
        long finalFileBytes,
        ReadOnlySpan<MultiIndexSameIdentitiesPlan> plans)
    {
        Console.WriteLine("| slot | shape | identity B | root | commits | writes | bytes | validated items | distinct shelves |");
        Console.WriteLine("|---:|---|---:|---:|---:|---:|---:|---:|---:|");
        for (int i = 0; i < plans.Length; i++)
        {
            MultiIndexSameIdentitiesPlan plan = plans[i];
            Console.WriteLine($"| {plan.SlotIndex} | {FormatMultiIndexShape(plan.Shape)} | {plan.IdentityBytes} | {plan.RootOffset} | {plan.Telemetry.CommitCount} | {plan.Telemetry.WriteCallCount} | {plan.Telemetry.BytesWritten} | {plan.ValidatedItems} | {plan.DistinctShelves} |");
        }

        Console.WriteLine($"rootCreateWrites {rootCreateWrites:N0}");
        Console.WriteLine($"rootCreateBytes {rootCreateBytes:N0}");
        Console.WriteLine($"identityChecksum {identityChecksum}");
        Console.WriteLine($"finalFileBytes {finalFileBytes:N0}");
    }


    /// <summary>
    /// Encodes an unsigned sortable scalar into SQLite's signed integer order.<br/>
    /// Flipping the high bit maps unsigned order onto signed `INTEGER` order, so SQLite B-tree comparisons match `SS8-8` unsigned scalar route and shelf ordering.<br/>
    /// </summary>
    /// <param name="value">The encoded unsigned scalar value.</param>
    /// <returns>The signed integer value to store in SQLite.</returns>
    private static long EncodeSqliteSortableUnsignedScalar8(ulong value)
    {
        return unchecked((long)(value ^ 0x8000_0000_0000_0000UL));
    }


    /// <summary>
    /// Decodes a SQLite signed integer value back to the unsigned sortable scalar identity.<br/>
    /// This is the inverse of `EncodeSqliteSortableUnsignedScalar8` and preserves byte-for-byte identity validation against LibraDex expected keys.<br/>
    /// </summary>
    /// <param name="value">The signed SQLite integer value.</param>
    /// <returns>The decoded unsigned scalar value.</returns>
    private static ulong DecodeSqliteSortableUnsignedScalar8(long value)
    {
        return unchecked((ulong)value) ^ 0x8000_0000_0000_0000UL;
    }


    /// <summary>
    /// Executes the raw 16-byte item stream write loop and aggregates commit telemetry.<br/>
    /// The hot loop writes canonical scalar key and identity values directly into the reserved span.<br/>
    /// </summary>
    /// <param name="kernel">The active `DataKernel`.</param>
    /// <param name="batches">The number of committed batches to execute.</param>
    /// <param name="itemsPerBatch">The number of items in each batch.</param>
    /// <param name="order">The deterministic item order vector.</param>
    /// <returns>Aggregated write telemetry and checksum.</returns>
    private static WriteParityLoopTelemetry RunDataKernelItemStreamWriteParityLoop(DataKernel kernel, int batches, int itemsPerBatch, int[] order)
    {
        WriteParityLoopTelemetry telemetry = default;
        int batchBytes = checked(itemsPerBatch * Scalar8Scalar8Layout.ItemSize);
        for (int batch = 0; batch < batches; batch++)
        {
            RawDataReservation reservation = kernel.Reserve(batchBytes);
            FillWriteParityItems(reservation.Span, itemsPerBatch, order, batch);
            AddWriteParityCommit(kernel.Commit(), ref telemetry);
            telemetry.Checksum += reservation.Extent.Length;
        }

        return telemetry;
    }


    /// <summary>
    /// Executes the raw fixed-width payload stream write loop and aggregates commit telemetry.<br/>
    /// Payload bytes are generated from the same key cadence as routed rows, then expanded into 8-byte lanes so wider shape floors remain deterministic.<br/>
    /// </summary>
    /// <param name="kernel">The active `DataKernel`.</param>
    /// <param name="batches">The number of committed batches to execute.</param>
    /// <param name="itemsPerBatch">The number of items in each batch.</param>
    /// <param name="payloadBytes">The fixed logical payload bytes per item.</param>
    /// <param name="order">The deterministic item order vector.</param>
    /// <returns>Aggregated write telemetry and checksum.</returns>
    private static WriteParityLoopTelemetry RunDataKernelPayloadStreamWriteParityLoop(DataKernel kernel, int batches, int itemsPerBatch, int payloadBytes, int[] order)
    {
        WriteParityLoopTelemetry telemetry = default;
        int batchBytes = checked(itemsPerBatch * payloadBytes);
        for (int batch = 0; batch < batches; batch++)
        {
            RawDataReservation reservation = kernel.Reserve(batchBytes);
            FillPayloadWriteParityItems(reservation.Span, itemsPerBatch, payloadBytes, order, batch);
            AddWriteParityCommit(kernel.Commit(), ref telemetry);
            telemetry.Checksum += reservation.Extent.Length;
        }

        return telemetry;
    }


    /// <summary>
    /// Writes deterministic 16-byte `(key, identity)` payloads into a raw reserved span.<br/>
    /// The method uses the `SS8-8` scalar item writer so raw rows use the same canonical scalar byte encoding as shelf rows.<br/>
    /// </summary>
    /// <param name="target">The destination span.</param>
    /// <param name="itemsPerBatch">The number of logical items to write.</param>
    /// <param name="order">The deterministic item order vector.</param>
    /// <param name="batch">The current batch ordinal.</param>
    private static void FillWriteParityItems(Span<byte> target, int itemsPerBatch, int[] order, int batch)
    {
        for (int i = 0; i < itemsPerBatch; i++)
        {
            ulong value = (ulong)order[i] + ((ulong)batch << 32);
            int offset = i * Scalar8Scalar8Layout.ItemSize;
            Scalar8Scalar8Layout.WriteItemKey(target, offset, Scalar8Scalar8Layout.EncodeUnsignedScalar8(value));
            Scalar8Scalar8Layout.WriteItemIdentity(target, offset, Scalar8Scalar8Layout.EncodeUnsignedScalar8(value));
        }
    }


    /// <summary>
    /// Writes deterministic fixed-width raw payload rows into a reserved span.<br/>
    /// Each row is filled as 8-byte little-endian lanes derived from the generated key and lane ordinal, preserving a stable non-zero payload for 16-, 24-, and 32-byte raw floors.<br/>
    /// </summary>
    /// <param name="target">The destination span.</param>
    /// <param name="itemsPerBatch">The number of logical items to write.</param>
    /// <param name="payloadBytes">The fixed payload bytes per item.</param>
    /// <param name="order">The deterministic item order vector.</param>
    /// <param name="batch">The current batch ordinal.</param>
    private static void FillPayloadWriteParityItems(Span<byte> target, int itemsPerBatch, int payloadBytes, int[] order, int batch)
    {
        int laneCount = payloadBytes / sizeof(ulong);
        for (int i = 0; i < itemsPerBatch; i++)
        {
            ulong value = (ulong)order[i] + ((ulong)batch << 32);
            int rowOffset = i * payloadBytes;
            for (int lane = 0; lane < laneCount; lane++)
            {
                ulong laneValue = unchecked(value ^ ((ulong)lane * 0x9E37_79B9_7F4A_7C15UL));
                WriteLittleEndianUInt64(target, rowOffset + (lane * sizeof(ulong)), laneValue);
            }
        }
    }


    /// <summary>
    /// Writes one unsigned 64-bit value to a span as little-endian bytes.<br/>
    /// Raw payload-stream rows do not participate in sort order, so little-endian writes keep the tight fill loop simple and platform-independent.<br/>
    /// </summary>
    /// <param name="target">The destination span.</param>
    /// <param name="offset">The starting byte offset.</param>
    /// <param name="value">The value to write.</param>
    private static void WriteLittleEndianUInt64(Span<byte> target, int offset, ulong value)
    {
        for (int i = 0; i < sizeof(ulong); i++)
        {
            target[offset + i] = (byte)(value >> (i * 8));
        }
    }


    /// <summary>
    /// Adds one commit telemetry object into the aggregate write-parity telemetry accumulator.<br/>
    /// Keeping this explicit makes commit count, write-call count, bytes, and `SetLength` easy to audit in reports.<br/>
    /// </summary>
    /// <param name="commit">The commit telemetry to add.</param>
    /// <param name="telemetry">The aggregate telemetry accumulator.</param>
    private static void AddWriteParityCommit(DataKernelCommitTelemetry commit, ref WriteParityLoopTelemetry telemetry)
    {
        telemetry.CommitCount++;
        telemetry.WriteCallCount += commit.WriteCallCount;
        telemetry.BytesWritten += commit.BytesWritten;
        telemetry.SetLengthCallCount += commit.SetLengthCallCount;
    }


    /// <summary>
    /// Measures one raw positional write shape over a pre-created rotating file region set.<br/>
    /// The warmup loop executes the same syscall shape before timing to reduce first-touch noise.<br/>
    /// </summary>
    /// <param name="handle">The open file handle.</param>
    /// <param name="shape">The operation shape to measure.</param>
    /// <param name="buffer">The caller-owned write buffer.</param>
    /// <param name="iterations">The number of measured operations.</param>
    /// <param name="warmupIterations">The number of unmeasured warmup operations.</param>
    /// <param name="regionStride">The byte stride between rotating regions.</param>
    /// <param name="regionCount">The number of rotating regions.</param>
    /// <returns>The measured small-I/O baseline row.</returns>
    private static SmallIoBaselineResult MeasureSmallIoWriteShape(
        Microsoft.Win32.SafeHandles.SafeFileHandle handle,
        SmallIoShape shape,
        byte[] buffer,
        int iterations,
        int warmupIterations,
        int regionStride,
        int regionCount)
    {
        RunSmallIoWriteLoop(handle, shape, buffer, warmupIterations, regionStride, regionCount);
        Stopwatch watch = Stopwatch.StartNew();
        long checksum = RunSmallIoWriteLoop(handle, shape, buffer, iterations, regionStride, regionCount);
        watch.Stop();
        return CreateSmallIoBaselineResult(shape, "write", iterations, watch.Elapsed, checksum);
    }


    /// <summary>
    /// Measures one raw positional read shape over a pre-created rotating file region set.<br/>
    /// The warmup loop executes the same syscall shape before timing to reduce first-touch noise.<br/>
    /// </summary>
    /// <param name="handle">The open file handle.</param>
    /// <param name="shape">The operation shape to measure.</param>
    /// <param name="buffer">The caller-owned read buffer.</param>
    /// <param name="iterations">The number of measured operations.</param>
    /// <param name="warmupIterations">The number of unmeasured warmup operations.</param>
    /// <param name="regionStride">The byte stride between rotating regions.</param>
    /// <param name="regionCount">The number of rotating regions.</param>
    /// <returns>The measured small-I/O baseline row.</returns>
    private static SmallIoBaselineResult MeasureSmallIoReadShape(
        Microsoft.Win32.SafeHandles.SafeFileHandle handle,
        SmallIoShape shape,
        byte[] buffer,
        int iterations,
        int warmupIterations,
        int regionStride,
        int regionCount)
    {
        RunSmallIoReadLoop(handle, shape, buffer, warmupIterations, regionStride, regionCount);
        Stopwatch watch = Stopwatch.StartNew();
        long checksum = RunSmallIoReadLoop(handle, shape, buffer, iterations, regionStride, regionCount);
        watch.Stop();
        return CreateSmallIoBaselineResult(shape, "read", iterations, watch.Elapsed, checksum);
    }


    /// <summary>
    /// Executes the raw positional write loop for a single small-I/O shape.<br/>
    /// Each operation writes all configured segments contiguously inside one rotating region.<br/>
    /// </summary>
    /// <param name="handle">The open file handle.</param>
    /// <param name="shape">The write shape to execute.</param>
    /// <param name="buffer">The source write buffer.</param>
    /// <param name="iterations">The number of operations to execute.</param>
    /// <param name="regionStride">The byte stride between rotating regions.</param>
    /// <param name="regionCount">The number of rotating regions.</param>
    /// <returns>A checksum that prevents the loop from becoming semantically empty.</returns>
    private static long RunSmallIoWriteLoop(
        Microsoft.Win32.SafeHandles.SafeFileHandle handle,
        SmallIoShape shape,
        byte[] buffer,
        int iterations,
        int regionStride,
        int regionCount)
    {
        int[] segments = shape.Segments;
        long checksum = 0;
        for (int i = 0; i < iterations; i++)
        {
            long baseOffset = (long)(i % regionCount) * regionStride;
            int segmentOffset = 0;
            for (int segmentIndex = 0; segmentIndex < segments.Length; segmentIndex++)
            {
                int segmentLength = segments[segmentIndex];
                RandomAccess.Write(handle, buffer.AsSpan(segmentOffset, segmentLength), baseOffset + segmentOffset);
                segmentOffset += segmentLength;
                checksum += segmentLength;
            }
        }

        return checksum;
    }


    /// <summary>
    /// Executes the raw positional read loop for a single small-I/O shape.<br/>
    /// Each operation reads all configured segments contiguously inside one rotating region.<br/>
    /// </summary>
    /// <param name="handle">The open file handle.</param>
    /// <param name="shape">The read shape to execute.</param>
    /// <param name="buffer">The destination read buffer.</param>
    /// <param name="iterations">The number of operations to execute.</param>
    /// <param name="regionStride">The byte stride between rotating regions.</param>
    /// <param name="regionCount">The number of rotating regions.</param>
    /// <returns>A checksum over read bytes that prevents the loop from becoming semantically empty.</returns>
    private static long RunSmallIoReadLoop(
        Microsoft.Win32.SafeHandles.SafeFileHandle handle,
        SmallIoShape shape,
        byte[] buffer,
        int iterations,
        int regionStride,
        int regionCount)
    {
        int[] segments = shape.Segments;
        long checksum = 0;
        for (int i = 0; i < iterations; i++)
        {
            long baseOffset = (long)(i % regionCount) * regionStride;
            int segmentOffset = 0;
            for (int segmentIndex = 0; segmentIndex < segments.Length; segmentIndex++)
            {
                int segmentLength = segments[segmentIndex];
                int read = RandomAccess.Read(handle, buffer.AsSpan(segmentOffset, segmentLength), baseOffset + segmentOffset);
                if (read != segmentLength)
                {
                    throw new EndOfStreamException($"Expected {segmentLength} bytes at offset {baseOffset + segmentOffset}, read {read}.");
                }

                checksum += buffer[segmentOffset];
                segmentOffset += segmentLength;
            }
        }

        return checksum;
    }


    /// <summary>
    /// Creates the final small-I/O baseline result row from elapsed time and operation shape metadata.<br/>
    /// Throughput is calculated from operation bytes multiplied by measured operation count.<br/>
    /// </summary>
    /// <param name="shape">The measured operation shape.</param>
    /// <param name="mode">The row mode, normally `read` or `write`.</param>
    /// <param name="iterations">The number of measured operations.</param>
    /// <param name="elapsed">The measured elapsed time.</param>
    /// <param name="checksum">The measured checksum.</param>
    /// <returns>The completed small-I/O baseline row.</returns>
    private static SmallIoBaselineResult CreateSmallIoBaselineResult(
        SmallIoShape shape,
        string mode,
        int iterations,
        TimeSpan elapsed,
        long checksum)
    {
        double seconds = Math.Max(elapsed.TotalSeconds, 0.000000001d);
        double totalBytes = (double)shape.TotalBytes * iterations;
        return new SmallIoBaselineResult(
            shape.Name,
            mode,
            shape.Segments.Length,
            shape.TotalBytes,
            iterations,
            elapsed,
            elapsed.TotalMilliseconds * 1000d / iterations,
            CalculateMiBs(totalBytes, seconds),
            iterations * (double)shape.Segments.Length / seconds,
            checksum);
    }


    /// <summary>
    /// Creates nearly even segment sizes for scenarios where the total bytes and syscall count matter more than a specific per-call split.<br/>
    /// Any remainder is assigned one byte at a time to the earliest segments so the exact total is preserved.<br/>
    /// </summary>
    /// <param name="totalBytes">The total bytes per operation.</param>
    /// <param name="segmentCount">The number of segments per operation.</param>
    /// <returns>The segment byte sizes.</returns>
    private static int[] CreateEvenSegments(int totalBytes, int segmentCount)
    {
        if (totalBytes <= 0 || segmentCount <= 0 || segmentCount > totalBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(segmentCount), segmentCount, "Small-I/O segment count must be positive and no larger than the total byte count.");
        }

        int[] segments = new int[segmentCount];
        int baseLength = totalBytes / segmentCount;
        int remainder = totalBytes - (baseLength * segmentCount);
        for (int i = 0; i < segments.Length; i++)
        {
            segments[i] = baseLength + (i < remainder ? 1 : 0);
        }

        return segments;
    }


    /// <summary>
    /// Finds the largest per-operation byte count among the supplied small-I/O shapes.<br/>
    /// The value becomes the rotating region stride so every shape fits within one region.<br/>
    /// </summary>
    /// <param name="writeShapes">The write shapes to inspect.</param>
    /// <param name="readShapes">The read shapes to inspect.</param>
    /// <returns>The largest operation byte count.</returns>
    private static int GetMaxSmallIoShapeBytes(SmallIoShape[] writeShapes, SmallIoShape[] readShapes)
    {
        int max = 0;
        for (int i = 0; i < writeShapes.Length; i++)
        {
            max = Math.Max(max, writeShapes[i].TotalBytes);
        }

        for (int i = 0; i < readShapes.Length; i++)
        {
            max = Math.Max(max, readShapes[i].TotalBytes);
        }

        return max;
    }


    private static byte[] CreatePattern(int length, int seed)
    {
        byte[] buffer = new byte[length];
        for (int i = 0; i < buffer.Length; i++)
        {
            buffer[i] = unchecked((byte)((i * 31 + seed * 17) & 0xFF));
        }

        return buffer;
    }


    /// <summary>
    /// Adds stage, commit, read, and syscall-shape metrics for a raw performance result set.<br/>
    /// Throughput is compared against recorded baselines while syscall counts are compared against exact expected values.<br/>
    /// </summary>
    /// <param name="metrics">The validation metric collection to append to.</param>
    /// <param name="name">The metric name prefix.</param>
    /// <param name="results">The measured raw performance results.</param>
    /// <param name="backing">The backing kind measured.</param>
    /// <param name="totalBytes">The total measured bytes per run.</param>
    /// <param name="stageBaselineMiBs">The stage throughput baseline in MiB/s.</param>
    /// <param name="commitBaselineMiBs">The commit throughput baseline in MiB/s, or null when not comparable.</param>
    /// <param name="readBaselineMiBs">The read throughput baseline in MiB/s.</param>
    private static void AddPerfMetrics(
        List<ValidationMetric> metrics,
        string name,
        PerfRawResult[] results,
        DataKernelBackingKind backing,
        long totalBytes,
        double stageBaselineMiBs,
        double? commitBaselineMiBs,
        double readBaselineMiBs)
    {
        double totalMiB = totalBytes / 1024d / 1024d;
        double[] stageMiBs = new double[results.Length];
        double[] commitMiBs = new double[results.Length];
        double[] readMiBs = new double[results.Length];

        for (int i = 0; i < results.Length; i++)
        {
            stageMiBs[i] = totalMiB / Math.Max(results[i].StageElapsed.TotalSeconds, 0.000001);
            commitMiBs[i] = totalMiB / Math.Max(results[i].CommitElapsed.TotalSeconds, 0.000001);
            readMiBs[i] = totalMiB / Math.Max(results[i].ReadElapsed.TotalSeconds, 0.000001);
        }

        DataKernelCommitTelemetry commit = results[0].CommitTelemetry;
        DataKernelReadTelemetry read = results[0].ReadTelemetry;
        long expectedWrites = backing == DataKernelBackingKind.File && totalBytes > 0
            ? 1
            : 0;
        long expectedReads = backing == DataKernelBackingKind.File
            ? (totalBytes + ValidationBlockSize - 1) / ValidationBlockSize
            : 0;

        metrics.Add(CreateHigherIsBetterMetric($"{name} stage", Mean(stageMiBs), stageBaselineMiBs, "MiB/s"));
        if (commitBaselineMiBs.HasValue)
        {
            metrics.Add(CreateHigherIsBetterMetric($"{name} commit", Mean(commitMiBs), commitBaselineMiBs.GetValueOrDefault(), "MiB/s"));
        }

        metrics.Add(CreateHigherIsBetterMetric($"{name} read", Mean(readMiBs), readBaselineMiBs, "MiB/s"));
        metrics.Add(CreateExactMetric($"{name} write syscalls", commit.WriteCallCount, expectedWrites, "calls"));
        metrics.Add(CreateExactMetric($"{name} read syscalls", read.ReadCallCount, expectedReads, "calls"));
        metrics.Add(CreateExactMetric($"{name} SetLength syscalls", commit.SetLengthCallCount, 0, "calls"));
        metrics.Add(CreateExactMetric($"{name} flush syscalls", commit.FlushCallCount, 0, "calls"));
    }


    private static void PrintStorageBaselineSet(StorageBaselineMode mode, int blockSize, StorageBaselineResult[] results)
    {
        double[] writeMiBs = new double[results.Length];
        double[] readMiBs = new double[results.Length];

        for (int i = 0; i < results.Length; i++)
        {
            double totalMiB = results[i].TotalBytes / 1024d / 1024d;
            writeMiBs[i] = totalMiB / Math.Max(results[i].WriteElapsed.TotalSeconds, 0.000001);
            readMiBs[i] = totalMiB / Math.Max(results[i].ReadElapsed.TotalSeconds, 0.000001);
        }

        Console.WriteLine($"| {mode} | {blockSize} | {results.Length} | {Mean(writeMiBs):N2} | {writeMiBs.Min():N2} | {writeMiBs.Max():N2} | {StdDev(writeMiBs):N2} | {results[0].WriteCalls} | {Mean(readMiBs):N2} | {readMiBs.Min():N2} | {readMiBs.Max():N2} | {StdDev(readMiBs):N2} | {results[0].ReadCalls} |");
    }


    private static DataKernel OpenKernel(
        DataKernelBackingKind backing,
        string path,
        FileMode mode,
        DataKernelOptions options,
        DataKernelTelemetryOptions telemetryOptions)
    {
        return backing == DataKernelBackingKind.File
            ? DataKernel.Open(path, mode, options, telemetryOptions)
            : DataKernel.OpenMemory(options, telemetryOptions);
    }


    private readonly record struct StorageBaselineResult(
        TimeSpan WriteElapsed,
        long WriteCalls,
        TimeSpan ReadElapsed,
        long ReadCalls,
        long TotalBytes);


    private readonly record struct CommitCoalescingSanityResult(
        long WriteCallCount,
        long BackingWriteCallCount,
        long BytesWritten,
        long Checksum);


    private readonly record struct PerfRawResult(
        TimeSpan StageElapsed,
        TimeSpan CommitElapsed,
        TimeSpan ReadElapsed,
        DataKernelCommitTelemetry CommitTelemetry,
        DataKernelReadTelemetry ReadTelemetry,
        long TotalBytes);


    private readonly record struct ValidationTierOptions(
        string Name,
        long TotalBytes,
        int BlockSize,
        int AppendBufferSize,
        int PerfRuns,
        int StressIterations);


    private readonly record struct ValidationMetric(
        string Name,
        double Actual,
        double Baseline,
        double DriftPercent,
        string Unit,
        ValidationStatus Status,
        bool IsShapeMetric);


    private readonly record struct FormatStressResult(
        int Iterations,
        TimeSpan Elapsed,
        double IterationsPerSecond);


    private readonly record struct SmallIoShape(
        string Name,
        int[] Segments)
    {
        public int TotalBytes
        {
            get
            {
                int total = 0;
                for (int i = 0; i < Segments.Length; i++)
                {
                    total += Segments[i];
                }

                return total;
            }
        }
    }


    private readonly record struct SmallIoBaselineResult(
        string Name,
        string Mode,
        int CallsPerOperation,
        int BytesPerOperation,
        int Iterations,
        TimeSpan Elapsed,
        double MicrosecondsPerOperation,
        double MiBs,
        double CallsPerSecond,
        long Checksum);


    private struct WriteParityLoopTelemetry
    {
        public long CommitCount;
        public long WriteCallCount;
        public long BytesWritten;
        public long SetLengthCallCount;
        public long Checksum;
    }


    private readonly record struct MultiIndexSameIdentitiesResult(
        int Scalar8Scalar8SlotIndex,
        int Scalar16Scalar8SlotIndex,
        long Scalar8Scalar8RootOffset,
        long Scalar16Scalar8RootOffset,
        long RootCreateWriteCallCount,
        long RootCreateBytesWritten,
        long Scalar8Scalar8CommitCount,
        long Scalar16Scalar8CommitCount,
        long Scalar8Scalar8WriteCallCount,
        long Scalar16Scalar8WriteCallCount,
        long Scalar8Scalar8BytesWritten,
        long Scalar16Scalar8BytesWritten,
        long SetLengthCallCount,
        long IdentityChecksum,
        long FinalFileBytes,
        int Scalar8Scalar8DistinctShelves,
        int Scalar16Scalar8DistinctShelves,
        int Scalar8Scalar8ValidatedItems,
        int Scalar16Scalar8ValidatedItems);


    private readonly record struct MultiIndexValidationCounts(
        int Scalar8Scalar8DistinctShelves,
        int Scalar16Scalar8DistinctShelves,
        int Scalar8Scalar8ValidatedItems,
        int Scalar16Scalar8ValidatedItems);


    private enum MultiIndexSameIdentitiesShape
    {
        SS88 = 0,
        SS168 = 1,
        SS816 = 2,
        SS1616 = 3
    }


    private sealed class MultiIndexSameIdentitiesPlan
    {
        internal MultiIndexSameIdentitiesPlan(int slotIndex, string name, MultiIndexSameIdentitiesShape shape, int identityBytes)
        {
            SlotIndex = slotIndex;
            Name = name;
            Shape = shape;
            IdentityBytes = identityBytes;
        }

        internal int SlotIndex { get; }

        internal string Name { get; }

        internal MultiIndexSameIdentitiesShape Shape { get; }

        internal int IdentityBytes { get; }

        internal long RootOffset { get; set; }

        internal Scalar8Scalar8IndexHandle Scalar8Scalar8Handle { get; set; }

        internal RoutedBulkWriteLoopTelemetry Telemetry { get; set; }

        internal int DistinctShelves { get; set; }

        internal int ValidatedItems { get; set; }
    }


    private enum StorageBaselineMode
    {
        Chunked,
        HugeBuffer
    }


    private enum AppendMode
    {
        Append,
        Reserve
    }


    private enum ValidationStatus
    {
        Pass,
        Warn,
        Fail
    }


    private static byte GetScalar16PrefixForHarness(ulong encodedHigh, ulong encodedLow, int keyDepth)
    {
        if ((uint)keyDepth >= Scalar16Scalar8Layout.KeySize)
        {
            throw new ArgumentOutOfRangeException(nameof(keyDepth), keyDepth, "SS16 scalar keys expose exactly sixteen routing bytes.");
        }

        if (keyDepth < sizeof(ulong))
        {
            int highShift = (sizeof(ulong) - 1 - keyDepth) * 8;
            return (byte)(encodedHigh >> highShift);
        }

        int lowDepth = keyDepth - sizeof(ulong);
        int lowShift = (sizeof(ulong) - 1 - lowDepth) * 8;
        return (byte)(encodedLow >> lowShift);
    }

    private enum ProofStatus
    {
        One = 1,
        Two = 2,
        Three = 3,
        Four = 4
    }

    private enum ProofShortStatus : short
    {
        One = 1,
        Two = 2,
        Four = 4
    }

    private enum ProofWideStatus : long
    {
        TooLargeForByte = 300
    }

    [Flags]
    private enum ProofPermissions : uint
    {
        Read = 0x01,
        Write = 0x02,
        Execute = 0x04
    }

}
