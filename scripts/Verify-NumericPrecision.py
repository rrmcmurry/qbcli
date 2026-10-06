"""Verify retained numeric precision evidence without replaying SDK requests."""
from decimal import Decimal
import hashlib
import json
import pathlib
import sys
root=pathlib.Path(__file__).resolve().parent.parent;dest=root/'artifacts/fixtures'
stamp=sys.argv[1];assert len(stamp)==6 and stamp.isdigit()
def load(label,suffix='result'):
    v=json.loads((dest/f'lifecycle-{stamp}-numeric-{label}-{suffix}.json').read_text())
    if suffix=='result':assert v['success'] and all(r['outcome']=='succeeded' for r in v['results'])
    return v
def scalar(n,k):return next((c.get('value') for c in n['children'] if c['name']==k),None)
items=load('items');invoice=load('invoice');fresh=load('fresh');records={r['requestId']:r['data'][0] for r in fresh['results']}
ids={r['requestId']:scalar(r['data'][0],'ListID') for r in items['results']}
assert scalar(records['service'],'ListID')==ids['service'] and scalar(records['discount'],'ListID')==ids['discount']
assert scalar(records['invoice'],'TxnID')==scalar(invoice['results'][0]['data'][0],'TxnID')
service=next(c for c in records['service']['children'] if c['name']=='SalesOrPurchase')
assert Decimal(scalar(service,'Price'))==Decimal('1.12345')
assert Decimal(scalar(records['discount'],'DiscountRatePercent'))==Decimal('12.12345')
lines=[c for c in records['invoice']['children'] if c['name']=='InvoiceLineRet'];assert len(lines)==2
assert Decimal(scalar(lines[0],'Quantity'))==Decimal('1.12345') and Decimal(scalar(lines[0],'Rate'))==Decimal('1.12345')
assert Decimal(scalar(lines[1],'RatePercent'))==Decimal('-12.12345')
original=load('invoice','plan')['requests'][0]['fields']['InvoiceAdd']['InvoiceLineAdd']
assert original[0]['Quantity']==original[0]['Rate']=='1.12345' and original[1]['RatePercent']=='12.12345'
for value in [items,invoice]:
    journal=[json.loads(line) for line in pathlib.Path(value['journalFile']).read_text().splitlines()]
    assert sum(r['kind']=='submitted' for r in journal)==1 and any(r['kind']=='finished' for r in journal)
check={'protocol':'13.0','priceFractionDigitsPreserved':5,'quantityFractionDigitsPreserved':5,'percentFractionDigitsPreserved':5,
    'freshItemAndInvoiceReadsVerified':True,'discountInvoicePercentReturnedNegative':True,'independentRetainedEvidenceVerification':True,
    'scope':'Test.QBW QBD2024R21 US protocol13: five-decimal service price/invoice rate and quantity, discount definition/line percentage magnitude. Positive definition/input returned negative invoice RatePercent; fractional digits preserved. No extreme integer sizes, older host/protocol, FLOATTYPE exchange-rate or universal amount-format proof.',
    'referenceHashes':{kind:hashlib.sha256((root/f'artifacts/sdk-source/{kind}.html').read_bytes()).hexdigest() for kind in ['PRICETYPE','QUANTYPE','PERCENTTYPE','FLOATTYPE','AMTTYPE']}}
(dest/f'lifecycle-{stamp}-numeric-precision-verification.json').write_text(json.dumps(check,indent=2),encoding='utf-8')
print(json.dumps({'fiveDecimalPriceQuantityAndPercentageVerified':True,'discountInvoicePercentReturnedNegative':True}))
