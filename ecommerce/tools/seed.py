#!/usr/bin/env python3
"""Seed a running development backend without duplicating existing demo records."""
import argparse
import http.cookiejar
import json
import urllib.request

class Client:
    def __init__(self, base, user):
        self.base=base.rstrip('/')
        self.http=urllib.request.build_opener(urllib.request.HTTPCookieProcessor(http.cookiejar.CookieJar()))
        self.call('POST','/api/session',{'user':user})
    def call(self, method, path, value=None):
        request=urllib.request.Request(self.base+path,data=None if value is None else json.dumps(value).encode(),method=method,headers={'Content-Type':'application/json'})
        with self.http.open(request,timeout=30) as response:return json.load(response)
    def rows(self, feature, entity):return self.call('GET',f'/api/data/{feature}/{entity}?size=100')['items']
    def create(self, feature, entity, value):return self.call('POST',f'/api/data/{feature}/{entity}',value)

VAT_RULES=[
    ('CH','Switzerland',True,{'Standard':'8.10','Reduced':'2.60','Special':'3.80'}),
    ('DE','Germany',True,{'Standard':'19.00','Reduced':'7.00','Special':'7.00'}),
    ('AT','Austria',True,{'Standard':'20.00','Reduced':'10.00','Special':'13.00'}),
    ('US','United States (sample rates)',False,{'Standard':'8.00','Reduced':'2.00','Special':'2.00'}),
]

def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--url',default='http://localhost:5180')
    args=parser.parse_args()
    admin=Client(args.url,'admin.dev')
    # VAT rules: CH, DE and AT list prices include VAT; the US rates are illustrative sample data, added on top.
    countries={}
    for code,name,included,rates in VAT_RULES:
        country=next((c for c in admin.rows('tax','Country') if c['code']==code),None) or admin.create('tax','Country',{'code':code,'name':name,'pricesIncludeVAT':included})
        countries[code]=country['id']
        existing={r['rateKey'] for r in admin.rows('tax','VatRate')}
        for tax_class,rate in rates.items():
            key=f'{code}:{tax_class}'
            if key not in existing:admin.create('tax','VatRate',{'rateKey':key,'country':country['id'],'taxClass':tax_class,'rate':rate})
    categories={}
    for name,tax_class in [('Demo equipment','Standard'),('Books','Reduced'),('Electronics','Standard'),('Food','Reduced')]:
        category=next((c for c in admin.rows('catalogue','Category') if c['name']==name),None) or admin.create('catalogue','Category',{'name':name,'taxClass':tax_class})
        categories[name]=category['id']
    products=[]
    for sku,name,price,category in [
        ('DEMO-MUG','Karu mug','12.35','Demo equipment'),
        ('DEMO-BAG','Karu bag','24.90','Demo equipment'),
        ('DEMO-BOOK','Karu handbook','30.00','Books'),
        # CHF prices include Swiss VAT: these examples have simple net amounts.
        ('CH-HEADPHONES','Wireless headphones','108.10','Electronics'),
        ('CH-KEYBOARD','USB keyboard','54.05','Electronics'),
        ('CH-COFFEE','Roasted coffee beans 250 g','10.26','Food'),
        ('CH-CHOCOLATE','Swiss milk chocolate 100 g','5.13','Food'),
        ('CH-BOOK','Swiss hiking guide','20.52','Books'),
    ]:
        product=next((p for p in admin.rows('catalogue','Product') if p['sku']==sku),None)
        if product is None:
            product=admin.create('catalogue','Product',{'sku':sku,'name':name,'category':categories[category],'price':price})
            stock=next(s for s in admin.rows('catalogue','Stock') if s['product']['id']==product['id'])
            admin.call('POST',f"/api/command/catalogue/AdjustStock/{stock['id']}",{'quantity':20})
            admin.call('POST',f"/api/command/catalogue/PublishProduct/{product['id']}",{})
        products.append({'id':product['id'],'sku':sku})
    profiles=[]
    for user,name,address in [('alice.dev','Alice',{'line1':'Main street 1','city':'Zurich','postalCode':'8000','country':'CH'}),
                              ('bob.dev','Bob',{'line1':'1 Main Street','city':'Boston','postalCode':'02108','country':'US'})]:
        customer=Client(args.url,user)
        existing=customer.rows('customers','Customer')
        profile=existing[0] if existing else customer.create('customers','Customer',{'name':name,'email':name.lower()+'@example.test','shipping':address})
        profiles.append({'user':user,'customerId':profile['id'],'country':profile['shipping']['country']})
    print(json.dumps({'countries':countries,'products':products,'customers':profiles},indent=2))

if __name__=='__main__':main()
