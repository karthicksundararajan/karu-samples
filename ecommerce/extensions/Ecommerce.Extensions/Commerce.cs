using System.Globalization;
using Karu.Extensions.Sdk;
using Karu.Generated.Catalogue;
using Karu.Generated.Customers;
using Karu.Generated.Sales;
using Karu.Generated.Tax;

namespace Ecommerce.Extensions;

internal static class Commerce
{
    public static Dictionary<string, object?> Values(params (string Key, object? Value)[] values) => values.ToDictionary(v => v.Key, v => v.Value);
    /// <summary>The one row of a by-id or by-key query; no row is a refusal.</summary>
    public static T One<T>(IReadOnlyList<T> rows) => rows.Count == 0 ? throw new HandlerDataException("The related record does not exist.") : rows.Single();
    public static string Id(long id) => id.ToString(CultureInfo.InvariantCulture);
    public static bool Staff(RowFilterContext context) => context.Roles.Overlaps(["CatalogueManager", "Warehouse", "Support", "Admin", "System"]);
    /// <summary>A business refusal from inside a helper; the invoker returns it as a handler error and the write rolls back.</summary>
    public static HandlerDataException Refuse(string message) => new(message);
    public static bool Is(EntityRecord input, string command) => input.Entity.EndsWith("." + command, StringComparison.Ordinal);
}

// ---------------------------------------------------------------------------------------------------------------------
// VAT
// ---------------------------------------------------------------------------------------------------------------------

/// <summary>One order line to price: the snapshot of a product and its VAT rate (percent). A line priced before the order
/// exists has no sku or name; the platform copies them from the product.</summary>
public sealed record PriceInput(long Product, string? Sku, string? Name, string TaxClass, decimal Rate, decimal UnitPrice, int Quantity, long? LineId = null);
/// <summary>A priced line: <c>Amount</c> is unit price × quantity (the list amount), then the amount excluding VAT, the
/// VAT and the total including VAT.</summary>
public sealed record PricedLine(PriceInput Line, decimal Amount, decimal Net, decimal Vat, decimal Total);
/// <summary>The VAT breakdown row of one (tax class, rate).</summary>
public sealed record VatRow(string TaxClass, decimal Rate, decimal Net, decimal Vat, decimal Gross);
/// <summary>A priced order: <c>Subtotal</c> sums the list amounts; <c>Net</c>, <c>VatTotal</c> and <c>Total</c> sum the lines.</summary>
public sealed record PricedOrder(bool PricesIncludeVat, IReadOnlyList<PricedLine> Lines, IReadOnlyList<VatRow> Vat, decimal Subtotal, decimal Net, decimal VatTotal, decimal Total);

/// <summary>
/// Order pricing. Each line is rounded to 0.01 (away from zero):
/// VAT included: gross = price × qty, vat = gross × r / (100 + r), net = gross − vat;
/// VAT on top: net = price × qty, vat = net × r / 100, gross = net + vat.
/// The breakdown sums the lines per (tax class, rate) and the totals sum the lines; <c>Total</c> (gross) is charged.
/// </summary>
public static class Pricing
{
    public const decimal MaxTotal = 9999999999.99m;
    static readonly string[] Classes = ["Standard", "Reduced", "Special"];

    static decimal Round(decimal value) => decimal.Round(value, 2, MidpointRounding.AwayFromZero);

    public static PricedLine Line(PriceInput line, bool pricesIncludeVat)
    {
        var amount = Round(line.UnitPrice * line.Quantity);
        if (pricesIncludeVat)
        {
            var vat = Round(amount * line.Rate / (100m + line.Rate));
            return new(line, amount, amount - vat, vat, amount);
        }
        var added = Round(amount * line.Rate / 100m);
        return new(line, amount, amount, added, amount + added);
    }

    public static PricedOrder Price(IEnumerable<PriceInput> lines, bool pricesIncludeVat)
    {
        var priced = lines.Select(l => Line(l, pricesIncludeVat)).ToList();
        var vat = priced.GroupBy(l => (l.Line.TaxClass, l.Line.Rate))
            .OrderBy(g => Array.IndexOf(Classes, g.Key.TaxClass)).ThenByDescending(g => g.Key.Rate)
            .Select(g => new VatRow(g.Key.TaxClass, g.Key.Rate, g.Sum(l => l.Net), g.Sum(l => l.Vat), g.Sum(l => l.Total)))
            .ToList();
        return new(pricesIncludeVat, priced, vat, priced.Sum(l => l.Amount), priced.Sum(l => l.Net), priced.Sum(l => l.Vat), priced.Sum(l => l.Total));
    }
}

