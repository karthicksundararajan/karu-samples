using System.Globalization;
using Karu.Extensions.Sdk;

namespace Ecommerce.Extensions;

internal static class Commerce
{
    public const int MaxLineQuantity = 1000000;
    public static Dictionary<string, object?> Values(params (string Key, object? Value)[] values) => values.ToDictionary(v => v.Key, v => v.Value);
    public static IReadOnlyList<EntityRecord> All(HandlerContext context, string query, params (string Key, object? Value)[] parameters)
    {
        var rows = new List<EntityRecord>();
        for (var page = 1; ; page++)
        {
            var result = context.Data.Query(query, Values(parameters), page);
            rows.AddRange(result.Rows);
            if (rows.Count >= result.Total || result.Rows.Count == 0) return rows;
        }
    }
    public static EntityRecord One(HandlerContext context, string query, params (string Key, object? Value)[] parameters) =>
        All(context, query, parameters).SingleOrDefault() ?? throw new HandlerDataException("The related record does not exist.");
    public static string Id(long id) => id.ToString(CultureInfo.InvariantCulture);
    public static bool Staff(RowFilterContext context) => context.Roles.Overlaps(["CatalogueManager", "Warehouse", "Support", "Admin", "System"]);
    /// <summary>A business refusal from inside a helper; the invoker returns it as a handler error and the write rolls back.</summary>
    public static HandlerDataException Refuse(string message) => new(message);
    public static bool Is(EntityRecord input, string command) => input.Entity.EndsWith("." + command, StringComparison.Ordinal);
}

// ---------------------------------------------------------------------------------------------------------------------
// VAT
// ---------------------------------------------------------------------------------------------------------------------

/// <summary>One order line to price: the snapshot of a product and its VAT rate (percent).</summary>
public sealed record PriceInput(long Product, string Sku, string Name, string TaxClass, decimal Rate, decimal UnitPrice, int Quantity, long? LineId = null);
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
        var found = Commerce.All(context, "Tax.CountryByCode", ("code", country));
        if (found.Count == 0) throw Commerce.Refuse($"We cannot sell to {country}.");
        var rates = Commerce.All(context, "Tax.VatRatesFor", ("countryId", found[0].Id))
            .GroupBy(r => r.Get<string>("taxClass")).ToDictionary(g => g.Key, g => g.First().Get<decimal>("rate"));
        return new(country, found[0].Get<bool>("pricesIncludeVAT"), rates);
    }

    public decimal Rate(string taxClass) => Rates.TryGetValue(taxClass, out var rate) ? rate : throw Commerce.Refuse($"We cannot sell to {Country}.");
}

/// <summary>Shared order steps: catalogue snapshots, stock reservations and the priced child collections.</summary>
internal static class Orders
{
    /// <summary>A new line from the current catalogue: an active product, its price and its category's tax class.</summary>
    public static PriceInput FromCatalogue(HandlerContext context, TaxRules tax, long productId, int quantity, long? lineId = null)
    {
        var product = Commerce.One(context, "Catalogue.ProductById", ("productId", productId));
        if (product.Get<string>("status") != "Active") throw Commerce.Refuse("A product is no longer available.");
        var category = Commerce.One(context, "Catalogue.CategoryById", ("categoryId", product.Ref("category")!.Id));
        var taxClass = category.Get<string>("taxClass");
        return new(productId, product.Get<string>("sku"), product.Get<string>("name"), taxClass, tax.Rate(taxClass), product.Get<decimal>("price"), quantity, lineId);
    }

    /// <summary>A stored line, keeping its snapshot (price, names, tax class) and re-reading the country's current rate.</summary>
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

