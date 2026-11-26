using Ecommerce.Extensions;
using Karu.Extensions.Sdk;
using Karu.Extensions.Testing;

namespace Ecommerce.Extensions.Tests;

public sealed class CommerceTests
{
    static readonly ExtensionTestHost Host = ExtensionTestHost.Load(FindRoot(), typeof(PlaceOrder).Assembly);
    static readonly DateTime Now = new(2026, 11, 22, 12, 0, 0, DateTimeKind.Utc);
    static string FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "app.yaml"))) return dir.FullName;
        throw new InvalidOperationException("Cannot find app.yaml");
    }
    static Dictionary<string, object?> V(params (string Key, object? Value)[] values) => values.ToDictionary(v => v.Key, v => v.Value);
    static readonly Dictionary<string, object?> Zurich = V(("line1", "Main 1"), ("city", "Zurich"), ("postalCode", "8000"), ("country", "CH"));
    static readonly Dictionary<string, object?> Boston = V(("line1", "Main 1"), ("city", "Boston"), ("postalCode", "02108"), ("country", "US"));

    /// <summary>Handler data with the CH (VAT included) and US (VAT on top) rules, and the Mug (2, Standard, 12.35, Active)
    /// and Book (3, Reduced, 20.00, Active) products in stock.</summary>
    static FakeHandlerData Catalogue(string contract, decimal mugPrice = 12.35m)
    {
        var data = Host.Data(contract);
        var grants = Host.Model.FindContract(contract)!.Data!;
        if (grants.Queries.Contains("Tax.CountryByCode"))
            data.OnQuery("Tax.CountryByCode", p => (string)p["code"]! switch
            {
                "CH" => [Host.Record("Tax.Country", V(("id", 41L), ("code", "CH"), ("pricesIncludeVAT", true)))],
                "US" => [Host.Record("Tax.Country", V(("id", 1L), ("code", "US"), ("pricesIncludeVAT", false)))],
                _ => [],
            })
            .OnQuery("Tax.VatRatesFor", p => (long)p["countryId"]! == 41L
                ? [Rate("Standard", 8.1m), Rate("Reduced", 2.6m), Rate("Special", 3.8m)]
                : [Rate("Standard", 8.0m), Rate("Reduced", 2.0m)]);
        if (grants.Queries.Contains("Catalogue.ProductById"))
            data.OnQuery("Catalogue.ProductById", p => (long)p["productId"]! switch
            {
                2L => [Product(2, "MUG", "Mug", mugPrice, category: 10)],
                3L => [Product(3, "BOOK", "Book", 20.00m, category: 11)],
                _ => [Product(4, "OLD", "Old", 1.00m, category: 10, status: "Discontinued")],
            });
        if (grants.Queries.Contains("Catalogue.CategoryById"))
            data.OnQuery("Catalogue.CategoryById", p => [Host.Record("Catalogue.Category", V(("id", p["categoryId"]), ("taxClass", (long)p["categoryId"]! == 11L ? "Reduced" : "Standard")))]);
        if (grants.Queries.Contains("Catalogue.StockByProduct"))
            data.OnQuery("Catalogue.StockByProduct", p => [Host.Record("Catalogue.Stock", V(("id", 100L + (long)p["productId"]!), ("onHand", 50), ("reserved", 0)))]);
        return data;
    }
    static EntityRecord Rate(string taxClass, decimal rate) => Host.Record("Tax.VatRate", V(("id", (long)(rate * 10)), ("taxClass", taxClass), ("rate", rate)));
    /// <summary>Asserts the values of a written item or record.</summary>
    static void Has(IReadOnlyDictionary<string, object?> item, params (string Key, object? Value)[] expected)
    {
        foreach (var (key, value) in expected) Assert.True(Equals(value, item[key]), $"{key}: expected {value}, got {item[key]}");
    }
    static EntityRecord Product(long id, string sku, string name, decimal price, long category, string status = "Active") =>
        Host.Record("Catalogue.Product", V(("id", id), ("sku", sku), ("name", name), ("price", price), ("status", status), ("category", new RecordRef(category, "Category"))));
    static Dictionary<string, object?> Line(long id, long product, int quantity, decimal unitPrice, string taxClass = "Standard") =>
        V(("id", id), ("product", new RecordRef(product, "P")), ("sku", "SKU" + product), ("name", "Product " + product), ("taxClass", taxClass),
            ("vatRate", 8.1m), ("unitPrice", unitPrice), ("quantity", quantity), ("totalExclVAT", 0m), ("totalVAT", 0m), ("total", 0m));
    static EntityRecord PendingOrder(params Dictionary<string, object?>[] lines) => Host.Record("Sales.Order", V(("id", 1L), ("owner", "dev:alice.dev"),
        ("status", "PendingPayment"), ("shipping", Zurich), ("expiresAt", Now.AddMinutes(30)), ("lines", lines), ("vatBreakdown", Array.Empty<Dictionary<string, object?>>())));

    // Pricing

    [Fact]
    public void Prices_that_include_vat_extract_it_from_the_gross()
    {
        var line = Pricing.Line(new PriceInput(1, "MUG", "Mug", "Standard", 8.1m, 12.35m, 2), pricesIncludeVat: true);
        Assert.Equal((22.85m, 1.85m, 24.70m), (line.Net, line.Vat, line.Total));
    }

    [Fact]
    public void Prices_without_vat_add_it_on_top()
    {
        var line = Pricing.Line(new PriceInput(1, "MUG", "Mug", "Standard", 8.0m, 12.35m, 2), pricesIncludeVat: false);
        Assert.Equal((24.70m, 1.98m, 26.68m), (line.Net, line.Vat, line.Total));
    }

    [Fact]
    public void Mixed_tax_classes_get_one_breakdown_row_per_rate_and_totals_sum_the_lines()
    {
        var order = Pricing.Price([
            new PriceInput(1, "BOOK", "Book", "Reduced", 2.6m, 20.00m, 1),
            new PriceInput(2, "MUG", "Mug", "Standard", 8.1m, 12.35m, 2),
            new PriceInput(3, "CUP", "Cup", "Standard", 8.1m, 5.00m, 1)], pricesIncludeVat: true);
        Assert.Equal(["Standard", "Reduced"], order.Vat.Select(v => v.TaxClass));
        var standard = order.Vat[0];
        Assert.Equal((8.1m, 29.70m), (standard.Rate, standard.Gross));
        Assert.Equal(1.85m + 0.37m, standard.Vat);
        Assert.Equal(0.51m, order.Vat[1].Vat);
        Assert.Equal(49.70m, order.Total);
        Assert.Equal(order.Total, order.Net + order.VatTotal);
        Assert.Equal(order.Lines.Sum(l => l.Vat), order.VatTotal);
    }

    [Theory]
    [InlineData(false, 0.05, 10, 0.01)]   // 0.005 rounds away from zero
    [InlineData(true, 1.00, 8.1, 0.07)]   // 0.0749... rounds down
    [InlineData(true, 0.00, 8.1, 0.00)]
    public void Each_line_is_rounded_to_a_cent_away_from_zero(bool included, double price, double rate, double vat) =>
        Assert.Equal((decimal)vat, Pricing.Line(new PriceInput(1, "X", "X", "Standard", (decimal)rate, (decimal)price, 1), included).Vat);

    // Stock

    [Fact]
    public void Reserved_stock_cannot_exceed_on_hand()
    {
        var result = Host.Check("Catalogue.ValidStock", Host.Record("Catalogue.Stock", V(("onHand", 2), ("reserved", 3))));
        Assert.False(result.Valid);
    }

    [Fact]
    public void Stock_adjustment_cannot_invalidate_a_reservation()
    {
        var data = Host.Data("Catalogue.AdjustBalance");
        var result = Host.Handle("Catalogue.AdjustStock", Host.Record("Catalogue.Stock", V(("id", 1L), ("onHand", 5), ("reserved", 4))),
            Host.Input("Catalogue.AdjustStock", V(("quantity", -2))), new HandlerServices(data, "dev:warehouse.dev", Now));
        Assert.False(result.Valid);
        Assert.Empty(data.Creates);
    }

    [Fact]
    public void Successful_stock_change_records_a_movement_and_exact_balance()
    {
        var data = Host.Data("Catalogue.ReserveBalance");
        var result = Host.Handle("Catalogue.ReserveStock", Host.Record("Catalogue.Stock", V(("id", 1L), ("onHand", 5), ("reserved", 1))),
            Host.Input("Catalogue.ReserveStock", V(("quantity", 2))), new HandlerServices(data, "dev:alice.dev", Now));
        Assert.True(result.Valid);
        Assert.Equal(3, result.Changes.Single(p => p.Key == "reserved").Value);
        Assert.Equal("Reservation", Assert.Single(data.Creates).Values["kind"]);
    }

    [Fact]
    public void Returned_stock_goes_back_on_hand()
    {
        var data = Host.Data("Catalogue.ReturnBalance");
        var result = Host.Handle("Catalogue.ReturnStock", Host.Record("Catalogue.Stock", V(("id", 1L), ("onHand", 5), ("reserved", 1))),
            Host.Input("Catalogue.ReturnStock", V(("quantity", 2))), new HandlerServices(data, null, Now));
        Assert.Equal(7, result.Changes.Single(p => p.Key == "onHand").Value);
        Assert.Equal("Return", Assert.Single(data.Creates).Values["kind"]);
    }

    // Carts

    static EntityRecord OpenCart(params Dictionary<string, object?>[] lines) => Host.Record("Sales.Cart", V(("id", 1L), ("owner", "dev:alice.dev"),
        ("customer", new RecordRef(7, "Alice")), ("status", "Open"), ("lines", lines)));

    /// <summary>AddItem has already merged the line: the handler accepts it and leaves the lines as proposed.</summary>
    [Fact]
    public void Adding_to_a_cart_leaves_the_proposed_lines()
    {
        var stored = OpenCart(V(("id", 5L), ("product", new RecordRef(2, "Mug")), ("quantity", 1)));
        var data = Catalogue("Sales.ChangeCart").OnQuery("CartById", [stored]);
        var proposed = OpenCart(V(("id", 5L), ("product", new RecordRef(2, "Mug")), ("quantity", 3)));
        var result = Host.Handle("Sales.AddItem", proposed, Host.Input("Sales.AddItem", V(("product", new RecordRef(2, "Mug")), ("quantity", 2))),
            new HandlerServices(data, "dev:alice.dev", Now));
        Assert.True(result.Valid);
        Assert.Null(result.Children("lines"));
    }

    /// <summary>A discontinued product may stay or shrink in a cart, but its quantity cannot grow.</summary>
    [Fact]
    public void An_unavailable_product_cannot_grow()
    {
        var stored = OpenCart(V(("id", 5L), ("product", new RecordRef(4, "Old")), ("quantity", 2)));
        var data = Catalogue("Sales.ChangeCart").OnQuery("CartById", [stored]);
        HandlerOutcome Change(int quantity) => Host.Handle("Sales.ChangeItem", OpenCart(V(("id", 5L), ("product", new RecordRef(4, "Old")), ("quantity", quantity))),
            Host.Input("Sales.ChangeItem", V(("product", new RecordRef(4, "Old")), ("quantity", quantity))), new HandlerServices(data, "dev:alice.dev", Now));
        Assert.True(Change(1).Valid);
        Assert.Equal("The product is unavailable.", Assert.Single(Change(3).Errors).Message);
    }

    [Fact]
    public void A_cart_update_cannot_change_the_customer_or_add_an_unavailable_product()
    {
        var stored = OpenCart();
        var data = Catalogue("Sales.ChangeCart").OnQuery("CartById", [stored]);
        var otherCustomer = Host.Record("Sales.Cart", V(("id", 1L), ("owner", "dev:alice.dev"), ("customer", new RecordRef(8, "Bob")), ("status", "Open"), ("lines", Array.Empty<Dictionary<string, object?>>())));
        Assert.False(Host.Handle("Sales.UpdateCart", otherCustomer, Host.Input("Sales.UpdateCart", V()), new HandlerServices(data, "dev:alice.dev", Now)).Valid);
        var discontinued = OpenCart(V(("product", new RecordRef(4, "Old")), ("quantity", 1)));
        Assert.False(Host.Handle("Sales.UpdateCart", discontinued, Host.Input("Sales.UpdateCart", V()), new HandlerServices(data, "dev:alice.dev", Now)).Valid);
    }

    [Fact]
    public void A_new_cart_cannot_carry_an_order()
    {
        var cart = Host.Record("Sales.Cart", V(("customer", new RecordRef(7, "Alice")), ("order", new RecordRef(3, "3")), ("status", "Open"),
            ("lines", Array.Empty<Dictionary<string, object?>>())));
        var result = Host.Handle("Sales.CreateCart", cart, Host.Input("Sales.CreateCart", V()), new HandlerServices(Catalogue("Sales.OpenCart"), "dev:alice.dev", Now));
        Assert.Equal("order", Assert.Single(result.Errors).Field);
    }

    // Checkout

    [Fact]
    public void Empty_cart_cannot_be_checked_out()
    {
        var data = Host.Data("Sales.PlaceOrder").OnQuery("Customers.CustomerById", [Host.Record("Customers.Customer", V(("id", 7L), ("owner", "dev:alice.dev")))]);
        var result = Host.Handle("Sales.Checkout", OpenCart(), Host.Input("Sales.Checkout", V(("requestKey", "one"))), new HandlerServices(data, "dev:alice.dev", Now));
        Assert.False(result.Valid);
        Assert.Empty(data.Creates);
    }

    static FakeHandlerData CheckoutData(Dictionary<string, object?> shipping) => Catalogue("Sales.PlaceOrder")
        .OnQuery("Customers.CustomerById", [Host.Record("Customers.Customer", V(("id", 7L), ("owner", "dev:alice.dev"), ("shipping", shipping)))]);

    [Fact]
    public void Checkout_creates_the_order_with_its_lines_and_vat_and_reserves_stock()
    {
        var data = CheckoutData(Zurich);
        var cart = OpenCart(V(("id", 1L), ("product", new RecordRef(2, "Mug")), ("quantity", 2)), V(("id", 2L), ("product", new RecordRef(3, "Book")), ("quantity", 1)));
        var result = Host.Handle("Sales.Checkout", cart, Host.Input("Sales.Checkout", V(("requestKey", "checkout"))), new HandlerServices(data, "dev:alice.dev", Now));
        Assert.True(result.Valid, string.Join("; ", result.Errors.Select(e => e.Message)));
        var order = data.Creates.Single(c => c.Entity == "Sales.Order").Values;
        Assert.Equal(44.70m, order["total"]);
        Assert.Equal(true, order["pricesIncludeVAT"]);
        Assert.Equal(44.70m, order["subtotal"]);
        Assert.Equal(Now.AddMinutes(30), order["expiresAt"]);
        var lines = (List<IReadOnlyDictionary<string, object?>>)order["lines"]!;
        Assert.Equal(new object?[] { 12.35m, 20.00m }, lines.Select(l => l["unitPrice"]));
        Assert.Equal(new object?[] { "Standard", "Reduced" }, lines.Select(l => l["taxClass"]));
        Assert.Equal(2, ((List<IReadOnlyDictionary<string, object?>>)order["vatBreakdown"]!).Count);
        Assert.DoesNotContain(data.Creates, c => c.Entity == "Sales.OrderLine");
        Assert.Equal(2, data.Creates.Count(c => c.Entity == "Sales.Reservation"));
        Assert.Contains(data.Calls, c => c.Command == "Catalogue.ReserveStock" && (int)c.Input["quantity"]! == 2);
        Assert.Contains(data.Calls, c => c.Command == "Sales.SealCart");
    }

    [Fact]
    public void Checkout_to_the_us_adds_vat_on_top()
    {
        var data = CheckoutData(Boston);
        var cart = OpenCart(V(("id", 1L), ("product", new RecordRef(2, "Mug")), ("quantity", 2)));
        Assert.True(Host.Handle("Sales.Checkout", cart, Host.Input("Sales.Checkout", V(("requestKey", "us"))), new HandlerServices(data, "dev:alice.dev", Now)).Valid);
        var order = data.Creates.Single(c => c.Entity == "Sales.Order").Values;
        Has(order, ("subtotal", 24.70m), ("totalExclVAT", 24.70m), ("totalVAT", 1.98m), ("total", 26.68m), ("pricesIncludeVAT", false));
    }

    [Fact]
    public void Checkout_refuses_a_country_without_vat_rules()
    {
        var data = CheckoutData(V(("line1", "1"), ("city", "Paris"), ("postalCode", "75001"), ("country", "FR")));
        var cart = OpenCart(V(("id", 1L), ("product", new RecordRef(2, "Mug")), ("quantity", 1)));
        var result = Host.Handle("Sales.Checkout", cart, Host.Input("Sales.Checkout", V(("requestKey", "fr"))), new HandlerServices(data, "dev:alice.dev", Now));
        Assert.Equal("We cannot sell to FR.", Assert.Single(result.Errors).Message);
        Assert.Empty(data.Creates);
    }

    // Editing pending orders

    /// <summary>The handler sees the lines with the edit applied: a merged line keeps its id and snapshot, a new line (id
    /// null, only product and quantity) takes the catalogue price and tax class.</summary>
    [Fact]
    public void Editing_an_order_keeps_the_price_of_a_staying_product_and_reprices_a_new_one()
    {
        var data = Catalogue("Sales.EditOrder", mugPrice: 99.00m);
        var services = new HandlerServices(data, "dev:alice.dev", Now);

        var more = Host.Handle("Sales.AddOrderItem", PendingOrder(Line(10, 2, 3, 12.35m)),
            Host.Input("Sales.AddOrderItem", V(("product", new RecordRef(2, "Mug")), ("quantity", 1))), services);
        var line = Assert.Single(more.Children("lines")!);
        Has(line, ("id", 10L), ("quantity", 3), ("unitPrice", 12.35m), ("total", 37.05m));
        Assert.Equal(37.05m, more.Changes.Single(c => c.Key == "total").Value);

        var swapped = Host.Handle("Sales.ChangeOrderItem", PendingOrder(V(("product", new RecordRef(3, "Book")), ("quantity", 1))), Host.Input("Sales.ChangeOrderItem",
            V(("product", new RecordRef(2, "Mug")), ("quantity", 1), ("newProduct", new RecordRef(3, "Book")))), services);
        var book = Assert.Single(swapped.Children("lines")!);
        Has(book, ("product", 3L), ("sku", "BOOK"), ("unitPrice", 20.00m), ("taxClass", "Reduced"));
        Assert.False(book.ContainsKey("id"));
        Assert.Equal("Reduced", Assert.Single(swapped.Children("vatBreakdown")!)["taxClass"]);
    }

    [Fact]
    public void Editing_an_order_releases_and_reserves_its_stock_again()
    {
        var data = Catalogue("Sales.EditOrder").OnQuery("HeldReservations", [Host.Record("Sales.Reservation", V(("id", 30L), ("stock", new RecordRef(102, "Mug")), ("quantity", 2)))]);
        var order = PendingOrder(Line(10, 2, 5, 12.35m));
        var result = Host.Handle("Sales.ChangeOrderItem", order, Host.Input("Sales.ChangeOrderItem", V(("product", new RecordRef(2, "Mug")), ("quantity", 5))),
            new HandlerServices(data, "dev:alice.dev", Now));
        Assert.True(result.Valid);
        Assert.Equal(["Catalogue.ReleaseStock", "Sales.ReleaseReservation", "Catalogue.ReserveStock"], data.Calls.Select(c => c.Command));
        Assert.Equal(5, Assert.Single(data.Creates, c => c.Entity == "Sales.Reservation").Values["quantity"]);
    }

    [Fact]
    public void The_last_line_of_an_order_cannot_be_removed()
    {
        var data = Catalogue("Sales.EditOrder");
        var result = Host.Handle("Sales.RemoveOrderItem", PendingOrder(),
            Host.Input("Sales.RemoveOrderItem", V(("product", new RecordRef(2, "Mug")))), new HandlerServices(data, "dev:alice.dev", Now));
        Assert.False(result.Valid);
        Assert.Contains("cancel the order", Assert.Single(result.Errors).Message);
        Assert.Empty(data.Calls);
    }

    // Replacement and refunds

    static EntityRecord PaidOrder(long? replacement = null) => Host.Record("Sales.Order", V(("id", 1L), ("owner", "dev:alice.dev"), ("status", "Paid"),
        ("customer", new RecordRef(7, "Alice")), ("shipping", Zurich), ("currency", "CHF"), ("total", 24.70m),
        ("replacement", replacement is { } r ? new RecordRef(r, "O-2") : null), ("lines", new[] { Line(10, 2, 2, 12.35m) }),
        ("vatBreakdown", Array.Empty<Dictionary<string, object?>>())));

    [Fact]
    public void Replacing_an_order_copies_its_snapshots_and_links_the_replacement()
    {
        var data = Catalogue("Sales.StartReplacement").OnCreate("Order", _ => 2L);
        var result = Host.Handle("Sales.ReplaceOrder", PaidOrder(), Host.Input("Sales.ReplaceOrder", V(("requestKey", "r1"))), new HandlerServices(data, "dev:alice.dev", Now));
        Assert.True(result.Valid, string.Join("; ", result.Errors.Select(e => e.Message)));
        var replacement = data.Creates.Single(c => c.Entity == "Sales.Order").Values;
        Has(replacement, ("replaces", 1L), ("total", 24.70m));
        Assert.False(replacement.ContainsKey("checkoutKey"));
        Assert.Equal(2L, result.Changes.Single(c => c.Key == "replacement").Value);
        Assert.Single(data.Creates, c => c.Entity == "Sales.Reservation");
    }

    [Fact]
    public void A_superseded_order_returns_its_stock_and_is_refunded_once()
    {
        var data = Host.Data("Sales.SettleReplacedOrder")
            .OnQuery("ConsumedReservations", [Host.Record("Sales.Reservation", V(("id", 30L), ("stock", new RecordRef(102, "Mug")), ("quantity", 2)))]);
        var port = Host.Fake("Sales.Payments").On("Refund", _ => V(("outcome", "Succeeded"), ("reference", "simr_1")));
        var ports = new Dictionary<string, IPortClient> { ["Sales.Payments"] = port };
        var result = Host.Handle("Sales.SupersedeOrder", PaidOrder(), Host.Input("Sales.SupersedeOrder", V()), new HandlerServices(data, null, Now), ports);
        Assert.True(result.Valid);
        Assert.Equal(("Catalogue.ReturnStock", 102L, 2), (data.Calls[0].Command, data.Calls[0].Id, (int)data.Calls[0].Input["quantity"]!));
        var (operation, input) = Assert.Single(port.Calls);
        Assert.Equal(("Refund", 24.70m), (operation, input.Get<decimal>("amount")));
        var refund = Assert.Single(data.Creates).Values;
        Has(refund, ("kind", "Refund"), ("attemptKey", "refund:1"), ("amount", 24.70m));
    }

    [Fact]
    public void A_failing_refund_fails_the_cancellation()
    {
        var data = Host.Data("Sales.RefundOrder");
        var port = Host.Fake("Sales.Payments").Fails("Refund", PortFailureKind.TimedOut);
        var result = Host.Handle("Sales.CancelPaidOrder", PaidOrder(), Host.Input("Sales.CancelPaidOrder", V()), new HandlerServices(data, "dev:alice.dev", Now),
            new Dictionary<string, IPortClient> { ["Sales.Payments"] = port });
        Assert.Equal("Refund unavailable, try again.", Assert.Single(result.Errors).Message);
        Assert.Empty(data.Creates);
    }

    [Fact]
    public void A_cancelled_replacement_unlinks_its_original()
    {
        var data = Host.Data("Sales.ReleaseReservations");
        var replacement = Host.Record("Sales.Order", V(("id", 2L), ("replaces", new RecordRef(1, "O-1"))));
        Assert.True(Host.Handle("Sales.CancelOrder", replacement, Host.Input("Sales.CancelOrder", V()), new HandlerServices(data, "dev:alice.dev", Now)).Valid);
        var call = Assert.Single(data.Calls);
        Assert.Equal(("Sales.UnlinkReplacement", 1L, 2L), (call.Command, call.Id, (long)call.Input["replacement"]!));
    }

    // Unchanged behaviour

    [Fact]
    public void Expiry_refuses_before_the_deadline_and_accepts_at_the_deadline()
    {
        var record = Host.Record("Sales.Order", V(("id", 1L), ("expiresAt", Now)));
        var input = Host.Input("Sales.ExpireOrder", V());
        Assert.False(Host.Handle("Sales.ExpireOrder", record, input, new HandlerServices(Host.Data("Sales.ExpireReservations"), null, Now.AddMilliseconds(-1))).Valid);
        Assert.True(Host.Handle("Sales.ExpireOrder", record, input, new HandlerServices(Host.Data("Sales.ExpireReservations"), null, Now)).Valid);
    }

    [Fact]
    public async Task Expiry_job_handles_more_than_one_page_without_skipping_orders()
    {
        var context = Host.JobContext("ExpirePendingOrders");
        context.Clock = Now;
        context.OnQuery("Sales.PendingOrders", Enumerable.Range(1, 201).Select(i => new JobRow(i, ("expiresAt", Now))));
        await Host.RunJob(new ExpirePendingOrders(), context);
        Assert.Equal(201, context.Calls.Count);
        Assert.Equal(201, context.Calls.Select(c => c.Id).Distinct().Count());
    }

    [Theory]
    [InlineData("Success", "Succeeded")]
    [InlineData("Decline", "Declined")]
    public async Task Payment_simulation_is_deterministic(string method, string outcome)
    {
        var port = Host.Adapter("Sales.Payments", new SimulatedPayments());
        var input = V(("key", "attempt-1"), ("amount", 12.34m), ("method", method));
        var first = await Task.Run(() => port.Call("Charge", input));
        var second = port.Call("Charge", input);
        Assert.Equal(outcome, first.Get<string>("outcome"));
        Assert.Equal(first.Get<string?>("reference"), second.Get<string?>("reference"));
        Assert.Equal("Succeeded", port.Call("Refund", V(("key", "refund:1"), ("amount", 12.34m))).Get<string>("outcome"));
    }

    [Fact]
    public void A_payment_timeout_is_recorded_without_consuming_stock()
    {
        var data = Host.Data("Sales.TakePayment");
        var port = Host.Fake("Sales.Payments").Fails("Charge", PortFailureKind.TimedOut);
        var record = Host.Record("Sales.Order", V(("id", 1L), ("owner", "dev:alice.dev"), ("status", "PendingPayment"), ("total", 12.35m), ("expiresAt", Now.AddMinutes(30))));
        var result = Host.Handle("Sales.PayOrder", record, Host.Input("Sales.PayOrder", V(("attemptKey", "one"), ("method", "Timeout"))),
            new HandlerServices(data, "dev:alice.dev", Now), new Dictionary<string, IPortClient> { ["Sales.Payments"] = port });
        Assert.True(result.Valid);
        var attempt = Assert.Single(data.Creates).Values;
        Has(attempt, ("outcome", "TimedOut"), ("kind", "Charge"), ("amount", 12.35m));
        Assert.Empty(data.Calls);
    }
}
