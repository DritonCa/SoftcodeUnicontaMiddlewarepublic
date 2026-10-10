using SoftcodeUnicontaMiddleware.Models.Orders;
using OrderResult = SoftcodeUnicontaMiddleware.Models.Orders.OrderResult;
using SoftcodeUnicontaMiddleware.UnicontaService;
using Uniconta.ClientTools.DataModel;
using Uniconta.Common;
using Uniconta.DataModel;
using Microsoft.Extensions.Options;

namespace SoftcodeUnicontaMiddleware.Services;

public class OrderService
{
    private readonly ILogger<OrderService> _logger;
    private readonly IOrderLogger _orderLog;
    private readonly OrderOptions _options;

    public OrderService(ILogger<OrderService> logger, IOrderLogger orderLog, IOptions<OrderOptions> options)
    {
        _logger   = logger;
        _orderLog = orderLog;
        _options  = options.Value;
    }

    public async Task<OrderResult> ProcessAsync(OrderRequest req, UnicontaServiceClient client)
    {
        try
        {
            _logger.LogInformation("Processing Uniconta order {OrderId} type={Type}", req.OrderId, req.CustomerType);

            var debtors = await client.GetAllDebtorsAsync();
            var account = FindDebtorAccount(debtors, req, out var matchRule);
            // The rule goes into the order log, so a wrong match can be traced
            // afterwards without guessing which field decided it.
            var debtorStatus = $"existing, matched on {matchRule}";

            if (account == null)
            {
                debtorStatus     = "new";
                account          = NextAccountNumber(debtors);   // highest existing Uniconta account + 1
                var debtor       = BuildDebtor(req, account, _options);
                var createResult = await client.CreateDebtorAsync(debtor);
                if (createResult != ErrorCodes.Succes)
                {
                    var msg = $"CreateDebtor returned {createResult}";
                    _logger.LogWarning(msg + " for order {OrderId}", req.OrderId);
                    var debtorFail = new OrderResult { Success = false, Message = msg };
                    _orderLog.LogFailed(req.OrderId, req.CustomerType, req.Email, msg,
                        DescribeDebtor(debtor, req), req, debtorFail);
                    return debtorFail;
                }
            }

            var order       = BuildOrderHeader(req, account, _options);
            var orderResult = await client.CreateOrderHeaderAsync(order);
            if (orderResult != ErrorCodes.Succes)
            {
                var msg = $"CreateOrderHeader returned {orderResult}";
                _logger.LogError(msg + " for order {OrderId}", req.OrderId);
                var headerFail = new OrderResult { Success = false, Message = msg };
                _orderLog.LogFailed(req.OrderId, req.CustomerType, req.Email, msg,
                    $"ORDER header create attempt:\n  Account={account}\n  OrderNumber={req.OrderId}\n" +
                    $"  Payment={req.PaymentCode}\n  SalesValue={req.TotalPrice}\n  DeliveryType={req.DeliveryType}",
                    req, headerFail);
                return headerFail;
            }

            var nothingShips    = req.Items.All(i => i.IsNonShippable);
            var createdLines    = new List<DebtorOrderLineClient>();

            foreach (var item in req.Items)
            {
                var line       = BuildOrderLine(req.OrderId, item, req.PricesIncludeTax);
                var lineResult = await client.CreateOrderLineAsync(line);
                if (lineResult != ErrorCodes.Succes)
                {
                    var warn = $"CreateOrderLine returned {lineResult}";
                    _logger.LogWarning(warn + " SKU={Sku} order={OrderId}", item.Sku, req.OrderId);
                    _orderLog.LogLineWarning(req.OrderId, item.Sku, warn);
                }
                else
                {
                    createdLines.Add(line);
                }

                foreach (var textLine in BuildTextLines(req.OrderId, item))
                {
                    var textResult = await client.CreateOrderLineAsync(textLine);
                    if (textResult != ErrorCodes.Succes)
                    {
                        // A missing text line only makes the invoice less informative,
                        // so it is logged and skipped rather than failing the order.
                        var warn = $"Text line returned {textResult}";
                        _logger.LogWarning(warn + " SKU={Sku} order={OrderId}", item.Sku, req.OrderId);
                        _orderLog.LogLineWarning(req.OrderId, item.Sku, warn);
                    }
                    else
                    {
                        createdLines.Add(textLine);
                    }
                }
            }

            if (!nothingShips && req.ShippingAmount > 0)
            {
                // Shipping is booked the way it is entered by hand in Uniconta:
                // one VAT-free amount in the "I alt" column, with no quantity and
                // no unit sales price. _AmountEntered is that column; a _Qty of 1
                // plus a _Price would instead print "1 ... 31,20" under Antal and
                // Salgspris, and would post the ex-VAT amount rather than what the
                // customer actually paid for delivery.
                var shippingLine = new DebtorOrderLineClient
                {
                    _OrderNumber   = req.OrderId,
                    _Item          = req.ShippingProductSku,
                    _AmountEntered = req.ShippingAmount,
                    _Storage       = StorageRegister.Move
                };
                var shResult = await client.CreateOrderLineAsync(shippingLine);
                if (shResult != ErrorCodes.Succes)
                {
                    var warn = $"Shipping line returned {shResult}";
                    _logger.LogWarning(warn + " for order {OrderId}", req.OrderId);
                    _orderLog.LogLineWarning(req.OrderId, req.ShippingProductSku, warn);
                }
                else
                {
                    createdLines.Add(shippingLine);
                }
            }

            // Nothing is invoiced automatically — for any customer type. Only the
            // sales order is created here. The invoice is posted later via
            // POST /orders/{n}/invoice, which fires when the order is invoiced in
            // the webshop, so Uniconta only ever shows what has actually
            // been invoiced.
            _logger.LogInformation(
                "Order {OrderId} ({CustomerType}) created as sales order — invoice deferred until invoiced in the webshop",
                req.OrderId, req.CustomerType);

            _logger.LogInformation("Uniconta order {OrderId} submitted successfully", req.OrderId);
            var ok = new OrderResult { Success = true, Message = $"Order submitted to debtor {account} ({debtorStatus})" };
            _orderLog.LogSubmitted(req.OrderId, req.CustomerType, req.Email, $"{account} ({debtorStatus})", req, ok);
            return ok;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Order processing failed for order {OrderId}", req.OrderId);
            var crash = new OrderResult { Success = false, Message = ex.Message };
            _orderLog.LogFailed(req.OrderId, req.CustomerType, req.Email, ex.Message, ex.ToString(), req, crash);
            return crash;
        }
    }

