"""Five-decimal price/quantity/percentage acceptance on disposable Test.QBW."""
from decimal import Decimal
import datetime
import hashlib
import json
import pathlib
import subprocess
ROOT=pathlib.Path(__file__).resolve().parent.parent;DEST=ROOT/'artifacts/fixtures';EXE=ROOT/'artifacts/win-x64/qbcli.exe'
COMPANY=(ROOT/'QBData/Test.QBW').resolve();assert COMPANY==pathlib.Path(__import__("os").environ["QBCLI_LIVE_TEST_COMPANY"]).resolve(strict=True)
STAMP=datetime.datetime.now().strftime('%H%M%S');PREFIX='PRECISION'+STAMP
def scalar(n,k):return next((c.get('value') for c in n['children'] if c['name']==k),None)
def execute(label,calls,apply=False):
    path=DEST/f'lifecycle-{STAMP}-numeric-{label}-plan.json'
    path.write_text(json.dumps({'protocolVersion':'13.0','country':'US','requests':calls}),encoding='utf-8')
    p=subprocess.run([str(EXE),'sdk','batch','--input',str(path),'--company-file',str(COMPANY),'--json',*(['--apply'] if apply else [])],capture_output=True,text=True,encoding='utf-8')
    path.with_name(path.name.replace('-plan','-result')).write_text(p.stdout,encoding='utf-8');v=json.loads(p.stdout)
    print(json.dumps({'phase':label,'success':v.get('success'),'outcomes':[{k:r.get(k) for k in ['operation','outcome','statusCode']} for r in v.get('results',[])]}),flush=True)
    assert p.returncode==0 and v['success'],'Inspect evidence; do not replay'
    return v
def call(op,fields,key):return {'requestId':key,'operation':op,'fields':fields}
items=execute('items',[call('ItemServiceAdd',{'ItemServiceAdd':{'Name':PREFIX+' Service','SalesOrPurchase':{'Price':'1.12345','AccountRef':{'FullName':'SDK154955 Income'}}}},'service'),
    call('ItemDiscountAdd',{'ItemDiscountAdd':{'Name':PREFIX+' Discount','DiscountRatePercent':'12.12345','AccountRef':{'FullName':'SDK154955 Income'}}},'discount')],True)
ids={r['requestId']:scalar(r['data'][0],'ListID') for r in items['results']}
invoice=execute('invoice',[call('InvoiceAdd',{'InvoiceAdd':{'CustomerRef':{'FullName':'SDK154955 Customer'},'ARAccountRef':{'FullName':'SDK154955 AR'},
    'TxnDate':'2026-10-05','Memo':PREFIX,'InvoiceLineAdd':[{'ItemRef':{'ListID':ids['service']},'Quantity':'1.12345','Rate':'1.12345'},
        {'ItemRef':{'ListID':ids['discount']},'RatePercent':'12.12345'}]}},'invoice')],True)
txn=scalar(invoice['results'][0]['data'][0],'TxnID')
fresh=execute('fresh',[call('ItemServiceQuery',{'ListID':ids['service']},'service'),call('ItemDiscountQuery',{'ListID':ids['discount']},'discount'),
    call('InvoiceQuery',{'TxnID':txn,'IncludeLineItems':True,'IncludeLinkedTxns':True},'invoice')])
records={r['requestId']:r['data'][0] for r in fresh['results']}
service=next(n for n in records['service']['children'] if n['name']=='SalesOrPurchase')
assert Decimal(scalar(service,'Price'))==Decimal('1.12345')
assert Decimal(scalar(records['discount'],'DiscountRatePercent'))==Decimal('12.12345')
lines=[n for n in records['invoice']['children'] if n['name']=='InvoiceLineRet'];assert len(lines)==2
assert Decimal(scalar(lines[0],'Quantity'))==Decimal('1.12345') and Decimal(scalar(lines[0],'Rate'))==Decimal('1.12345')
assert Decimal(scalar(lines[1],'RatePercent'))==Decimal('-12.12345')
check={'protocol':'13.0','priceFractionDigitsPreserved':5,'quantityFractionDigitsPreserved':5,'percentFractionDigitsPreserved':5,
    'freshItemAndInvoiceReadsVerified':True,'discountInvoicePercentReturnedNegative':True,
    'scope':'Test.QBW QBD2024R21 US protocol13: five-decimal service price/invoice rate and quantity, discount definition/line percentage magnitude. Positive discount definition/input returned negative invoice RatePercent; fractional digits preserved. No extreme integer sizes, older host/protocol, FLOATTYPE exchange-rate or universal amount-format proof.',
    'referenceHashes':{kind:hashlib.sha256((ROOT/f'artifacts/sdk-source/{kind}.html').read_bytes()).hexdigest() for kind in ['PRICETYPE','QUANTYPE','PERCENTTYPE','FLOATTYPE','AMTTYPE']}}
(DEST/f'lifecycle-{STAMP}-numeric-precision-verification.json').write_text(json.dumps(check,indent=2),encoding='utf-8')
print(json.dumps(check),flush=True)
