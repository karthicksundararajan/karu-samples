// Browser acceptance checks against the isolated runtime started by http_test.py --ui.
const path = require('node:path');
const fs = require('node:fs');
const { createRequire } = require('node:module');
const source = process.env.KARU_SOURCE || path.resolve(__dirname, '../../../karu');
const requireWeb = createRequire(path.join(source, 'web/package.json'));
const { chromium, expect } = requireWeb('@playwright/test');
const [base, orderId] = process.argv.slice(2);

(async () => {
  const browser = await chromium.launch({ headless: true });
  try {
    for (const width of [1280, 390]) {
      const context = await browser.newContext({ viewport: { width, height: 900 } });
      await context.request.post(base + '/api/session', { data: { user: 'alice.dev' } });
      const page = await context.newPage();
      await page.goto(`${base}/sales/order/${orderId}`);
      const lines = page.getByTestId('child-grid-lines');
      const vat = page.getByTestId('child-grid-vatBreakdown');
      const payments = page.getByTestId('related-grid').filter({ has: page.getByRole('heading', { name: 'Payments', exact: true }) });
      await expect(lines.getByRole('heading', { name: 'Lines', exact: true })).toBeVisible();
      await expect(lines).toHaveAttribute('data-readonly', 'true');
      await expect(lines.getByTestId('field-sku').first()).toHaveValue('SKU-1');
      await expect(lines.getByTestId('field-unitPrice').first()).toHaveValue('12.35');
      await expect(lines.getByTestId('field-totalVAT').first()).toHaveValue('1.85');
      await expect(vat.getByRole('heading', { name: 'Vat breakdown', exact: true })).toBeVisible();
      await expect(vat.getByTestId('field-rate').first()).toHaveValue('8.10');
      await expect(vat.getByTestId('field-total').first()).toHaveValue('24.70');
      const amounts = page.getByTestId('section').filter({ has: page.getByRole('heading', { name: 'Amounts', exact: true }) });
      await expect(amounts).toBeVisible();
      await expect(amounts.locator('[data-layout="stacked"]')).toHaveCount(1);
      await expect(amounts.getByTestId('field-pricesIncludeVAT')).toBeChecked();
      await expect(amounts.getByTestId('field-subtotal')).toHaveValue('24.70');
      await expect(amounts.getByTestId('field-totalExclVAT')).toHaveValue('22.85');
      await expect(amounts.getByTestId('field-totalVAT')).toHaveValue('1.85');
      await expect(amounts.getByTestId('field-total')).toHaveValue('24.70');
      // One amount per line, top to bottom: subtotal, total excl VAT, total VAT, total.
      const tops = [];
      for (const f of ['subtotal', 'totalExclVAT', 'totalVAT', 'total']) tops.push((await amounts.getByTestId('field-' + f).boundingBox()).y);
      expect(tops).toEqual([...tops].sort((a, b) => a - b));
      expect(new Set(tops).size).toBe(4);
      await expect(page.getByRole('heading', { name: 'Replacement', exact: true })).toBeVisible();
      await expect(payments.getByRole('cell', { name: 'Succeeded', exact: true })).toBeVisible();
      await expect(payments.getByRole('cell', { name: 'Declined', exact: true })).toBeVisible();
      await expect(payments.getByRole('cell', { name: 'Timed out', exact: true })).toBeVisible();
      await expect(payments.getByRole('cell', { name: 'Charge', exact: true }).first()).toBeVisible();
      await expect(page.getByRole('heading', { name: 'Shipment', exact: true })).toBeVisible();
      await expect(page.getByTestId('field-carrier')).toHaveValue('Manual');
      await expect(page.getByTestId('field-tracking')).toHaveValue('CH-001');
      expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true);
      const output = path.join(__dirname, '../TestResults');
      fs.mkdirSync(output, { recursive: true });
      await page.screenshot({ path: path.join(output, `order-overview-${width}.png`), fullPage: true });

      // Failure and retry are local to the payment table; the order and its lines remain visible.
      let fail = true;
      await page.route('**/api/query/sales/OrderPayments?*', route => {
        if (fail) { fail = false; return route.fulfill({ status: 500, json: { errors: [{ message: 'temporary' }] } }); }
        return route.continue();
      });
      await page.reload();
      await expect(payments.getByRole('alert')).toBeVisible();
      await expect(lines.getByTestId('field-sku').first()).toHaveValue('SKU-1');
      await payments.getByRole('button', { name: 'Retry' }).click();
      await expect(payments.getByRole('cell', { name: 'Succeeded', exact: true })).toBeVisible();

      // Pagination binds the same parent id on every request.
      await page.route('**/api/query/sales/OrderPayments?*', route => {
        const url = new URL(route.request().url());
        expect(url.searchParams.get('p.orderId')).toBe(orderId);
        const second = url.searchParams.get('page') === '2';
        return route.fulfill({ json: { items: Array.from({ length: second ? 1 : 20 }, (_, i) => ({ id: i + 1, kind: 'Charge', outcome: 'Succeeded', amount: '1.00', reference: second ? 'last-payment' : `payment-${i}`, at: '2026-11-22T12:00:00Z' })), total: 21 } });
      });
      await page.reload();
      await payments.getByRole('button', { name: 'Next' }).click();
      await expect(payments.getByRole('cell', { name: 'last-payment', exact: true })).toBeVisible();
      await expect(payments.getByRole('button', { name: 'Next' })).toBeDisabled();
      await context.close();
    }

    // The cart grid is editable while the cart is open and read-only after checkout; a pending order offers the order
    // item commands, a paid one replacement and cancellation; a payment command refreshes the related data in place.
    const context = await browser.newContext();
    const api = context.request;
    await api.post(base + '/api/session', { data: { user: 'alice.dev' } });
    const customers = await (await api.get(base + '/api/data/customers/Customer')).json();
    const products = await (await api.get(base + '/api/query/catalogue/PublishedProducts')).json();
    const product = products.items.find(p => p.sku === 'DEMO-MUG');
    const cart = await (await api.post(base + '/api/data/sales/Cart', { data: { customer: customers.items[0].id } })).json();
    expect((await api.post(`${base}/api/command/sales/AddItem/${cart.id}`, { data: { product: product.id, quantity: 1 } })).ok()).toBe(true);
    const page = await context.newPage();
    await page.goto(`${base}/sales/cart/${cart.id}`);
    const cartLines = page.getByTestId('child-grid-lines');
    await expect(cartLines).toHaveAttribute('data-readonly', 'false');
    await expect(cartLines.getByTestId('child-add-lines')).toBeVisible();
    await expect(cartLines.getByTestId('field-quantity').first()).toHaveValue('1');
    await cartLines.getByTestId('field-quantity').first().fill('2');
    await page.getByTestId('save').click();
    await expect(page.getByTestId('saved')).toBeVisible();
    await expect(cartLines.getByTestId('field-quantity').first()).toHaveValue('2');

    const checked = await (await api.post(`${base}/api/command/sales/Checkout/${cart.id}`, { data: { requestKey: 'ui-checkout' } })).json();
    await page.reload();
    await expect(cartLines).toHaveAttribute('data-readonly', 'true');
    await expect(cartLines.getByTestId('child-add-lines')).toHaveCount(0);

    await page.goto(`${base}/sales/order/${checked.order.id}`);
    for (const action of ['AddOrderItem', 'ChangeOrderItem', 'RemoveOrderItem', 'PayOrder', 'CancelOrder'])
      await expect(page.getByTestId(`record-action-${action}`)).toBeVisible();
    await expect(page.getByTestId('child-grid-lines').getByTestId('field-quantity').first()).toHaveValue('2');
    const payments = page.getByTestId('related-grid').filter({ has: page.getByRole('heading', { name: 'Payments', exact: true }) });
    await expect(payments).toContainText('No records to show.');
    await page.getByTestId('record-action-PayOrder').click();
    await page.getByTestId('input-dialog').getByTestId('field-attemptKey').fill('ui-pay');
    await page.getByTestId('input-dialog').getByTestId('field-method').selectOption('Success');
    await page.getByTestId('input-submit').click();
    await expect(payments.getByRole('cell', { name: 'Succeeded', exact: true })).toBeVisible();
    await expect(page.getByTestId('field-status')).toHaveValue('Paid');
    await expect(page.getByTestId('record-action-ReplaceOrder')).toBeVisible();
    await expect(page.getByTestId('record-action-CancelPaidOrder')).toBeVisible();
    await expect(page.getByTestId('record-action-AddOrderItem')).toHaveCount(0);
    await context.close();
    console.log('PASS UI: order amounts, lines and VAT grids, payments and shipment; desktop/mobile, retry, pagination; cart grid editing; order actions and payment refresh');
  } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
