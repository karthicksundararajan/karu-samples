# Ecommerce backend

A Karu application built from the [user guide](../../karu/docs/guide/README.md), with catalogue, customer profiles, carts, stock reservations, checkout, simulated payments and manual fulfilment. The application consumes the Karu SDK packages; it does not implement a separate HTTP server.

Catalogue prices are CHF list prices. VAT depends on the destination country of the order's shipping address:
- In CH, DE and AT, the list price includes VAT.
- In the US, VAT is added on top. The US rates are illustrative sample data, not real sales tax.

The rate depends on the product category's tax class (`Standard`, `Reduced` or `Special`). Shipping is free. There is one stock pool, one shipment per order, and no guest checkout, discounts or returns of shipped goods. Paid orders can be cancelled with a refund, or replaced, before they ship. The payment adapter is a deterministic local simulation and accepts no real payment credentials.

## Prepare and run

Requires .NET 10, Python 3 for HTTP tests, and the adjacent Karu source checkout with the transaction-data prerequisite. No additional NuGet or npm dependencies are needed beyond Karu and its existing xUnit stack.

From this folder:

```bash
export PATH="$HOME/.dotnet:$PATH"
./tools/prepare.sh                    # packs the six Karu packages, builds CLI/runtime and this solution
./tools/karu validate .
./tools/karu migrations check .
./tools/karu migrate .
./tools/runtime --model . --environment Development --urls http://localhost:5180
```

The helper scripts default to `../../karu`; pass a source directory to `prepare.sh` and set `KARU_SOURCE` for other scripts if your layout differs. `packages/` and `.packages/` are ignored local artifacts. Preparation refreshes only this application's cached `0.0.0-dev` Karu packages so rebuilding local source cannot silently reuse an old SDK. Released packages and the runtime must come from the same Karu version; update `KaruVersion` together when adopting a release containing transaction data support.

The runtime serves the HTTP API without requiring a frontend build. A separately installed Karu web bundle may be supplied with `--web-root` for computed administration screens; no storefront is included.

Development identities:

| User | Role |
|---|---|
| `alice.dev`, `bob.dev` | Customer |
| `catalogue.dev` | CatalogueManager |
| `warehouse.dev` | Warehouse |
| `support.dev` | Support |
| `admin.dev` | All staff roles |

In another terminal, seed the VAT rules (CH, DE, AT and the illustrative US rates), four categories (`Demo equipment` and `Electronics` are Standard; `Books` and `Food` are Reduced), sample products, and the two customer profiles (Alice ships to Zurich, Bob to Boston):

```bash
python3 tools/seed.py --url http://localhost:5180
```

It prints the created/existing IDs. Repeating it does not duplicate records or replenish existing stock.

### Swiss sample products

The seed publishes these additional products with 20 units of stock each. For Alice's CH shipping address, the CHF list price includes VAT:

| SKU | Product | VAT rate | Excluding VAT | VAT | Including VAT |
|---|---|---|---|---|---|
| CH-HEADPHONES | Wireless headphones | 8.1% | 100.00 | 8.10 | 108.10 |
| CH-KEYBOARD | USB keyboard | 8.1% | 50.00 | 4.05 | 54.05 |
| CH-COFFEE | Roasted coffee beans 250 g | 2.6% | 10.00 | 0.26 | 10.26 |
| CH-CHOCOLATE | Swiss milk chocolate 100 g | 2.6% | 5.00 | 0.13 | 5.13 |
| CH-BOOK | Swiss hiking guide | 2.6% | 20.00 | 0.52 | 20.52 |

