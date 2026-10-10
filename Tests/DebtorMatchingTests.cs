using SoftcodeUnicontaMiddleware.Models.Orders;
using SoftcodeUnicontaMiddleware.Services;
using Uniconta.DataModel;
using Xunit;

namespace SoftcodeUnicontaMiddleware.Tests;

/// <summary>
/// Matching an order to an EXISTING debtor. Get this wrong and Uniconta ends up
/// with two customers for the same school, which has to be merged by hand.
///
/// The typical case: a school has a debtor on "Parkvej 109b". A new order arrives
/// on the same EAN from a new contact person, with the address written "Parkvej
/// 109B". Comparing only e-mail and address case-sensitively creates a duplicate.
/// </summary>
public class DebtorMatchingTests
{
    private const string Ean = "5790000000001";

    private static Debtor D(string account, string? ean = null, string? cvr = null,
                            string? address = null, string? zip = null, string? city = null,
                            string? email = null) =>
        new()
        {
            _Account      = account,
            _EAN          = ean ?? "",
            _VatNumber    = cvr ?? "",
            _Address1     = address ?? "",
            _ZipCode      = zip ?? "",
            _City         = city ?? "",
            _ContactEmail = email ?? "",
        };

    private static OrderRequest Req(string type = "ean", string? ean = Ean, string? cvr = null,
                                    string? address = null, string? zip = null, string? city = null,
                                    string? email = null) =>
        new()
        {
            CustomerType     = type,
            Ean              = ean,
            Cvr              = cvr,
            DeliveryAddress  = address ?? "",
            DeliveryPostcode = zip ?? "",
            DeliveryCity     = city ?? "",
            Email            = email ?? "",
        };

    [Fact]
    public void School_matches_on_EAN_although_person_and_letter_case_differ()
    {
        var debtors = new[]
        {
            D("110241", ean: Ean, address: "Parkvej 109b", zip: "4700", city: "Exampletown"),
        };

        var konto = OrderService.FindDebtorAccount(
            debtors,
            Req(address: "Parkvej 109B", zip: "4700", city: "Exampletown", email: "ny.person@example.com"),
            out var regel);

        Assert.Equal("110241", konto);
        Assert.Equal("EAN", regel);
    }

    [Fact]
    public void Several_debtors_on_the_same_EAN_pick_the_one_whose_address_fits()
    {
        var debtors = new[]
        {
            D("110241", ean: Ean, address: "Parkvej 109b",  zip: "4700", city: "Exampletown"),
            D("112287", ean: Ean, address: "Eksempelvej 2",    zip: "4700", city: "Exampletown"),
        };

        var konto = OrderService.FindDebtorAccount(
            debtors, Req(address: "Eksempelvej 2", zip: "4700", city: "Exampletown"), out var regel);

        Assert.Equal("112287", konto);
        Assert.StartsWith("EAN (2 debtors", regel);
    }

    [Fact]
    public void Equally_good_matches_fall_back_to_the_oldest_account()
    {
        var debtors = new[]
        {
            D("112287", ean: Ean, address: "Parkvej 109B", zip: "4700", city: "Exampletown"),
            D("110241", ean: Ean, address: "Parkvej 109b", zip: "4700", city: "Exampletown"),
        };

        var konto = OrderService.FindDebtorAccount(
            debtors, Req(address: "Parkvej 109B", zip: "4700", city: "Exampletown"), out _);

        Assert.Equal("110241", konto);
    }

    [Fact]
    public void EAN_wins_over_a_stale_e_mail_on_another_debtor()
    {
        var debtors = new[]
        {
            D("100001", email: "indkoeb@example.com", address: "Et helt andet sted 1"),
            D("110241", ean: Ean, address: "Parkvej 109b", zip: "4700"),
        };

        var konto = OrderService.FindDebtorAccount(
            debtors, Req(address: "Parkvej 109b", zip: "4700", email: "indkoeb@example.com"),
            out var regel);

        Assert.Equal("110241", konto);
        Assert.Equal("EAN", regel);
    }

    [Fact]
    public void EAN_is_compared_on_digits_only_so_pasted_spaces_do_not_matter()
    {
        var debtors = new[] { D("110241", ean: "5790 000 000 001", address: "Parkvej 109b") };

        var konto = OrderService.FindDebtorAccount(debtors, Req(ean: Ean), out var regel);

        Assert.Equal("110241", konto);
        Assert.Equal("EAN", regel);
    }

    [Fact]
    public void CVR_matches_the_same_way()
    {
        var debtors = new[] { D("120500", cvr: "12345678", address: "Erhvervsvej 4") };

        var konto = OrderService.FindDebtorAccount(
            debtors, Req(type: "cvr", ean: null, cvr: "12 34 56 78"), out var regel);

        Assert.Equal("120500", konto);
        Assert.Equal("CVR", regel);
    }