/// <summary>The VAT rules of a destination country, read through handler data.</summary>
internal sealed record TaxRules(string Country, bool PricesIncludeVat, IReadOnlyDictionary<string, decimal> Rates)
{
    public static TaxRules For(HandlerContext context, string country)
    {
        var found = context.Data.CountryByCode(country);
        if (found.Count == 0) throw Commerce.Refuse($"We cannot sell to {country}.");
        var rates = context.Data.VatRatesFor(found[0].Id!.Value)
            .GroupBy(r => r.TaxClass.ToString()).ToDictionary(g => g.Key, g => g.First().Rate);
        return new(country, found[0].PricesIncludeVAT, rates);
    }

    public decimal Rate(string taxClass) => Rates.TryGetValue(taxClass, out var rate) ? rate : throw Commerce.Refuse($"We cannot sell to {Country}.");
}

/// <summary>Shared order steps: catalogue snapshots, stock reservations and the priced child collections.</summary>
internal static class Orders
{
    /// <summary>A line for an order that does not exist yet: the product's current price and its category's tax class.
    /// The platform copies the sku and name when it creates the line, and refuses a product that is not active.</summary>
    public static PriceInput FromCatalogue(HandlerContext context, TaxRules tax, long productId, int quantity)
    {
        var product = Commerce.One(context.Data.ProductById(checked((int)productId)));
        var taxClass = Commerce.One(context.Data.CategoryById(checked((int)product.Category.Id))).TaxClass.ToString();
        return new(productId, null, null, taxClass, tax.Rate(taxClass), product.Price, quantity);
    }

    /// <summary>A line of an order, keeping its snapshot (price, names, tax class) and re-reading the country's current rate.
    /// A new line (id null) has its snapshot too: the platform copied it when the line was added.</summary>
    public static PriceInput FromLine(EntityRecord line, TaxRules tax, bool keepId = true)
    {
        var taxClass = line.Get<string>("taxClass");
        return new(line.Ref("product")!.Id, line.Get<string>("sku"), line.Get<string>("name"), taxClass, tax.Rate(taxClass),
            line.Get<decimal>("unitPrice"), line.Get<int>("quantity"), keepId ? line.Id : null);
    }

    public static PricedOrder Price(IEnumerable<PriceInput> lines, TaxRules tax)
    {
        var priced = Pricing.Price(lines, tax.PricesIncludeVat);
        if (priced.Total > Pricing.MaxTotal) throw Commerce.Refuse("The order total exceeds the supported amount.");
        return priced;
    }

    public static IReadOnlyDictionary<string, object?> Item(PricedLine line)
    {
        var item = Commerce.Values(("product", line.Line.Product), ("sku", line.Line.Sku), ("name", line.Line.Name),
            ("quantity", line.Line.Quantity), ("unitPrice", line.Line.UnitPrice), ("taxClass", line.Line.TaxClass), ("vatRate", line.Line.Rate),
            ("totalExclVAT", line.Net), ("totalVAT", line.Vat), ("total", line.Total));
        if (line.Line.LineId is { } id) item["id"] = id;
        return item;
    }

    public static IReadOnlyDictionary<string, object?> Item(VatRow row) =>
        Commerce.Values(("taxClass", row.TaxClass), ("rate", row.Rate), ("totalExclVAT", row.Net), ("totalVAT", row.Vat), ("total", row.Gross));

