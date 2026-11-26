#!/usr/bin/env python3
"""HTTP acceptance tests using an isolated SQLite copy, or a dedicated empty PostgreSQL database."""
import argparse
import concurrent.futures
from datetime import datetime, timedelta, timezone
import http.cookiejar
import json
import os
from pathlib import Path
import shutil
import socket
import sqlite3
import subprocess
import tempfile
import time
import urllib.error
import urllib.parse
import urllib.request

APP = Path(__file__).resolve().parents[1]
SOURCE = Path(os.environ.get('KARU_SOURCE', APP / '../../karu')).resolve()
DOTNET = str(Path.home() / '.dotnet/dotnet')
CLI = [DOTNET, str(SOURCE / 'src/Karu.Cli/bin/Debug/net10.0/karu.dll')]
RUNTIME = [DOTNET, str(SOURCE / 'src/Karu.Runtime/bin/Debug/net10.0/Karu.Runtime.dll')]

class Client:
    def __init__(self, base, user):
        self.base = base
        self.opener = urllib.request.build_opener(urllib.request.HTTPCookieProcessor(http.cookiejar.CookieJar()))
        self.call('POST', '/api/session', {'user': user})
    def call(self, method, path, body=None, expected=200):
        request = urllib.request.Request(self.base + path, data=None if body is None else json.dumps(body).encode(), method=method,
                                         headers={'Content-Type': 'application/json'})
        try:
            with self.opener.open(request, timeout=20) as response:
                status, raw = response.status, response.read()
        except urllib.error.HTTPError as error:
            status, raw = error.code, error.read()
        value = json.loads(raw) if raw else None
        allowed = expected if isinstance(expected, (list, tuple)) else [expected]
        assert status in allowed, (method, path, status, value, allowed)
        return value
    def create(self, feature, entity, body, expected=201):
        return self.call('POST', f'/api/data/{feature}/{entity}', body, expected)
    def get(self, feature, entity, ident, expected=200):
        return self.call('GET', f'/api/data/{feature}/{entity}/{ident}', expected=expected)
    def command(self, feature, name, ident, body=None, expected=200):
        return self.call('POST', f'/api/command/{feature}/{name}/{ident}', body or {}, expected)
    def rows(self, feature, entity):
        answer = self.call('GET', f'/api/data/{feature}/{entity}?size=100')
        return answer['items']

