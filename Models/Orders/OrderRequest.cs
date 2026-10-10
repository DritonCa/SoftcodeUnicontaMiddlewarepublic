namespace SoftcodeUnicontaMiddleware.Models.Orders;

public class OrderRequest
{
    public int OrderId { get; set; }

    /// <summary>"privat" | "cvr" | "ean"</summary>
    public string CustomerType { get; set; } = "privat";

    public string Email { get; set; } = "";
    public string? Ean { get; set; }
    public string? Cvr { get; set; }
    public string CompanyName { get; set; } = "";
    public string ContactName { get; set; } = "";
    public string? Phone { get; set; }
    public string? Comment { get; set; }

    /// <summary>
    /// Uniconta payment code (payment terms), e.g. "NK8" for 8 days net. Must exist
    /// in the Uniconta company.
    /// </summary>
    public string PaymentCode { get; set; } = "";

    /// <summary>1 = delivery address, 2 = parcel shop</summary>
    public int DeliveryType { get; set; } = 1;

    // Home address delivery
    public string DeliveryName { get; set; } = "";
    public string DeliveryAddress { get; set; } = "";
    public string DeliveryPostcode { get; set; } = "";
    public string DeliveryCity { get; set; } = "";

    /// <summary>
    /// Company the parcel goes to, set only when the customer chose "Ship to Business".
    /// When present it becomes the Uniconta delivery name and DeliveryName drops to
    /// the attention line.
    /// </summary>
    public string? DeliveryCompany { get; set; }

    // Parcel shop (only when DeliveryType == 2)
    public string? ParcelShopName { get; set; }
    public string? ParcelShopAddress { get; set; }
    public string? ParcelShopPostcode { get; set; }
    public string? ParcelShopCity { get; set; }

    public double TotalPrice { get; set; }

    /// <summary>
    /// What the customer paid for delivery. Posted as-is on the shipping item's line
    /// (in the amount column), so set up the shipping item's VAT in Uniconta to match.
    /// </summary>
    public double ShippingAmount { get; set; }

    /// <summary>Item number of the shipping item in Uniconta.</summary>
    public string ShippingProductSku { get; set; } = "";

    /// <summary>
    /// "Our reference" on the sales order, e.g. the clerk web orders are booked under.
    /// </summary>
    public string? OurRef { get; set; }

    /// <summary>
    /// False (default): prices are sent excluding VAT and passed through as-is, which is
    /// what Uniconta expects. True: prices include 25 % VAT, which is stripped here.
    /// </summary>
    public bool PricesIncludeTax { get; set; }

    public List<OrderItemRequest> Items { get; set; } = new();
}

public class OrderItemRequest
{
    public string Sku { get; set; } = "";
    public string Name { get; set; } = "";
    public double Price { get; set; }
    public int Qty { get; set; } = 1;

    /// <summary>
    /// True for items that are never shipped (services, courses, digital products).
    /// When every item on the order is non-shippable, no shipping line is added.
    /// </summary>
    public bool IsNonShippable { get; set; }

    /// <summary>
    /// Free-text lines printed under this product on the order and invoice, e.g.
    /// "Participant: Jane Doe" or a personalisation. Blank and duplicate lines are dropped.
    /// </summary>
    public List<string> TextLines { get; set; } = new();
}