    /// <summary>Creates a pending order, payable for 30 minutes, with its totals, lines and VAT breakdown.</summary>
    public static long Create(HandlerContext context, PricedOrder priced, string owner, long customer, AddressValue shipping, OrderCurrency currency,
        string? cartId = null, long? replaces = null) => context.Data.CreateOrder(new()
    {
        Owner = owner, CartId = cartId, Customer = customer, Shipping = shipping, Currency = currency, Replaces = replaces,
        PlacedAt = context.UtcNow, ExpiresAt = context.UtcNow.AddMinutes(30),
        PricesIncludeVAT = priced.PricesIncludeVat, Subtotal = priced.Subtotal, TotalExclVAT = priced.Net, TotalVAT = priced.VatTotal, Total = priced.Total,
        Lines = priced.Lines.Select(l => new OrderLineValues
        {
            Product = l.Line.Product, Sku = l.Line.Sku, Name = l.Line.Name, Quantity = l.Line.Quantity, UnitPrice = l.Line.UnitPrice,
            TaxClass = Enum.Parse<CategoryTaxClass>(l.Line.TaxClass), VatRate = l.Line.Rate, TotalExclVAT = l.Net, TotalVAT = l.Vat, Total = l.Total,
        }).ToList(),
        VatBreakdown = priced.Vat.Select(v => new OrderVatValues
        {
            TaxClass = Enum.Parse<OrderVatTaxClass>(v.TaxClass), Rate = v.Rate, TotalExclVAT = v.Net, TotalVAT = v.Vat, Total = v.Gross,
        }).ToList(),
    });

    /// <summary>The changes of an existing order: totals, lines (by id) and a rebuilt VAT breakdown.</summary>
    public static HandlerResult Changes(PricedOrder priced) => HandlerResult.Ok
        .Set("pricesIncludeVAT", priced.PricesIncludeVat).Set("subtotal", priced.Subtotal).Set("totalExclVAT", priced.Net)
        .Set("totalVAT", priced.VatTotal).Set("total", priced.Total)
        .SetChildren("lines", priced.Lines.Select(Item)).SetChildren("vatBreakdown", priced.Vat.Select(Item));

    /// <summary>Reserves every line's quantity for the order: a held Reservation per product.</summary>
    public static void Reserve(HandlerContext context, long orderId, IEnumerable<PriceInput> lines)
    {
        foreach (var product in lines.GroupBy(l => l.Product))
        {
            var quantity = product.Sum(l => l.Quantity);
            var stock = Commerce.One(context.Data.StockByProduct(product.Key));
            if ((long)stock.OnHand - stock.Reserved < quantity) throw Commerce.Refuse("Insufficient stock.");
            context.Data.ReserveStock(stock.Id!.Value, new() { Quantity = quantity });
            context.Data.CreateReservation(new() { Order = orderId, Stock = stock.Id.Value, Quantity = quantity });
        }
    }

    /// <summary>Releases every held reservation of the order.</summary>
    public static void Release(HandlerContext context, long orderId)
    {
        foreach (var reservation in context.Data.HeldReservations(orderId))
        {
            context.Data.ReleaseStock(reservation.Stock.Id, new() { Quantity = reservation.Quantity });
            context.Data.ReleaseReservation(reservation.Id!.Value);
        }
    }

    public static string Country(EntityRecord order) => Order.Wrap(order).Shipping.Country;
}

// ---------------------------------------------------------------------------------------------------------------------
// Catalogue
// ---------------------------------------------------------------------------------------------------------------------

[KaruContract("Catalogue.VisibleProducts")]
public sealed class VisibleProducts : RowFilter
{
    public override Condition? Filter(RowFilterContext context) => Commerce.Staff(context) ? null : Condition.Eq("status", "Active");
}

[KaruContract("Catalogue.InitializeStock")]
public sealed class InitializeStock : EventSubscriber
{
    public override SubscriberResult Handle(EventRecord e) => SubscriberResult.Ok.Create("Stock", Commerce.Values(
        ("product", e.SourceId), ("stockKey", Commerce.Id(e.SourceId)), ("onHand", 0), ("reserved", 0)));
}
[KaruContract("Catalogue.ValidStock")]
public sealed class ValidStock : Rule
{
    public override RuleResult Check(EntityRecord record) => record.Get<int>("reserved") <= record.Get<int>("onHand")
        ? RuleResult.Ok : RuleResult.Fail("reserved", "Reserved stock cannot exceed stock on hand.");
}