    /// <summary>The order values of a new order: totals, lines and VAT breakdown.</summary>
    public static Dictionary<string, object?> Values(PricedOrder priced, params (string Key, object? Value)[] values)
    {
        var all = Commerce.Values(values);
        all["pricesIncludeVAT"] = priced.PricesIncludeVat;
        all["subtotal"] = priced.Subtotal;
        all["totalExclVAT"] = priced.Net;
        all["totalVAT"] = priced.VatTotal;
        all["total"] = priced.Total;
        all["lines"] = priced.Lines.Select(Item).ToList();
        all["vatBreakdown"] = priced.Vat.Select(Item).ToList();
        return all;
    }

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
            var stock = Commerce.One(context, "Catalogue.StockByProduct", ("productId", product.Key));
            if ((long)stock.Get<int>("onHand") - stock.Get<int>("reserved") < quantity) throw Commerce.Refuse("Insufficient stock.");
            context.Data.Run("Catalogue.ReserveStock", stock.Id!.Value, Commerce.Values(("quantity", quantity)));
            context.Data.Create("Reservation", Commerce.Values(("order", orderId), ("stock", stock.Id), ("quantity", quantity)));
        }
    }

    /// <summary>Releases every held reservation of the order.</summary>
    public static void Release(HandlerContext context, long orderId)
    {
        foreach (var reservation in Commerce.All(context, "HeldReservations", ("orderId", orderId)))
        {
            context.Data.Run("Catalogue.ReleaseStock", reservation.Ref("stock")!.Id, Commerce.Values(("quantity", reservation.Get<int>("quantity"))));
            context.Data.Run("ReleaseReservation", reservation.Id!.Value);
        }
    }

    public static string Country(EntityRecord withShipping) => withShipping.GetValueObject("shipping")!.Get<string>("country");
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
    protected abstract string Kind { get; }
    public override HandlerResult Handle(EntityRecord record, EntityRecord input, HandlerContext context)
    {
        var quantity = input.Get<int>("quantity");
        long onHand = record.Get<int>("onHand"), reserved = record.Get<int>("reserved");
        switch (Kind)
        {
            case "Adjustment": onHand += quantity; break;
            case "Reservation": reserved += quantity; break;
            case "Release": reserved -= quantity; break;
            case "Sale": reserved -= quantity; onHand -= quantity; break;
            case "Return": onHand += quantity; break;
        }
        if (onHand < 0 || reserved < 0 || reserved > onHand || onHand > int.MaxValue)
            return HandlerResult.Fail("onHand", "Insufficient stock, or adjustment would invalidate reservations.");
        context.Data.Create("StockMovement", Commerce.Values(("stock", record.Id), ("kind", Kind), ("quantity", quantity), ("at", context.UtcNow)));
        return HandlerResult.Ok.Set("onHand", (int)onHand).Set("reserved", (int)reserved);
    }
}
[KaruContract("Catalogue.AdjustBalance")]
public sealed class AdjustBalance : BalanceHandler { protected override string Kind => "Adjustment"; }
[KaruContract("Catalogue.ReserveBalance")]
public sealed class ReserveBalance : BalanceHandler { protected override string Kind => "Reservation"; }
[KaruContract("Catalogue.ReleaseBalance")]
public sealed class ReleaseBalance : BalanceHandler { protected override string Kind => "Release"; }
[KaruContract("Catalogue.ConsumeBalance")]
public sealed class ConsumeBalance : BalanceHandler { protected override string Kind => "Sale"; }
[KaruContract("Catalogue.ReturnBalance")]
public sealed class ReturnBalance : BalanceHandler { protected override string Kind => "Return"; }

// ---------------------------------------------------------------------------------------------------------------------
// Carts
// ---------------------------------------------------------------------------------------------------------------------

internal sealed record CartItem(long? Id, long Product, long Quantity);

internal static class Carts
{
    /// <summary>The cart's new lines: duplicates merged (keeping the first line id), at most 1,000,000 per line, and every
    /// product whose quantity grows compared with <paramref name="before"/> must be active.</summary>
    public static HandlerResult Lines(HandlerContext context, IEnumerable<CartItem> lines, IEnumerable<EntityRecord> before)
    {
        var merged = lines.GroupBy(l => l.Product)
            .Select(g => new CartItem(g.Select(l => l.Id).FirstOrDefault(id => id is not null), g.Key, g.Sum(l => l.Quantity))).ToList();
        if (merged.Any(l => l.Quantity > Commerce.MaxLineQuantity)) return HandlerResult.Fail(null, "A cart line may contain at most 1,000,000 items.");
        var was = before.GroupBy(l => l.Ref("product")!.Id).ToDictionary(g => g.Key, g => (long)g.Sum(l => l.Get<int>("quantity")));
        foreach (var line in merged.Where(l => l.Quantity > was.GetValueOrDefault(l.Product)))
            if (Commerce.One(context, "Catalogue.ProductById", ("productId", line.Product)).Get<string>("status") != "Active")
                return HandlerResult.Fail(null, "The product is unavailable.");
        return HandlerResult.Ok.SetChildren("lines", merged.Select(l =>
        {
            var item = Commerce.Values(("product", l.Product), ("quantity", (int)l.Quantity));
            if (l.Id is { } id) item["id"] = id;
            return (IReadOnlyDictionary<string, object?>)item;
        }));
    }