def run(base, folder, postgres):
    admin = Client(base, 'admin.dev')
    warehouse = Client(base, 'warehouse.dev')
    alice = Client(base, 'alice.dev')
    bob = Client(base, 'bob.dev')
    address = {'line1': 'Main street 1', 'city': 'Zurich', 'postalCode': '8000', 'country': 'CH'}
    us_address = {'line1': '1 Main Street', 'city': 'Boston', 'postalCode': '02108', 'country': 'US'}
    # VAT rules: CH list prices include VAT; the illustrative US rates are added on top.
    ch = admin.create('tax', 'Country', {'code': 'CH', 'name': 'Switzerland', 'pricesIncludeVAT': True})
    us = admin.create('tax', 'Country', {'code': 'US', 'name': 'United States', 'pricesIncludeVAT': False})
    for country, tax_class, rate in [(ch, 'Standard', '8.10'), (ch, 'Reduced', '2.60'), (us, 'Standard', '8.00'), (us, 'Reduced', '2.00')]:
        admin.create('tax', 'VatRate', {'rateKey': f"{country['code']}:{tax_class}", 'country': country['id'], 'taxClass': tax_class, 'rate': rate})
    alice.create('tax', 'Country', {'code': 'DE', 'name': 'Germany', 'pricesIncludeVAT': True}, 403)
    def customer(client, name, shipping=address):
        return client.create('customers', 'Customer', {'name': name, 'email': name+'@example.test', 'shipping': shipping})
    ac, bc = customer(alice, 'Alice'), customer(bob, 'Bob', us_address)
    assert ac['owner'] == 'dev:alice.dev'
    alice.create('customers', 'Customer', {'owner': 'dev:bob.dev', 'name': 'Spoof', 'email': 'x@example.test', 'shipping': address}, 422)
    bob.get('customers', 'Customer', ac['id'], 404)
    alice.call('PUT', f"/api/data/customers/Customer/{ac['id']}", {'owner':'dev:bob.dev','name':'Alice','email':'alice@example.test','shipping':address}, 422)
    assert alice.call('PUT', f"/api/data/customers/Customer/{ac['id']}", {'name':'Alice','email':'alice@example.test','shipping':address})['owner'] == 'dev:alice.dev'
    assert [c['id'] for c in alice.rows('customers', 'Customer')] == [ac['id']]
    category = admin.create('catalogue', 'Category', {'name': 'Equipment'})
    assert category['taxClass'] == 'Standard', category
    books = admin.create('catalogue', 'Category', {'name': 'Books', 'taxClass': 'Reduced'})
    def product(sku, price, quantity, category=category):
        p = admin.create('catalogue', 'Product', {'sku': sku, 'name': sku, 'category': category['id'], 'price': price})
        alice.get('catalogue', 'Product', p['id'], 404)
        admin.command('catalogue', 'PublishProduct', p['id'])
        stock = next(s for s in warehouse.rows('catalogue', 'Stock') if s['product']['id'] == p['id'])
        warehouse.command('catalogue', 'AdjustStock', stock['id'], {'quantity': quantity})
        return p, stock
    p, stock = product('SKU-1', '12.35', 10)
    p2, stock2 = product('BOOK-1', '20.00', 10, books)
    def cart(client, profile, prod=p, quantity=2):
        c = client.create('sales','Cart',{'customer':profile['id']})
        client.command('sales','AddItem',c['id'],{'product':prod['id'],'quantity':quantity})
        return c
    c = cart(alice, ac)
    alice.command('sales','AddItem',c['id'],{'product':p['id'],'quantity':0},422)
    # Cart lines are edited in the cart's grid (PUT with lines) or with ChangeItem; clients cannot price them.
    edit = cart(alice, ac)
    current = alice.get('sales','Cart',edit['id'])
    first = current['lines'][0]
    grid = {'customer': ac['id'], 'order': None}
    # Lines are keyed by product: an update that lists a product twice is refused, not merged.
    duplicate = alice.call('PUT', f"/api/data/sales/Cart/{edit['id']}", {**grid, 'lines': [
        {'id': first['id'], 'product': p['id'], 'quantity': 1}, {'product': p['id'], 'quantity': 2}]}, 422)
    assert [(e['field'], e['code']) for e in duplicate['errors']] == [('lines[1].product', 'duplicateKey')], duplicate
    updated = alice.call('PUT', f"/api/data/sales/Cart/{edit['id']}", {**grid, 'lines': [
        {'id': first['id'], 'product': p['id'], 'quantity': 3}, {'product': p2['id'], 'quantity': 1}]})
    lines = {l['product']['id']: l for l in updated['lines']}
    assert len(updated['lines']) == 2 and lines[p['id']]['quantity'] == 3 and lines[p['id']]['id'] == first['id'] and lines[p2['id']]['quantity'] == 1, updated
    alice.call('PUT', f"/api/data/sales/Cart/{edit['id']}", {**grid, 'lines': [{'product': p['id'], 'quantity': 1, 'unitPrice': '0.01'}]}, 422)
    alice.call('PUT', f"/api/data/sales/Cart/{edit['id']}", {**grid, 'lines': [{'product': p['id'], 'quantity': 1, 'totalVAT': '0.00'}]}, 422)
    alice.call('PUT', f"/api/data/sales/Cart/{edit['id']}", {**grid, 'customer': bc['id'], 'lines': []}, 422)
    bob.call('PUT', f"/api/data/sales/Cart/{edit['id']}", {**grid, 'lines': []}, 404)
    alice.command('sales','ChangeItem',edit['id'],{'product':p2['id'],'quantity':4})
    changed = alice.command('sales','ChangeItem',edit['id'],{'product':p2['id'],'quantity':4,'newProduct':p['id']})
    assert [(l['product']['id'], l['quantity']) for l in changed['lines']] == [(p['id'], 7)], changed
    missing = alice.command('sales','ChangeItem',edit['id'],{'product':p2['id'],'quantity':1},422)
    assert [(e['field'], e['code']) for e in missing['errors']] == [('product', 'notFound')], missing
    assert alice.command('sales','RemoveItem',edit['id'],{'product':p['id']})['lines'] == []
    alice.create('sales','Cart',{'customer':bc['id']},422)
    alice.create('sales','Cart',{'customer':ac['id'],'owner':'dev:bob.dev'},422)
    bob.get('sales','Cart',c['id'],404)
    bob.command('sales','Checkout',c['id'],{'requestKey':'steal'},404)
    checked = alice.command('sales','Checkout',c['id'],{'requestKey':'checkout-1'})
    oid = checked['order']['id']
    order = alice.get('sales','Order',oid)
    bob.get('sales','Order',oid,404)
    bob.command('sales','AddOrderItem',oid,{'product':p['id'],'quantity':1},404)
    assert order['total'] == '24.70' and order['currency'] == 'CHF' and order['status'] == 'PendingPayment', order
    # CH: the list prices include VAT, so the total is their sum and VAT is extracted from it.
    assert (order['pricesIncludeVAT'], order['subtotal'], order['totalExclVAT'], order['totalVAT']) == (True, '24.70', '22.85', '1.85'), order
    assert [(l['sku'], l['unitPrice'], l['quantity'], l['taxClass'], l['vatRate'], l['totalExclVAT'], l['totalVAT'], l['total']) for l in order['lines']] == [('SKU-1', '12.35', 2, 'Standard', '8.10', '22.85', '1.85', '24.70')], order
    assert [(v['taxClass'], v['rate'], v['total']) for v in order['vatBreakdown']] == [('Standard', '8.10', '24.70')], order
    # The checked-out cart's lines are read-only.
    alice.call('PUT', f"/api/data/sales/Cart/{c['id']}", {'customer': ac['id'], 'order': oid, 'lines': []}, 409)
    # US: VAT is added on top of the list prices.
    us_cart = cart(bob, bc)
    us_order = bob.get('sales','Order', bob.command('sales','Checkout',us_cart['id'],{'requestKey':'us'})['order']['id'])
    assert (us_order['pricesIncludeVAT'], us_order['subtotal'], us_order['totalExclVAT'], us_order['totalVAT'], us_order['total']) == (False, '24.70', '24.70', '1.98', '26.68'), us_order
    bob.command('sales','CancelOrder',us_order['id'])
    assert alice.command('sales','Checkout',c['id'],{'requestKey':'checkout-1'})['order']['id'] == oid
    alice.command('sales','Checkout',c['id'],{'requestKey':'different'},409)
    alice.command('sales','AddItem',c['id'],{'product':p['id'],'quantity':1},409)
    bob.get('sales','Order',oid,404)
    bob.command('sales','PayOrder',oid,{'attemptKey':'steal','method':'Success'},404)
    assert [o['id'] for o in bob.rows('sales','Order')] == [us_order['id']]
    assert bob.rows('sales','PaymentAttempt') == []
    assert bob.call('GET',f'/api/query/sales/OrderPayments?p.orderId={oid}')['items'] == []
    assert alice.call('GET',f'/api/query/sales/OrderPayments?p.orderId={oid}')['items'] == []
    # Lines are part of the order: they have no endpoint, query or page of their own.
    alice.call('GET','/api/data/sales/OrderLine',expected=404)
    alice.call('GET','/api/query/sales/OrderItems?p.orderId=1',expected=404)
    metadata = warehouse.call('GET','/api/metadata')
    detail = next(p for p in metadata['pages'] if p['name'] == 'OrderDetail')
    assert detail.get('related',[]) == []   # Warehouse cannot read payments
    customer_detail = next(p for p in alice.call('GET','/api/metadata')['pages'] if p['name'] == 'OrderDetail')
    assert [b['title']['text'] for b in customer_detail.get('related',[])] == ['Payments']
    assert [c['name'] for c in detail.get('children',[])] == ['lines','vatBreakdown'], detail.get('children')
    alice.call('PUT',f'/api/data/sales/Order/{oid}',{'total':'0.01'},403)
    alice.create('sales','OrderLine',{'order':oid,'quantity':1,'unitPrice':'0.01'},404)
    alice.command('sales','ConfirmOrder',oid,expected=403)
    alice.command('catalogue','ConsumeStock',stock['id'],{'quantity':1},403)
    alice.call('GET','/api/data/catalogue/Stock',expected=403)
    warehouse.command('catalogue','AdjustStock',stock['id'],{'quantity':-9},422)
    # Changing the catalogue after checkout cannot alter the order snapshot.
    admin.call('PUT',f"/api/data/catalogue/Product/{p['id']}",{'sku':p['sku'],'name':'Renamed','category':category['id'],'price':'99.00'})
    assert alice.get('sales','Order',oid)['lines'][0]['unitPrice'] == '12.35'
    # A pending order can be edited: a staying product keeps its snapshot, a new one takes the current price, and the
    # reservations and totals follow.
    edit_order = alice.command('sales','Checkout', cart(alice, ac, p, 1)['id'], {'requestKey':'edit'})['order']['id']
    reserved = warehouse.get('catalogue','Stock',stock['id'])['reserved']
    admin.call('PUT',f"/api/data/catalogue/Product/{p['id']}",{'sku':p['sku'],'name':'Renamed','category':category['id'],'price':'15.00'})
    edited = alice.command('sales','AddOrderItem',edit_order,{'product':p['id'],'quantity':1})
    assert (edited['total'], edited['lines'][0]['unitPrice'], edited['lines'][0]['quantity']) == ('198.00', '99.00', 2), edited
    assert warehouse.get('catalogue','Stock',stock['id'])['reserved'] == reserved + 1
    edited = alice.command('sales','AddOrderItem',edit_order,{'product':p2['id'],'quantity':1})
    assert (edited['subtotal'], edited['total'], edited['totalVAT']) == ('218.00', '218.00', '15.35'), edited
    assert [(v['taxClass'], v['rate'], v['totalVAT']) for v in edited['vatBreakdown']] == [('Standard', '8.10', '14.84'), ('Reduced', '2.60', '0.51')], edited
    assert warehouse.get('catalogue','Stock',stock2['id'])['reserved'] == 1
    edited = alice.command('sales','ChangeOrderItem',edit_order,{'product':p2['id'],'quantity':1,'newProduct':p['id']})
    assert [(l['sku'], l['unitPrice'], l['quantity']) for l in edited['lines']] == [('SKU-1', '99.00', 3)], edited
    assert warehouse.get('catalogue','Stock',stock2['id'])['reserved'] == 0
    alice.command('sales','RemoveOrderItem',edit_order,{'product':p['id']},422)
    bob.command('sales','AddOrderItem',edit_order,{'product':p['id'],'quantity':1},404)
    alice.command('sales','CancelOrder',edit_order)
    alice.command('sales','AddOrderItem',edit_order,{'product':p['id'],'quantity':1},409)
    assert warehouse.get('catalogue','Stock',stock['id'])['reserved'] == reserved - 1
    alice.call('PUT',f"/api/data/customers/Customer/{ac['id']}",{'name':'Alice','email':'alice@example.test','shipping':{**address,'line1':'New street 2'}})
    assert alice.get('sales','Order',oid)['shipping'] == address
    assert alice.command('sales','PayOrder',oid,{'attemptKey':'declined','method':'Decline'})['status'] == 'PendingPayment'
    # Same key, other input: the platform refuses the reused key and runs nothing.
    alice.command('sales','PayOrder',oid,{'attemptKey':'declined','method':'Success'},409)
    assert len(alice.rows('sales','PaymentAttempt')) == 1
    assert alice.command('sales','PayOrder',oid,{'attemptKey':'timeout','method':'Timeout'})['status'] == 'PendingPayment'
    assert alice.command('sales','PayOrder',oid,{'attemptKey':'success','method':'Success'})['status'] == 'Paid'
    alice.command('sales','PayOrder',oid,{'attemptKey':'success','method':'Success'})
    alice.command('sales','PayOrder',oid,{'attemptKey':'new','method':'Success'},409)
    current = warehouse.get('catalogue','Stock',stock['id'])
    assert (current['onHand'],current['reserved']) == (8,0), current
    alice.command('sales','CancelOrder',oid,expected=409)
    warehouse.command('sales','ShipOrder',oid,{'carrier':'Manual','tracking':'CH-001'})
    assert warehouse.command('sales','DeliverOrder',oid)['status'] == 'Delivered'
    warehouse.command('sales','DeliverOrder',oid,expected=409)
    def paid_order(key, *lines):
        order_cart = alice.create('sales','Cart',{'customer':ac['id']})
        for prod, quantity in lines: alice.command('sales','AddItem',order_cart['id'],{'product':prod['id'],'quantity':quantity})
        order_id = alice.command('sales','Checkout',order_cart['id'],{'requestKey':key})['order']['id']
        assert alice.command('sales','PayOrder',order_id,{'attemptKey':key,'method':'Success'})['status'] == 'Paid'
        return order_id
    def on_hand(): return warehouse.get('catalogue','Stock',stock['id'])['onHand'], warehouse.get('catalogue','Stock',stock2['id'])['onHand']
    def refunds(order_id): return [a for a in alice.rows('sales','PaymentAttempt') if a['order']['id'] == order_id and a['kind'] == 'Refund']
    # Replacement: replace a paid order, drop a line, pay the replacement; the original is refunded and restocked.
    original = paid_order('original', (p, 1), (p2, 1))
    paid = alice.get('sales','Order',original)
    before = on_hand()
    link = alice.command('sales','ReplaceOrder',original,{'requestKey':'r1'})['replacement']['id']
    assert alice.command('sales','ReplaceOrder',original,{'requestKey':'r1'})['replacement']['id'] == link
    alice.command('sales','ReplaceOrder',original,{'requestKey':'r2'},409)
    bob.command('sales','ReplaceOrder',original,{'requestKey':'r3'},404)
    warehouse.command('sales','ShipOrder',original,{'carrier':'Manual','tracking':'CH-2'},409)
    alice.command('sales','CancelPaidOrder',original,expected=409)
    replacement = alice.get('sales','Order',link)
    assert (replacement['replaces']['id'], replacement['status'], replacement['total'], len(replacement['lines'])) == (original, 'PendingPayment', paid['total'], 2), replacement
    assert replacement['cartId'] is None
    alice.command('sales','RemoveOrderItem',link,{'product':p2['id']})
    assert alice.command('sales','PayOrder',link,{'attemptKey':'pay-replacement','method':'Success'})['status'] == 'Paid'
    superseded = alice.get('sales','Order',original)
    assert superseded['status'] == 'Replaced', superseded
    [refund] = refunds(original)
    assert (refund['amount'], refund['outcome']) == (paid['total'], 'Succeeded'), refund
    assert on_hand() == (before[0], before[1] + 1), (before, on_hand())
    # A paid order without an open replacement can be cancelled with a refund, once.
    cancelled = paid_order('cancel-paid', (p2, 1))
    before = on_hand()
    bob.command('sales','CancelPaidOrder',cancelled,expected=404)
    assert alice.command('sales','CancelPaidOrder',cancelled)['status'] == 'Cancelled'
    alice.command('sales','CancelPaidOrder',cancelled,expected=409)
    assert on_hand() == (before[0], before[1] + 1)
    assert [r['amount'] for r in refunds(cancelled)] == [alice.get('sales','Order',cancelled)['total']]
    # Cancellation releases once, and subsequent requests cannot release again.
    cancel_cart = cart(alice,ac)
    cancel_order = alice.command('sales','Checkout',cancel_cart['id'],{'requestKey':'cancel'})['order']['id']
    alice.command('sales','CancelOrder',cancel_order)
    alice.command('sales','CancelOrder',cancel_order,expected=409)
    assert warehouse.get('catalogue','Stock',stock['id'])['reserved'] == 0
    # Competing customers cannot both reserve the final item.
    last,last_stock = product('LAST','1.00',1)
    left,right = cart(alice,ac,last,1),cart(bob,bc,last,1)
    def checkout(args):
        client,record = args
        return client.command('sales','Checkout',record['id'],{'requestKey':'race'},[200,422])
    with concurrent.futures.ThreadPoolExecutor(2) as executor:
        raced=list(executor.map(checkout,[(alice,left),(bob,right)]))
    assert sum('order' in answer for answer in raced) == 1, raced
    assert warehouse.get('catalogue','Stock',last_stock['id'])['reserved'] == 1
    # Multi-line failure must leave no partial order or reservation.
    multi=cart(alice,ac,p,1)
    alice.command('sales','AddItem',multi['id'],{'product':last['id'],'quantity':2})
    before=len(admin.rows('sales','Order'))
    alice.command('sales','Checkout',multi['id'],{'requestKey':'rollback'},422)
    assert len(admin.rows('sales','Order')) == before
    assert warehouse.get('catalogue','Stock',stock['id'])['reserved'] == 0
    empty=alice.create('sales','Cart',{'customer':ac['id']})
    alice.command('sales','Checkout',empty['id'],{'requestKey':'empty'},422)
    # Accelerate only the isolated SQLite fixture's deadline; the copied job runs every second.
    if not postgres:
        expiring_cart = cart(alice, ac, p, 1)
        expiring_id = alice.command('sales', 'Checkout', expiring_cart['id'], {'requestKey':'expire'})['order']['id']
        with sqlite3.connect(folder/'data/ecommerce.db') as db:
            db.execute("UPDATE sales_order SET expires_at = '2020-01-01 00:00:00.000' WHERE id = ?", (expiring_id,))
        alice.command('sales','PayOrder',expiring_id,{'attemptKey':'too-late','method':'Success'},422)
        for _ in range(100):
            if alice.get('sales','Order',expiring_id)['status'] == 'Expired': break
            time.sleep(.1)
        else: raise AssertionError('Scheduled expiry did not run')
        assert warehouse.get('catalogue','Stock',stock['id'])['reserved'] == 0
        alice.command('sales','CancelOrder',expiring_id,expected=409)
        # An expired replacement clears the original's link, so the order can be replaced again.
        original = paid_order('expire-original', (p2, 1))
        lapsing = alice.command('sales','ReplaceOrder',original,{'requestKey':'lapse'})['replacement']['id']
        with sqlite3.connect(folder/'data/ecommerce.db') as db:
            db.execute("UPDATE sales_order SET expires_at = '2020-01-01 00:00:00.000' WHERE id = ?", (lapsing,))
        for _ in range(100):
            if alice.get('sales','Order',lapsing)['status'] == 'Expired': break
            time.sleep(.1)
        else: raise AssertionError('Scheduled expiry of the replacement did not run')
        relinked = alice.get('sales','Order',original)
        assert (relinked['status'], relinked['replacement']) == ('Paid', None), relinked
        assert warehouse.get('catalogue','Stock',stock2['id'])['reserved'] == 0
        # Race payment with the expiry worker; only one terminal outcome may win the write gate.
        racing_cart = cart(alice, ac, p, 1)
        racing_id = alice.command('sales','Checkout',racing_cart['id'],{'requestKey':'expiry-race'})['order']['id']
        deadline = datetime.now(timezone.utc) + timedelta(milliseconds=250)
        with sqlite3.connect(folder/'data/ecommerce.db') as db:
            db.execute('UPDATE sales_order SET expires_at = ? WHERE id = ?', (deadline.strftime('%Y-%m-%d %H:%M:%S.%f')[:-3], racing_id))
        time.sleep(.23)
        alice.command('sales','PayOrder',racing_id,{'attemptKey':'race','method':'Success'},[200,409,422])
        for _ in range(100):
            terminal = alice.get('sales','Order',racing_id)['status']
            if terminal in ('Paid','Expired'): break
            time.sleep(.1)
        else: raise AssertionError('Payment/expiry race did not settle')
        assert warehouse.get('catalogue','Stock',stock['id'])['reserved'] == 0
        with sqlite3.connect(folder/'data/ecommerce.db') as db:
            checkout_audit = db.execute("SELECT id FROM karu_audit_entry WHERE command_name = 'Sales.Checkout' AND record_id = ? ORDER BY id LIMIT 1", (c['id'],)).fetchone()[0]
            order_cause = db.execute("SELECT a.cause_entry_id FROM karu_audit_entry a JOIN karu_entity_type e ON e.id = a.entity_type_id WHERE e.qualified_name = 'Sales.Order' AND a.record_id = ? AND a.action = 'Created'", (oid,)).fetchone()[0]
            assert order_cause == checkout_audit, (order_cause, checkout_audit)
    # Seed data is repeatable and must not replenish stock on a second invocation.
    first = subprocess.check_output(['python3',str(APP/'tools/seed.py'),'--url',base],text=True)
    balances = warehouse.rows('catalogue','Stock')
    second = subprocess.check_output(['python3',str(APP/'tools/seed.py'),'--url',base],text=True)
    assert json.loads(first) == json.loads(second)
    assert warehouse.rows('catalogue','Stock') == balances
    # The Swiss sample basket exercises both tax classes through real checkout.
    seeded = json.loads(first)
    swiss_customer = next(c for c in seeded['customers'] if c['user'] == 'alice.dev')
    swiss_cart = alice.create('sales', 'Cart', {'customer': swiss_customer['customerId']})
    swiss_skus = {'CH-HEADPHONES', 'CH-KEYBOARD', 'CH-COFFEE', 'CH-CHOCOLATE', 'CH-BOOK'}
    swiss_products = [p for p in seeded['products'] if p['sku'] in swiss_skus]
    assert len(swiss_products) == 5
    for product in swiss_products:
        alice.command('sales', 'AddItem', swiss_cart['id'], {'product': product['id'], 'quantity': 1})
    swiss_id = alice.command('sales', 'Checkout', swiss_cart['id'], {'requestKey': 'swiss-sample'})['order']['id']
    swiss_order = alice.get('sales', 'Order', swiss_id)
    assert swiss_order['pricesIncludeVAT'] is True
    assert (swiss_order['totalExclVAT'], swiss_order['totalVAT'], swiss_order['total']) == ('185.00', '13.06', '198.06'), swiss_order
    breakdown = {row['taxClass']: row for row in swiss_order['vatBreakdown']}
    assert (breakdown['Standard']['rate'], breakdown['Standard']['totalVAT']) == ('8.10', '12.15'), breakdown
    assert (breakdown['Reduced']['rate'], breakdown['Reduced']['totalVAT']) == ('2.60', '0.91'), breakdown
    alice.command('sales', 'CancelOrder', swiss_id)
    print('PASS HTTP: isolation, cart edits, VAT, snapshots, checkout, order edits, payments, replacement, refunds, fulfilment, cancellation, concurrency, rollback and seed' + (', expiry and audit' if not postgres else ''))
    return oid