public abstract class BalanceHandler : CommandHandler
{
    protected abstract StockMovementKind Kind { get; }
    public override HandlerResult Handle(EntityRecord record, EntityRecord input, HandlerContext context)
    {
        var quantity = input.Get<int>("quantity");
        long onHand = record.Get<int>("onHand"), reserved = record.Get<int>("reserved");
        switch (Kind)
        {
            case StockMovementKind.Adjustment: onHand += quantity; break;
            case StockMovementKind.Reservation: reserved += quantity; break;
            case StockMovementKind.Release: reserved -= quantity; break;
            case StockMovementKind.Sale: reserved -= quantity; onHand -= quantity; break;
            case StockMovementKind.Return: onHand += quantity; break;
        }
        if (onHand < 0 || reserved < 0 || reserved > onHand || onHand > int.MaxValue)
            return HandlerResult.Fail("onHand", "Insufficient stock, or adjustment would invalidate reservations.");
        context.Data.CreateStockMovement(new() { Stock = record.Id!.Value, Kind = Kind, Quantity = quantity, At = context.UtcNow });
        return HandlerResult.Ok.Set("onHand", (int)onHand).Set("reserved", (int)reserved);
    }
}
[KaruContract("Catalogue.AdjustBalance")]
public sealed class AdjustBalance : BalanceHandler { protected override StockMovementKind Kind => StockMovementKind.Adjustment; }
[KaruContract("Catalogue.ReserveBalance")]
public sealed class ReserveBalance : BalanceHandler { protected override StockMovementKind Kind => StockMovementKind.Reservation; }
[KaruContract("Catalogue.ReleaseBalance")]
public sealed class ReleaseBalance : BalanceHandler { protected override StockMovementKind Kind => StockMovementKind.Release; }
[KaruContract("Catalogue.ConsumeBalance")]
public sealed class ConsumeBalance : BalanceHandler { protected override StockMovementKind Kind => StockMovementKind.Sale; }
[KaruContract("Catalogue.ReturnBalance")]
public sealed class ReturnBalance : BalanceHandler { protected override StockMovementKind Kind => StockMovementKind.Return; }

// ---------------------------------------------------------------------------------------------------------------------
// Carts
// ---------------------------------------------------------------------------------------------------------------------

[KaruContract("Sales.OpenCart")]
public sealed class OpenCart : CommandHandler
{
    public override HandlerResult Handle(EntityRecord record, EntityRecord input, HandlerContext context) =>
        record.IsNull("order") ? HandlerResult.Ok : HandlerResult.Fail("order", "Omit order; it is set at checkout.");
}

/// <summary>The cart grid update, AddItem and ChangeItem, after the platform has applied the edit: only the lines may
/// change. The platform refuses a new line for a product that is not active (refWhen).</summary>
[KaruContract("Sales.ChangeCart")]
public sealed class ChangeCart : CommandHandler
{
    public override HandlerResult Handle(EntityRecord record, EntityRecord input, HandlerContext context)
    {
        var stored = Commerce.One(context.Data.CartById(checked((int)record.Id!.Value))).Record;
        if (Commerce.Is(input, "UpdateCart") && (record.Ref("customer")?.Id != stored.Ref("customer")?.Id || record.Ref("order")?.Id != stored.Ref("order")?.Id))
            return HandlerResult.Fail(null, "Only the lines of a cart can be changed.");
        return HandlerResult.Ok;
    }
}

// ---------------------------------------------------------------------------------------------------------------------
// Orders
// ---------------------------------------------------------------------------------------------------------------------

[KaruContract("Sales.PlaceOrder")]
public sealed class PlaceOrder : CommandHandler
{
    public override HandlerResult Handle(EntityRecord record, EntityRecord input, HandlerContext context)
    {
        var customer = Commerce.One(context.Data.CustomerById(checked((int)record.Ref("customer")!.Id)));
        var lines = record.Children("lines");
        if (lines.Count == 0) return HandlerResult.Fail(null, "The cart is empty.");
        var tax = TaxRules.For(context, customer.Shipping.Country);
        var priced = Orders.Price(lines.Select(l => Orders.FromCatalogue(context, tax, l.Ref("product")!.Id, l.Get<int>("quantity"))), tax);
        var orderId = Orders.Create(context, priced, record.Get<string>("owner"), customer.Id!.Value, customer.Shipping, OrderCurrency.CHF,
            cartId: Commerce.Id(record.Id!.Value));
        Orders.Reserve(context, orderId, priced.Lines.Select(l => l.Line));
        context.Data.SealCart(record.Id!.Value);
        return HandlerResult.Ok.Set("order", orderId);
    }
}