These use the [Swiss Federal Tax Administration's rates](https://www.estv.admin.ch/en/vat-rates-switzerland): 8.1% standard and 2.6% reduced for food sold as goods and books. The 3.8% special rate is for accommodation, so none of these goods uses it.

Sign in as `alice.dev`, create a cart for Alice, add one of each Swiss product and check out. The order shows **CHF 185.00 excluding VAT + CHF 13.06 VAT = CHF 198.06 total**, with separate Standard and Reduced VAT breakdown rows. VAT is extracted per line as `round(price × quantity × rate / (100 + rate), 2)`, using midpoint rounding away from zero; net is the line total minus VAT. Changing quantities can change rounding. Catalogue records store the list price; the calculated VAT appears on the order after checkout.

## Order overview in the administration UI

From the ecommerce folder, build and serve Karu's UI:

```bash
export KARU_SOURCE="$(realpath ../../karu)"
(cd "$KARU_SOURCE/web" && npm ci && npm run build)
./tools/runtime --model . --environment Development --urls http://localhost:5180 \
  --web-root "$KARU_SOURCE/web/dist/karu-web/browser"
```

Restart the runtime after changing the model or UI YAML.

Open **Sales → Orders** and select an order. Its detail page shows:
- the order summary;
- the **Amounts**, one per line: currency, whether prices include VAT, subtotal (list price × quantity), total excluding VAT, total VAT and total;
- the shipment address, carrier and tracking;
- the **Replacement** links;
- the order references;
- the **Lines** grid, with each product's quantity, price snapshot, tax class and rate, total excluding VAT, VAT and total;
- the **Vat breakdown** grid, one row per tax class and rate;
- the payment history, with charges and refunds.

The grids are read-only and refresh after commands such as Pay order. Pending orders offer Add, Change and Remove order item. Paid orders offer Replace order and Cancel paid order. Payment history is visible to Customer, Support and Admin. Warehouse sees the lines and the shipment information.

To compare VAT handling, check out the same products as `alice.dev` (CH: the total equals the list prices, and VAT is extracted from them) and as `bob.dev` (US: VAT is added to the list prices).

**Sales → Carts** shows a cart with its lines as an editable grid while it is Open. Saving merges duplicate products and refuses unavailable ones. After checkout, the grid is read-only.

Shipment is currently one shipment per order, represented by the order's status, shipping address, carrier and tracking fields. There is no separate shipment history entity.

## API walkthrough

Karu returns records directly, with numeric `id` values and references shaped as `{ "id": 1, "display": "..." }`. Lists and queries return `{ "items": [...], "total": ..., "page": ..., "size": ... }`. Decimal amounts are strings with two decimal places.

Sign in and retain the cookie:

```bash
curl -c /tmp/ecommerce.cookies -H 'Content-Type: application/json' \
  -d '{"user":"alice.dev"}' http://localhost:5180/api/session
```

Use the customer and product IDs printed by the seed command. The following example assumes both are `1`:

```bash
curl -b /tmp/ecommerce.cookies -H 'Content-Type: application/json' \
  -d '{"customer":1}' http://localhost:5180/api/data/sales/Cart
# Use the returned cart id below (example: 1).
curl -b /tmp/ecommerce.cookies -H 'Content-Type: application/json' \
  -d '{"product":1,"quantity":2}' http://localhost:5180/api/command/sales/AddItem/1
curl -b /tmp/ecommerce.cookies -H 'Content-Type: application/json' \
  -d '{"requestKey":"checkout-001"}' http://localhost:5180/api/command/sales/Checkout/1
# The cart now contains an order reference. Use that order id below (example: 1).
curl -b /tmp/ecommerce.cookies -H 'Content-Type: application/json' \
  -d '{"attemptKey":"payment-001","method":"Success"}' http://localhost:5180/api/command/sales/PayOrder/1
curl -b /tmp/ecommerce.cookies http://localhost:5180/api/query/sales/MyOrders
```

Checkout snapshots the current catalogue price and customer address, reserves every line atomically and starts a 30-minute payment window. Repeat checkout with the same cart and request key to recover its order reference. A different key on an already checked-out cart is rejected. The cart is immutable after checkout; create another cart for a new purchase.

Payment methods are `Success`, `Decline` and `Timeout`. A payment attempt is identified by order and attempt key. Repeating a key returns the recorded result without calling the adapter again. Use a new key to retry a declined or timed-out attempt. A successful payment moves the order to `Paid`, reduces on-hand stock and consumes reservations. A decline or timeout leaves the reservation held until payment, cancellation or expiry. Inspect `/api/data/sales/PaymentAttempt` to see outcomes.

Sign in as `warehouse.dev` with a separate cookie, then ship and deliver:

```bash
curl -c /tmp/ecommerce.warehouse -H 'Content-Type: application/json' \
  -d '{"user":"warehouse.dev"}' http://localhost:5180/api/session
curl -b /tmp/ecommerce.warehouse -H 'Content-Type: application/json' \
  -d '{"carrier":"Manual","tracking":"CH-001"}' http://localhost:5180/api/command/sales/ShipOrder/1
curl -b /tmp/ecommerce.warehouse -H 'Content-Type: application/json' \
  -d '{}' http://localhost:5180/api/command/sales/DeliverOrder/1
```

Useful operations:

| Operation | Route / input |
|---|---|
| Published catalogue | `GET /api/query/catalogue/PublishedProducts` |
| Register profile | `POST /api/data/customers/Customer`: `name`, `email`, required `shipping` address |
| Update own profile | `PUT /api/data/customers/Customer/{id}`: `name`, `email`, `shipping`; omit owner |
| Add quantity to cart | `POST /api/command/sales/AddItem/{cartId}`: `product`, positive `quantity` |
| Change a cart line | `POST /api/command/sales/ChangeItem/{cartId}`: `product`, new `quantity`, optional `newProduct` |
| Remove cart product | `POST /api/command/sales/RemoveItem/{cartId}`: `product` |
| Edit cart lines | `PUT /api/data/sales/Cart/{cartId}`: the cart with its `lines` array (`id`, `product`, `quantity`) |
| Add to a pending order | `POST /api/command/sales/AddOrderItem/{orderId}`: `product`, positive `quantity` |
| Change a pending order line | `POST /api/command/sales/ChangeOrderItem/{orderId}`: `product`, new `quantity`, optional `newProduct` |
| Remove from a pending order | `POST /api/command/sales/RemoveOrderItem/{orderId}`: `product` (not the last line) |
| Cancel unpaid order | `POST /api/command/sales/CancelOrder/{orderId}`: `{}` |
| Cancel a paid order with refund | `POST /api/command/sales/CancelPaidOrder/{orderId}`: `{}` |
| Replace a paid order | `POST /api/command/sales/ReplaceOrder/{orderId}`: `requestKey` |
| Adjust inventory | `POST /api/command/catalogue/AdjustStock/{stockId}`: signed `quantity` |
| Publish / discontinue | `POST /api/command/catalogue/PublishProduct/{id}` or `DiscontinueProduct/{id}` |

Creating a product creates its zero-balance Stock record. Staff adjust that record before selling. Products progress from Draft to Active to Discontinued; staff may change names and prices, while historical order lines retain their snapshots. Commercial records have no public delete permission. Cart and order lines live inside their cart or order. They have no pages, permissions or queries of their own, and the root's row filter covers them. Removing a product from a cart deletes its line.

### Changing pending orders

A `PendingPayment` order can be changed by its customer until the payment window ends.
- Prices: a product that stays in the order keeps its price snapshot. A new or swapped-in product takes the current catalogue price.
- Stock: every change releases the order's held reservations and reserves the new quantities in the same transaction.
- Totals: the lines, VAT breakdown and totals are rebuilt.
- The last line cannot be removed. Use `CancelOrder` to cancel the whole order.

### Cancelling and replacing paid orders

A `Paid` order that has not shipped can be cancelled with `CancelPaidOrder` (Customer for their own orders, Support and Admin). The stock goes back on hand, and the charge is refunded through the payment port. The refund is recorded as a `Refund` payment attempt. If the refund fails, nothing changes; try again.

To change a paid order, call `ReplaceOrder` with a request key:
1. A new `PendingPayment` order is created with the same line snapshots and its own stock reservations. It links to the original through `replaces`, and the original links to it through `replacement`. Repeating the key returns the same replacement.
2. Edit the replacement with the order item commands. Removing lines cancels them.
3. Pay the replacement with `PayOrder`. The original becomes `Replaced`, its stock is returned, and its charge is refunded.

If the replacement is cancelled or expires, the original's `replacement` link is cleared and it can be replaced again. While a replacement is open, the original cannot be shipped or cancelled.

Known limitation: the original's goods stay allocated until the replacement is paid. A replacement therefore needs stock available on top of the original's quantities.

Customers, carts, orders and payment attempts declare `ownership:`: the platform stamps the owner from the authenticated subject when a record is created, refuses an owner sent by a client, shows each customer only their own records (Support, Warehouse and Admin see all, as listed per entity), and accepts a reference to a customer profile or order only when it belongs to the caller. Customers cannot invoke internal `[System]` commands or write order lines, VAT amounts, reservations, balances, totals or payment outcomes through generic CRUD. Cart lines accept only `product` and `quantity`. Staff access is limited by each feature's permission lists.

## Tests

```bash
dotnet test Ecommerce.slnx
./tools/karu validate .
./tools/karu test .
./tools/karu migrations check .
python3 tests/http_test.py
python3 tests/http_test.py --ui       # requires the built Karu web app and Playwright Chromium
```

Scenarios use migrated temporary SQLite databases and fixed clocks. The HTTP suite copies the application into a temporary folder, migrates twice and starts a real runtime. It checks isolation, ownership spoofing, protected writes, price snapshots, VAT included and on top, cart and pending-order edits, replay handling, declines and timeouts, cancellation with and without refund, replacement, fulfilment, final-item competition, rollback and audit. Only that isolated SQLite fixture uses an accelerated job schedule and an expired deadline to exercise scheduled release. The checked-in job runs once per minute in UTC.

PostgreSQL HTTP tests require a **dedicated empty test database**. The suite migrates and populates it and leaves it for inspection; remove it afterward. An unset variable reports a skip:

```bash
export KARU_ECOMMERCE_TEST_POSTGRES='Host=localhost;Port=5432;Username=karu;Password=karu;Database=ecommerce_test'
python3 tests/http_test.py --postgres
```

Changes to Karu itself require its backend build/tests, including PostgreSQL tests with `KARU_TEST_POSTGRES` configured. No frontend code is changed by this application.

## PostgreSQL and deployment

Keep this folder in its own Git repository so Karu can stamp responses and audits with its model commit.

Create an empty PostgreSQL database and set `KARU_POSTGRES_CONNECTION`; credentials stay outside YAML:

```bash
./tools/karu migrate . --datasource postgres
./tools/runtime --model . --datasource postgres --environment Development --urls http://localhost:5180
```

For deployment, copy the application and built extension output, replace `infra/identity.yaml` with `config/identity.production.yaml`, and configure `Identity__Authority`, `Identity__ClientId` and `KARU_OIDC_CLIENT_SECRET`. Follow the Karu guide for HTTPS, OIDC, migrations, backups and service hosting. Unsafe requests under OIDC must send Karu's antiforgery token. Development identity is refused outside Development.

The simulation is not a real payment integration. External charges cannot be rolled back with SQL; adopting a real provider requires a separate reconciliation and external-idempotency design. This implementation intentionally ships only the local adapter.

`Expired` orders release reservations through `ExpireUnpaidOrders`; monitor `karu_job_run` for failures. `Shipped` orders require carrier and tracking. `Delivered`, `Cancelled`, `Expired` and `Replaced` are terminal. Karu records business changes and their causes in its audit tables.
