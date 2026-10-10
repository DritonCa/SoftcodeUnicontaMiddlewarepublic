using Uniconta.Common;

namespace SoftcodeUnicontaMiddleware.Services;

/// <summary>
/// How a webshop order is mapped onto the Uniconta company. Bound from the
/// "Orders" configuration section, so each company's own groups and codes are
/// configuration rather than code.
/// </summary>
public class OrderOptions
{
    public const string Section = "Orders";

    /// <summary>Uniconta country for new debtors and deliveries, e.g. "Denmark".</summary>
    public string Country { get; set; } = "Denmark";

    /// <summary>VAT code put on new debtors.</summary>
    public string DebtorVatCode { get; set; } = "U25";

    /// <summary>Debtor group per customer type. The groups must exist in the Uniconta company.</summary>
    public Dictionary<string, string> DebtorGroups { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["privat"] = "PRIV",
        ["cvr"] = "CVR",
        ["ean"] = "EAN",
    };

    /// <summary>Requisition on the sales order; {0} is the webshop order id.</summary>
    public string RequisitionFormat { get; set; } = "Web order {0}";

    /// <summary>
    /// Remove the sales order from Uniconta's open orders once it has been invoiced.
    /// Uniconta only does this when the order's own DeleteOrder flag is set.
    /// </summary>
    public bool DeleteOrderWhenInvoiced { get; set; } = true;

    public CountryCode CountryCode =>
        Enum.TryParse<CountryCode>(Country, ignoreCase: true, out var code) ? code : CountryCode.Denmark;

    public string GroupFor(string? customerType) =>
        DebtorGroups.TryGetValue(customerType ?? "privat", out var group)
            ? group
            : DebtorGroups.GetValueOrDefault("privat", "PRIV");
}