/// <summary>AddOrderItem, ChangeOrderItem and RemoveOrderItem on a pending order, after the platform has applied the
/// edit: every line is priced from its snapshot, which the platform copied from the catalogue for a new line. All held
/// stock is released and the new quantities reserved again, then the lines, VAT breakdown and totals are rebuilt.</summary>
[KaruContract("Sales.EditOrder")]
public sealed class EditOrder : CommandHandler
{
    public override HandlerResult Handle(EntityRecord record, EntityRecord input, HandlerContext context)
    {
        if (context.UtcNow >= record.Get<DateTime>("expiresAt")) return HandlerResult.Fail(null, "This order has expired.");
        var stored = record.Children("lines");
        if (stored.Count == 0) return HandlerResult.Fail(null, "An order needs at least one line; cancel the order instead.");
        var tax = TaxRules.For(context, Orders.Country(record));
        var lines = stored.Select(l => Orders.FromLine(l, tax)).ToList();
        var priced = Orders.Price(lines, tax);
        Orders.Release(context, record.Id!.Value);
        Orders.Reserve(context, record.Id!.Value, lines);
        return Orders.Changes(priced);
    }
}

/// <summary>ReplaceOrder: a paid order gets a pending replacement with the same line snapshots and its own reservations.
/// The customer edits the replacement (dropping lines cancels them) and pays it; paying it supersedes and refunds this
/// order. ReplaceOrder is idempotent, so a repeated request key never reaches this handler.</summary>
[KaruContract("Sales.StartReplacement")]
public sealed class StartReplacement : CommandHandler
{
    public override HandlerResult Handle(EntityRecord record, EntityRecord input, HandlerContext context)
    {
        var order = Order.Wrap(record);
        var tax = TaxRules.For(context, order.Shipping.Country);
        var priced = Orders.Price(record.Children("lines").Select(l => Orders.FromLine(l, tax, keepId: false)), tax);
        var replacement = Orders.Create(context, priced, order.Owner, order.Customer.Id, order.Shipping, order.Currency, replaces: order.Id);
        Orders.Reserve(context, replacement, priced.Lines.Select(l => l.Line));
        return HandlerResult.Ok.Set("replacement", replacement);
    }
}

[KaruContract("Sales.TakePayment")]
public sealed class TakePayment : CommandHandler
{
    public override HandlerResult Handle(EntityRecord record, EntityRecord input, HandlerContext context)
    {
        // The provider sees one key per order and attempt; a retried PayOrder never reaches this handler.
        var key = $"{record.Id}:{input.Get<string>("attemptKey")}";
        if (context.UtcNow >= record.Get<DateTime>("expiresAt")) return HandlerResult.Fail(null, "This order has expired.");
        string outcome; string? reference = null;
        var amount = record.Get<decimal>("total");
        try
        {
            var answer = context.Port("Payments").Call("Charge", Commerce.Values(("key", key), ("amount", amount), ("method", input.Get<string>("method"))));
            outcome = answer.Get<string>("outcome"); reference = answer.Get<string?>("reference");
        }
        catch (PortException) { outcome = "TimedOut"; }
        if (outcome is not ("Succeeded" or "Declined" or "TimedOut")) return HandlerResult.Fail(null, "The payment adapter returned an unsupported outcome.");
        context.Data.CreatePaymentAttempt(new() { Order = record.Id!.Value, AttemptKey = key, Kind = PaymentAttemptKind.Charge, Amount = amount,
            Outcome = Enum.Parse<PaymentAttemptOutcome>(outcome), Reference = reference, At = context.UtcNow });
        if (outcome == "Succeeded") context.Data.ConfirmOrder(record.Id!.Value);
        return HandlerResult.Ok;
    }
}

public abstract class ReservationHandler : CommandHandler
{
    protected virtual bool Consume => false;
    protected virtual bool Expire => false;
    public override HandlerResult Handle(EntityRecord record, EntityRecord input, HandlerContext context)
    {
        if (Expire && context.UtcNow < record.Get<DateTime>("expiresAt")) return HandlerResult.Fail(null, "The payment window is still open.");
        if (!Consume) Orders.Release(context, record.Id!.Value);
        else foreach (var reservation in context.Data.HeldReservations(record.Id!.Value))
        {
            context.Data.ConsumeStock(reservation.Stock.Id, new() { Quantity = reservation.Quantity });
            context.Data.ConsumeReservation(reservation.Id!.Value);
        }
        if (record.Ref("replaces") is { } original)
        {
            // A paid replacement supersedes its original; a cancelled or expired one frees the original for another try.
            if (Consume) context.Data.SupersedeOrder(original.Id);
            else context.Data.UnlinkReplacement(original.Id, new() { Replacement = record.Id!.Value });
        }
        return HandlerResult.Ok;
    }
}
[KaruContract("Sales.ConsumeReservations")]
public sealed class ConsumeReservations : ReservationHandler { protected override bool Consume => true; }
[KaruContract("Sales.ReleaseReservations")]
public sealed class ReleaseReservations : ReservationHandler;
[KaruContract("Sales.ExpireReservations")]
public sealed class ExpireReservations : ReservationHandler { protected override bool Expire => true; }