    // ---- Debtor matching -------------------------------------------------------

    /// <summary>
    /// Finds the debtor an order belongs to, trying each signal in order of how
    /// strongly it identifies a customer. <paramref name="rule"/> receives the
    /// rule that matched, so the order log shows WHY a debtor was reused.
    ///
    /// EAN and CVR come first: they identify an institution or company even when a
    /// new contact person orders or the address is typed slightly differently.
    ///
    /// One EAN ought to be one delivery point, but duplicates exist in real ledgers
    /// and a municipality may reuse a number across units. When several debtors
    /// carry the number they are scored on postcode, address, city and e-mail and the
    /// best one wins, with the lowest account number as tie-break so the same order
    /// always lands on the same debtor.
    ///
    /// Address alone is the last resort, compared without regard to case, spacing or
    /// punctuation.
    /// </summary>
    internal static string? FindDebtorAccount(Debtor[] debtors, OrderRequest req, out string rule)
    {
        // 1. EAN — a GLN is the strongest identifier a public institution has.
        if (req.CustomerType == "ean" && Digits(req.Ean).Length > 0)
        {
            var byEan = debtors.Where(d => Digits(d._EAN) == Digits(req.Ean)).ToList();
            if (byEan.Count > 0)
            {
                rule = byEan.Count == 1 ? "EAN" : $"EAN ({byEan.Count} debtors, best address)";
                return Best(byEan, req)._Account;
            }
        }

        // 2. CVR — same idea for private companies.
        if (req.CustomerType == "cvr" && Digits(req.Cvr).Length > 0)
        {
            var byCvr = debtors.Where(d => Digits(d._VatNumber) == Digits(req.Cvr)).ToList();
            if (byCvr.Count > 0)
            {
                rule = byCvr.Count == 1 ? "CVR" : $"CVR ({byCvr.Count} debtors, best address)";
                return Best(byCvr, req)._Account;
            }
        }

        // 3. E-mail, ignoring case — the same person ordering again.
        if (!string.IsNullOrWhiteSpace(req.Email))
        {
            var byEmail = debtors.Where(d => Loose(d._ContactEmail) == Loose(req.Email)).ToList();
            if (byEmail.Count > 0)
            {
                rule = byEmail.Count == 1 ? "e-mail" : $"e-mail ({byEmail.Count} debtors, best address)";
                return Best(byEmail, req)._Account;
            }
        }

        // 4. Address, normalised — the old behaviour, but no longer tripped up
        //    by letter case, double spaces, commas or "109 B" vs "109B".
        if (!string.IsNullOrWhiteSpace(req.DeliveryAddress))
        {
            var byAddress = debtors.Where(d => LooseAddress(d._Address1) == LooseAddress(req.DeliveryAddress)).ToList();
            if (byAddress.Count > 0)
            {
                rule = byAddress.Count == 1 ? "address" : $"address ({byAddress.Count} debtors, best match)";
                return Best(byAddress, req)._Account;
            }
        }

        rule = "none";
        return null;
    }