    public static List<CartItem> Items(EntityRecord record) =>
        record.Children("lines").Select(l => new CartItem(l.Id, l.Ref("product")!.Id, l.Get<int>("quantity"))).ToList();
}

[KaruContract("Sales.OpenCart")]
public sealed class OpenCart : CommandHandler
{
    public override HandlerResult Handle(EntityRecord record, EntityRecord input, HandlerContext context) =>
        record.IsNull("order") ? Carts.Lines(context, Carts.Items(record), []) : HandlerResult.Fail("order", "Omit order; it is set at checkout.");
}

/// <summary>Every cart change (grid update, AddItem, ChangeItem, RemoveItem) rewrites the lines through <see cref="Carts.Lines"/>.</summary>
[KaruContract("Sales.ChangeCart")]
public sealed class ChangeCart : CommandHandler
{
    public override HandlerResult Handle(EntityRecord record, EntityRecord input, HandlerContext context)
    {
        var stored = Commerce.One(context, "CartById", ("cartId", record.Id));
        var lines = Carts.Items(record);
        if (Commerce.Is(input, "UpdateCart"))
        {
            if (record.Ref("customer")?.Id != stored.Ref("customer")?.Id || record.Ref("order")?.Id != stored.Ref("order")?.Id)
                return HandlerResult.Fail(null, "Only the lines of a cart can be changed.");
            return Carts.Lines(context, lines, stored.Children("lines"));
        }
        var product = input.Ref("product")!.Id;
        if (Commerce.Is(input, "AddItem")) lines.Add(new CartItem(null, product, input.Get<int>("quantity")));
        else
        {
            var index = lines.FindIndex(l => l.Product == product);
            if (index < 0) return HandlerResult.Fail(null, "The product is not in the cart.");
            if (Commerce.Is(input, "RemoveItem")) lines.RemoveAll(l => l.Product == product);
            else lines[index] = lines[index] with { Product = input.Ref("newProduct")?.Id ?? product, Quantity = input.Get<int>("quantity") };
        }
        return Carts.Lines(context, lines, stored.Children("lines"));
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
        var customer = Commerce.One(context, "Customers.CustomerById", ("customerId", record.Ref("customer")!.Id));
        var lines = record.Children("lines");
        if (lines.Count == 0) return HandlerResult.Fail(null, "The cart is empty.");
        var tax = TaxRules.For(context, Orders.Country(customer));
        var priced = Orders.Price(lines.Select(l => Orders.FromCatalogue(context, tax, l.Ref("product")!.Id, l.Get<int>("quantity"))), tax);
        var orderId = context.Data.Create("Order", Orders.Values(priced, ("owner", record.Get<string>("owner")), ("cartId", Commerce.Id(record.Id!.Value)),
            ("customer", customer.Id), ("shipping", customer.GetValueObject("shipping")!.ToDictionary()), ("currency", "CHF"),
            ("placedAt", context.UtcNow), ("expiresAt", context.UtcNow.AddMinutes(30))));
        Orders.Reserve(context, orderId, priced.Lines.Select(l => l.Line));
        context.Data.Run("SealCart", record.Id!.Value);
        return HandlerResult.Ok.Set("order", orderId);
    }
}