[KaruContract("Sales.ClearReplacement")]
public sealed class ClearReplacement : CommandHandler
{
    public override HandlerResult Handle(EntityRecord record, EntityRecord input, HandlerContext context) =>
        record.Ref("replacement")?.Id == input.Ref("replacement")!.Id ? HandlerResult.Ok.Set("replacement", null) : HandlerResult.Ok;
}

/// <summary>Returns a paid order's goods to stock and refunds its charge (once per order, recorded as a Refund
/// PaymentAttempt). A failing refund fails the command, so nothing is returned either.</summary>
public abstract class RefundHandler : CommandHandler
{
    public override HandlerResult Handle(EntityRecord record, EntityRecord input, HandlerContext context)
    {
        foreach (var reservation in context.Data.ConsumedReservations(record.Id!.Value))
            context.Data.ReturnStock(reservation.Stock.Id, new() { Quantity = reservation.Quantity });
        var key = $"refund:{record.Id}";
        if (context.Data.PaymentByKey(key).Count != 0) return HandlerResult.Ok;
        var amount = record.Get<decimal>("total");
        string? reference;
        try
        {
            var answer = context.Port("Payments").Call("Refund", Commerce.Values(("key", key), ("amount", amount)));
            if (answer.Get<string?>("outcome") != "Succeeded") return HandlerResult.Fail(null, "Refund unavailable, try again.");
            reference = answer.Get<string?>("reference");
        }
        catch (PortException) { return HandlerResult.Fail(null, "Refund unavailable, try again."); }
        context.Data.CreatePaymentAttempt(new() { Order = record.Id!.Value, AttemptKey = key, Kind = PaymentAttemptKind.Refund, Amount = amount,
            Outcome = PaymentAttemptOutcome.Succeeded, Reference = reference, At = context.UtcNow });
        return HandlerResult.Ok;
    }
}
[KaruContract("Sales.SettleReplacedOrder")]
public sealed class SettleReplacedOrder : RefundHandler;
[KaruContract("Sales.RefundOrder")]
public sealed class RefundOrder : RefundHandler;

// ---------------------------------------------------------------------------------------------------------------------
// Payments and jobs
// ---------------------------------------------------------------------------------------------------------------------

public sealed record ChargeInput(string? Key, decimal? Amount, string? Method);
public sealed record RefundInput(string? Key, decimal? Amount);
public sealed record PaymentOutput(string? Outcome, string? Reference);
public sealed class SimulatedPayments : PortAdapter
{
    static string Reference(string prefix, string key) =>
        prefix + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key)))[..20];

    public async Task<PaymentOutput> Charge(ChargeInput input, PortCallContext context)
    {
        if (input.Method == "Timeout") await Task.Delay(Timeout.InfiniteTimeSpan, context.CancellationToken);
        return input.Method == "Success" ? new("Succeeded", Reference("sim_", input.Key!)) : new("Declined", null);
    }

    /// <summary>Refunds always succeed in the simulation; the reference is deterministic per key.</summary>
    public Task<PaymentOutput> Refund(RefundInput input, PortCallContext context) => Task.FromResult(new PaymentOutput("Succeeded", Reference("simr_", input.Key!)));
}

public sealed class ExpirePendingOrders : ScheduledJob
{
    public override Task Run(JobContext context)
    {
        // Snapshot candidates before changing the query's membership, so pagination never skips an order.
        var expired = new List<long>();
        for (var page = 1; ; page++)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            var rows = context.Query("Sales.PendingOrders", page, 100).Rows;
            expired.AddRange(rows.Where(row => row.Get<DateTime>("expiresAt") <= context.UtcNow).Select(row => row.Id));
            if (rows.Count < 100) break;
        }
        foreach (var id in expired)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            var result = context.Run("Sales.ExpireOrder", id);
            if (!result.Ok && result.Status != "notAvailable" && result.Status != "notFound")
                throw new InvalidOperationException($"Could not expire order {id}: {result.Status}");
        }
        return Task.CompletedTask;
    }
}