    [Fact]
    public void E_mail_matches_regardless_of_letter_case()
    {
        var debtors = new[] { D("130010", email: "Hanne@Example.com", address: "Vej 1") };

        var konto = OrderService.FindDebtorAccount(
            debtors, Req(type: "privat", ean: null, email: "hanne@example.com"), out var regel);

        Assert.Equal("130010", konto);
        Assert.Equal("e-mail", regel);
    }

    [Fact]
    public void Address_still_matches_as_a_last_resort_and_now_ignores_letter_case()
    {
        var debtors = new[] { D("140020", address: "Parkvej 109b", zip: "4700") };

        var konto = OrderService.FindDebtorAccount(
            debtors, Req(type: "privat", ean: null, address: "PARKVEJ  109B", zip: "4700"),
            out var regel);

        Assert.Equal("140020", konto);
        Assert.Equal("address", regel);
    }

    [Fact]
    public void An_unknown_customer_still_gets_a_new_debtor()
    {
        var debtors = new[] { D("110241", ean: Ean, address: "Parkvej 109b") };

        var konto = OrderService.FindDebtorAccount(
            debtors, Req(ean: "5790000000000", address: "Helt ny vej 7", email: "ny@example.com"),
            out var regel);

        Assert.Null(konto);
        Assert.Equal("none", regel);
    }

    [Fact]
    public void An_empty_EAN_on_an_existing_debtor_never_matches_an_empty_request()
    {
        // Most debtors have no EAN at all. Two blanks must not be "the same
        // number", or the first EAN-less debtor in the ledger would swallow
        // every private order.
        var debtors = new[] { D("150030", ean: "", address: "Vej 1") };

        var konto = OrderService.FindDebtorAccount(
            debtors, Req(ean: "", address: "Anden vej 9"), out var regel);

        Assert.Null(konto);
        Assert.Equal("none", regel);
    }

    // ---- Hvor meget maa adressen se anderledes ud -------------------------

    [Theory]
    // Det oprindelige tilfaelde: kun bogstavet er stort
    [InlineData("Parkvej 109b",        "Parkvej 109B")]
    // A space before the house letter — at least as common
    [InlineData("Parkvej 109b",        "Parkvej 109 B")]
    [InlineData("Parkvej 109 b",       "Parkvej 109B")]
    // Double spaces and spaces at the ends
    [InlineData("Parkvej 109b",        "  parkvej   109b  ")]
    // Commas and full stops
    [InlineData("Eksempelvej 2, st.",     "Eksempelvej 2 st")]
    [InlineData("Eksempelvej 2 st",       "eksempelvej 2, ST.")]
    // All at once
    [InlineData("  EKSEMPELVEJ  2,  ST. ", "eksempelvej 2 st")]
    public void The_same_address_written_differently_still_matches(string iUniconta, string fraKassen)
    {
        var debtors = new[] { D("160040", address: iUniconta, zip: "4700") };

        var konto = OrderService.FindDebtorAccount(
            debtors, Req(type: "privat", ean: null, address: fraKassen, zip: "4700"), out var regel);

        Assert.Equal("160040", konto);
        Assert.Equal("address", regel);
    }

    [Theory]
    // A different house number is a DIFFERENT customer and must NEVER merge
    [InlineData("Parkvej 109b", "Parkvej 110b")]
    [InlineData("Parkvej 109b", "Parkvej 109c")]
    [InlineData("Parkvej 109",  "Parkvej 109b")]
    // And a different street too
    [InlineData("Parkvej 109b", "Eksempelvej 109b")]
    public void Addresses_that_are_genuinely_different_do_not_match(string iUniconta, string fraKassen)
    {
        var debtors = new[] { D("160040", address: iUniconta, zip: "4700") };

        var konto = OrderService.FindDebtorAccount(
            debtors, Req(type: "privat", ean: null, address: fraKassen, zip: "4700"), out var regel);

        Assert.Null(konto);
        Assert.Equal("none", regel);
    }

    [Fact]
    public void The_house_letter_counts_when_two_units_share_an_EAN()
    {
        // Same EAN, two entrances. The spelling must not decide which one is chosen.
        var debtors = new[]
        {
            D("110241", ean: Ean, address: "Parkvej 109 A", zip: "4700"),
            D("110242", ean: Ean, address: "Parkvej 109 B", zip: "4700"),
        };

        var konto = OrderService.FindDebtorAccount(
            debtors, Req(address: "parkvej 109b", zip: "4700"), out _);

        Assert.Equal("110242", konto);
    }
}