/// <summary>AddOrderItem, ChangeOrderItem and RemoveOrderItem on a pending order. A product that stays keeps its price
/// snapshot; a new or swapped-in product takes the current catalogue price. All held stock is released and the new
/// quantities reserved again, then the lines, VAT breakdown and totals are rebuilt.</summary>
[KaruContract("Sales.EditOrder")]
public sealed class EditOrder : CommandHandler
{
    public override HandlerResult Handle(EntityRecord record, EntityRecord input, HandlerContext context)
    {
        if (record.Get<string>("status") != "PendingPayment") return HandlerResult.Fail(null, "Only pending orders can be changed.");
        if (context.UtcNow >= record.Get<DateTime>("expiresAt")) return HandlerResult.Fail(null, "This order has expired.");
        var tax = TaxRules.For(context, Orders.Country(record));
        var lines = record.Children("lines").Select(l => Orders.FromLine(l, tax)).ToList();
        var product = input.Ref("product")!.Id;
        var index = lines.FindIndex(l => l.Product == product);
        if (Commerce.Is(input, "AddOrderItem"))
        {
            var quantity = input.Get<int>("quantity");
            if (index >= 0) lines[index] = lines[index] with { Quantity = (int)Math.Min((long)lines[index].Quantity + quantity, int.MaxValue) };
            else lines.Add(Orders.FromCatalogue(context, tax, product, quantity));
        }
        else
        {
            if (index < 0) return HandlerResult.Fail(null, "The product is not in this order.");
            if (Commerce.Is(input, "RemoveOrderItem"))
            {
                if (lines.Count == 1) return HandlerResult.Fail(null, "An order needs at least one line; cancel the order instead.");
                lines.RemoveAt(index);
            }
            else
            {
                var quantity = input.Get<int>("quantity");
                var target = input.Ref("newProduct")?.Id ?? product;
                if (target == product) lines[index] = lines[index] with { Quantity = quantity };
                else
                {
                    var line = lines[index];
                    lines.RemoveAt(index);
                    var other = lines.FindIndex(l => l.Product == target);
                    if (other >= 0) lines[other] = lines[other] with { Quantity = (int)Math.Min((long)lines[other].Quantity + quantity, int.MaxValue) };
                    else lines.Insert(index, Orders.FromCatalogue(context, tax, target, quantity, line.LineId));
                }
            }
        }
        if (lines.Any(l => l.Quantity > Commerce.MaxLineQuantity)) return HandlerResult.Fail(null, "An order line may contain at most 1,000,000 items.");
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
        var tax = TaxRules.For(context, Orders.Country(record));
        var priced = Orders.Price(record.Children("lines").Select(l => Orders.FromLine(l, tax, keepId: false)), tax);
        var replacement = context.Data.Create("Order", Orders.Values(priced, ("owner", record.Get<string>("owner")),
            ("customer", record.Ref("customer")), ("shipping", record.GetValueObject("shipping")!.ToDictionary()), ("currency", record.Get<string>("currency")),
            ("placedAt", context.UtcNow), ("expiresAt", context.UtcNow.AddMinutes(30)), ("replaces", record.Id)));
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
        context.Data.Create("PaymentAttempt", Commerce.Values(("order", record.Id), ("attemptKey", key), ("kind", "Charge"), ("amount", amount),
            ("outcome", outcome), ("reference", reference), ("at", context.UtcNow)));
        if (outcome == "Succeeded") context.Data.Run("ConfirmOrder", record.Id!.Value);
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
        var reservations = Commerce.All(context, "HeldReservations", ("orderId", record.Id));
        foreach (var reservation in reservations)
        {
            context.Data.Run(Consume ? "Catalogue.ConsumeStock" : "Catalogue.ReleaseStock", reservation.Ref("stock")!.Id, Commerce.Values(("quantity", reservation.Get<int>("quantity"))));
            context.Data.Run(Consume ? "ConsumeReservation" : "ReleaseReservation", reservation.Id!.Value);
        }
        if (record.Ref("replaces") is { } original)
        {
            // A paid replacement supersedes its original; a cancelled or expired one frees the original for another try.
            if (Consume) context.Data.Run("SupersedeOrder", original.Id);
            else context.Data.Run("UnlinkReplacement", original.Id, Commerce.Values(("replacement", record.Id)));
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
        foreach (var reservation in Commerce.All(context, "ConsumedReservations", ("orderId", record.Id)))
            context.Data.Run("Catalogue.ReturnStock", reservation.Ref("stock")!.Id, Commerce.Values(("quantity", reservation.Get<int>("quantity"))));
        var key = $"refund:{record.Id}";
        if (Commerce.All(context, "PaymentByKey", ("key", key)).Count != 0) return HandlerResult.Ok;
        var amount = record.Get<decimal>("total");
        string? reference;
        try
        {
            var answer = context.Port("Payments").Call("Refund", Commerce.Values(("key", key), ("amount", amount)));
            if (answer.Get<string?>("outcome") != "Succeeded") return HandlerResult.Fail(null, "Refund unavailable, try again.");
            reference = answer.Get<string?>("reference");
        }
        catch (PortException) { return HandlerResult.Fail(null, "Refund unavailable, try again."); }
        context.Data.Create("PaymentAttempt", Commerce.Values(("order", record.Id), ("attemptKey", key), ("kind", "Refund"), ("amount", amount),
            ("outcome", "Succeeded"), ("reference", reference), ("at", context.UtcNow)));
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