def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--postgres', action='store_true', help='Use KARU_ECOMMERCE_TEST_POSTGRES: must name a dedicated empty database')
    parser.add_argument('--ui', action='store_true', help='Also test the built Karu UI in Chromium')
    args=parser.parse_args()
    if args.postgres and not os.environ.get('KARU_ECOMMERCE_TEST_POSTGRES'):
        print('SKIP PostgreSQL: KARU_ECOMMERCE_TEST_POSTGRES is unset'); return
    with tempfile.TemporaryDirectory(prefix='karu-ecommerce-') as temp:
        folder=Path(temp)/'app'
        shutil.copytree(APP,folder,ignore=shutil.ignore_patterns('packages','.packages','obj','data','.git','TestResults','__pycache__'))
        if not args.postgres:
            jobs=folder/'infra/jobs.yaml'
            jobs.write_text(jobs.read_text().replace('cron: 0 * * * * ?', "cron: '* * * * * ?'"))
        env=dict(os.environ)
        env['KARU_MODEL_COMMIT']='ecommerce-http-test'
        if args.postgres:
            env['KARU_POSTGRES_CONNECTION']=env['KARU_ECOMMERCE_TEST_POSTGRES']
            env['KARU_DATASOURCE']='postgres'
        subprocess.run(CLI+['migrate',str(folder)],check=True,env=env)
        subprocess.run(CLI+['migrate',str(folder)],check=True,env=env)
        with socket.socket() as sock:
            sock.bind(('127.0.0.1',0));port=sock.getsockname()[1]
        base=f'http://127.0.0.1:{port}'
        with (Path(temp)/'runtime.log').open('w+') as log:
            command=RUNTIME+['--model',str(folder),'--environment','Development','--urls',base]
            if args.ui: command += ['--web-root', str(SOURCE/'web/dist/karu-web/browser')]
            process=subprocess.Popen(command,env=env,stdout=log,stderr=subprocess.STDOUT)
            try:
                for _ in range(600):
                    try:
                        urllib.request.urlopen(base+'/api/session',timeout=1).close();break
                    except (urllib.error.URLError,TimeoutError):
                        if process.poll() is not None: raise RuntimeError('Runtime exited')
                        time.sleep(.1)
                else: raise RuntimeError('Runtime did not become ready')
                order_id = run(base,folder,args.postgres)
                if args.ui:
                    libs=SOURCE/'web/.pw-libs/root/usr/lib/x86_64-linux-gnu'
                    if libs.is_dir(): env['LD_LIBRARY_PATH']=str(libs)+':'+env.get('LD_LIBRARY_PATH','')
                    env['KARU_SOURCE']=str(SOURCE)
                    subprocess.run(['node',str(APP/'tests/ui_test.cjs'),base,str(order_id)],check=True,env=env)
            except Exception:
                log.seek(0);print(log.read());raise
            finally:
                process.terminate()
                try:process.wait(timeout=10)
                except subprocess.TimeoutExpired:process.kill();process.wait()

if __name__=='__main__':main()