    /// Digits only, so "5790 000 000 001" and "5790000000001" are the same
    /// number — customers paste EAN numbers straight out of an e-mail.
    internal static string Digits(string? s) =>
        string.IsNullOrEmpty(s) ? "" : new string(s.Where(char.IsDigit).ToArray());

    /// Lower-cased, trimmed, with runs of whitespace collapsed. "Parkvej 109B"
    /// and " parkvej  109b " compare equal. Used for e-mail, postcode and town.
    internal static string Loose(string? s) =>
        string.IsNullOrWhiteSpace(s)
            ? ""
            : string.Join(' ', s.ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    /// <summary>
    /// Loose(), and on top of that the two liberties people take when they type
    /// a Danish street address into a web form:
    ///
    ///   "Parkvej 109 B"  vs  "Parkvej 109B"   — a space before the house letter
    ///   "Eksempelvej 2, st." vs "Eksempelvej 2 st" — commas and full stops
    ///
    /// A lone letter straight after a number is glued back onto the number, and
    /// . , ; - are dropped. Both sides go through the same mill, so the result
    /// only ever needs to be consistent, not pretty.
    ///
    /// Deliberately NOT fuzzy. Anything looser — edit distance, ignoring the
    /// house number — could fold two genuinely different customers into one, and
    /// that is worse than a duplicate: a duplicate is merged in a minute, a
    /// wrong merge sends someone else's invoice to the wrong school.
    /// </summary>
    internal static string LooseAddress(string? s)
    {
        var flad = Loose(s);
        if (flad.Length == 0)
            return "";

        var uden = new System.Text.StringBuilder(flad.Length);
        foreach (var c in flad)
            if (c != '.' && c != ',' && c != ';' && c != '-')
                uden.Append(c);

        var ord = uden.ToString().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var ud = new List<string>(ord.Length);

        foreach (var o in ord)
        {
            bool husbogstav = o.Length == 1
                              && char.IsLetter(o[0])
                              && ud.Count > 0
                              && char.IsDigit(ud[^1][^1]);

            if (husbogstav)
                ud[^1] += o;      // "109" + "b" -> "109b"
            else
                ud.Add(o);
        }

        return string.Join(' ', ud);
    }

    /// Picks the debtor that fits the order best. Postcode and street weigh
    /// most, because that is what separates two units of the same institution;
    /// e-mail counts as well, and the town only breaks near-ties. Equal scores
    /// fall back to the lowest account number, so the choice is stable and the
    /// oldest debtor keeps the trade rather than the newest duplicate.
    internal static Debtor Best(List<Debtor> candidates, OrderRequest req) =>
        candidates
            .OrderByDescending(d => Score(d, req))
            .ThenBy(d => long.TryParse(d._Account, out var n) ? n : long.MaxValue)
            .First();

    internal static int Score(Debtor d, OrderRequest req)
    {
        int points = 0;
        if (Loose(req.DeliveryPostcode).Length > 0 && Loose(d._ZipCode)      == Loose(req.DeliveryPostcode)) points += 3;
        if (LooseAddress(req.DeliveryAddress).Length > 0 && LooseAddress(d._Address1) == LooseAddress(req.DeliveryAddress)) points += 3;
        if (Loose(req.Email).Length            > 0 && Loose(d._ContactEmail) == Loose(req.Email))            points += 2;
        if (Loose(req.DeliveryCity).Length     > 0 && Loose(d._City)         == Loose(req.DeliveryCity))     points += 1;
        return points;
    }

    // ---- Debtor creation -------------------------------------------------------

    // Next debtor account = highest existing account number + 1, mirroring the store's
    // 6-digit numbering. Uniconta requires an explicit key (empty -> KeyIsEmpty) and does
    // NOT auto-number on insert. The EAN/CVR itself lives in _EAN/_VatNumber, NOT the key.
    // Guard: only real account numbers count (≤ 9 digits) so a stray EAN-shaped account
    // (13 digits) can never hijack the max and blow the numbering up.
    private static string NextAccountNumber(Debtor[] debtors)
    {
        long max = 0;
        foreach (var d in debtors)
            if ((d._Account?.Length ?? 0) <= 9 && long.TryParse(d._Account, out var n) && n > max)
                max = n;
        return (max + 1).ToString();
    }

    internal static DebtorClient BuildDebtor(OrderRequest req, string account, OrderOptions options)
    {
        var debtor = new DebtorClient
        {
            // Uniconta requires the debtor key (_Account); without it Insert fails with
            // KeyIsEmpty. This is the account the order header will also reference.
            _Account       = account,
            _Name          = !string.IsNullOrEmpty(req.CompanyName) ? req.CompanyName : req.ContactName,
            _Address1      = req.DeliveryAddress,
            _ZipCode       = req.DeliveryPostcode,
            _City          = req.DeliveryCity,
            _Country       = options.CountryCode,
            _ContactPerson = req.ContactName,
            _ContactEmail  = req.Email,
            _MobilPhone    = req.Phone ?? "",
            _Payment       = req.PaymentCode,
            _Vat           = options.DebtorVatCode,
            // The group must exist in the Uniconta company, otherwise Insert fails
            // with FieldHasInvalidValue. Configured per customer type in "Orders".
            _Group         = options.GroupFor(req.CustomerType)
        };

        // CVR customers: legal/VAT number on the debtor.
        // EAN customers: GLN + electronic (OIOUBL/XML) invoicing.
        if (req.CustomerType == "cvr" && !string.IsNullOrEmpty(req.Cvr))
        {
            debtor._VatNumber = req.Cvr;
        }
        else if (req.CustomerType == "ean" && !string.IsNullOrEmpty(req.Ean))
        {
            debtor._EAN = req.Ean;
            debtor._InvoiceInXML = true;
        }

        return debtor;
    }

    // Full field dump of an attempted debtor — written to the error log so a
    // FieldHasInvalidValue failure shows exactly which value Uniconta rejected.
    private static string DescribeDebtor(DebtorClient d, OrderRequest req) =>
        $"DEBTOR create attempt:\n" +
        $"  Name={d._Name}\n  Group={d._Group}\n  Vat={d._Vat}\n  Payment={d._Payment}\n" +
        $"  Country={d._Country}\n  VatNumber={d._VatNumber}\n  EAN={d._EAN}\n  InvoiceInXML={d._InvoiceInXML}\n" +
        $"  Address1={d._Address1}\n  ZipCode={d._ZipCode}\n  City={d._City}\n" +
        $"  ContactEmail={d._ContactEmail}\n  MobilPhone={d._MobilPhone}\n" +
        $"  (request) CustomerType={req.CustomerType} Cvr={req.Cvr} Ean={req.Ean} CompanyName={req.CompanyName}";

    // ---- Order header ----------------------------------------------------------

    internal static DebtorOrderClient BuildOrderHeader(OrderRequest req, string account, OrderOptions options)
    {
        var order = new DebtorOrderClient
        {
            _DCAccount    = account,
            _OrderNumber  = req.OrderId,
            _ContactName  = req.ContactName,
            _Payment      = req.PaymentCode,
            _Requisition  = string.Format(options.RequisitionFormat, req.OrderId),
            _YourRef      = req.ContactName,
            _SalesValue   = req.TotalPrice,
            _DeliveryCountry = options.CountryCode,

            // Uniconta does not remove an invoiced sales order by itself: PostInvoice
            // has no parameter for it, the order's own flag decides. Without it every
            // web order stays in the open-orders list next to its invoice.
            _DeleteOrder  = options.DeleteOrderWhenInvoiced
        };

        if (!string.IsNullOrWhiteSpace(req.OurRef))
            order._OurRef = req.OurRef.Trim();

        if (!string.IsNullOrEmpty(req.Comment))
            order._Remark = req.Comment;

        if (req.DeliveryType == 2)
        {
            // Parcel shop delivery
            order._DeliveryName    = req.DeliveryName;
            order._DeliveryAddress1 = $"c/o {req.ParcelShopName}";
            order._DeliveryAddress2 = req.ParcelShopAddress ?? "";
            order._DeliveryZipCode  = req.ParcelShopPostcode ?? "";
            order._DeliveryCity     = req.ParcelShopCity ?? "";
        }
        else if (!string.IsNullOrWhiteSpace(req.DeliveryCompany))
        {
            // Ship to Business: the parcel goes to a company, so the company is the
            // recipient and the person named in the checkout is the attention line.
            order._DeliveryName     = req.DeliveryCompany.Trim();
            order._DeliveryAddress1 = $"Att.: {req.DeliveryName}";
            order._DeliveryAddress2 = req.DeliveryAddress;
            order._DeliveryZipCode  = req.DeliveryPostcode;
            order._DeliveryCity     = req.DeliveryCity;
        }
        else
        {
            // Ship to Home: the recipient is the person, and the delivery name already
            // says so. An attention line here would only repeat it.
            order._DeliveryName     = req.DeliveryName;
            order._DeliveryAddress1 = req.DeliveryAddress;
            order._DeliveryAddress2 = "";
            order._DeliveryZipCode  = req.DeliveryPostcode;
            order._DeliveryCity     = req.DeliveryCity;
        }

        return order;
    }

    // ---- Order line ------------------------------------------------------------

    /// <summary>
    /// Uniconta wants the line price WITHOUT VAT and adds VAT itself from the item's
    /// own setup, so the webshop should send prices excluding VAT. A shop that sends
    /// prices including 25 % VAT sets PricesIncludeTax and the VAT is stripped here.
    /// </summary>
    internal static DebtorOrderLineClient BuildOrderLine(int orderId, OrderItemRequest item, bool pricesIncludeTax)
    {
        var price = pricesIncludeTax ? item.Price * 0.8 : item.Price;

        return new DebtorOrderLineClient
        {
            _OrderNumber = orderId,
            _Item        = item.Sku,
            _Price       = price,
            _Qty         = item.Qty,
            _Storage     = StorageRegister.Move
        };
    }

    /// <summary>
    /// Free-text lines printed under the product line, e.g. "Participant: Jane Doe".
    ///
    /// A Uniconta order line with no item number and only _Text set is a text line:
    /// it prints under the product it follows and carries no quantity, price or stock
    /// movement. The text is NOT put on the product line itself, because _Text there
    /// replaces the item name on the invoice.
    ///
    /// Blank and duplicate lines are dropped, so an empty field in the checkout never
    /// prints as a gap on the customer's invoice.
    /// </summary>
    internal static List<DebtorOrderLineClient> BuildTextLines(int orderId, OrderItemRequest item)
    {
        var lines = new List<DebtorOrderLineClient>();
        var seen  = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var raw in item.TextLines ?? new List<string>())
        {
            var text = (raw ?? "").Trim();
            if (text.Length == 0 || !seen.Add(text))
                continue;

            lines.Add(new DebtorOrderLineClient
            {
                _OrderNumber = orderId,
                _Text        = text,
                _Storage     = StorageRegister.None
            });
        }

        return lines;
    }

}
