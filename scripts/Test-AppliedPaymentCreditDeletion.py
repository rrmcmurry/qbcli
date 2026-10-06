"""Delete only confirmed synthetic fixture payments/credits in disposable Test.QBW."""
import argparse
import datetime
import json
import pathlib
import subprocess
import sys
ROOT=pathlib.Path(__file__).resolve().parent.parent;DEST=ROOT/'artifacts/fixtures'
COMPANY=(ROOT/'QBData/Test.QBW').resolve();EXE=ROOT/'artifacts/win-x64/qbcli.exe'
assert COMPANY==pathlib.Path(__import__("os").environ["QBCLI_LIVE_TEST_COMPANY"]).resolve(strict=True)
parser=argparse.ArgumentParser(description=__doc__);parser.add_argument('source');parser.add_argument('--continue-after-payments',help='Reuse confirmed payment deletions; refuses any credit-delete plan/result.');options=parser.parse_args()
SOURCE=options.source;assert len(SOURCE)==6 and SOURCE.isdigit()
STAMP=datetime.datetime.now().strftime('%H%M%S');PREFIX=f'lifecycle-{STAMP}-applied-deletion'
if options.continue_after_payments:
    STAMP=options.continue_after_payments;assert len(STAMP)==6 and STAMP.isdigit();PREFIX=f'lifecycle-{STAMP}-applied-deletion'
    assert not list(DEST.glob(PREFIX+'-delete-credits-*.json')),'Credit deletion may have been attempted; never replay'
def scalar(n,k):return next((c.get('value') for c in n['children'] if c['name']==k),None)
def source(label):
    v=json.loads((DEST/f'lifecycle-{SOURCE}-payment-reallocation-{label}-result.json').read_text(encoding='utf-8'))
    assert v['success'] and pathlib.Path(v['companyFile'])==COMPANY
    assert all(r['statusCode']==0 and r['outcome']=='succeeded' for r in v['results']);return v
party={r['requestId']:scalar(r['data'][0],'ListID') for r in source('base')['results']}
ids={r['requestId']:scalar(r['data'][0],'TxnID') for r in source('seed')['results']}
extra={r['requestId']:scalar(r['data'][0],'TxnID') for r in source('extra-seed')['results']}
payments={r['requestId']:scalar(r['data'][0],'TxnID') for r in source('apply')['results']}
source('extra-apply')
manifest={'sourceFixture':SOURCE,'companyFile':str(COMPANY),'documentIds':ids,'extraCreditIds':extra,'paymentIds':payments,'partyIds':party}
path=DEST/(PREFIX+'-manifest.json')
if options.continue_after_payments:assert json.loads(path.read_text(encoding='utf-8'))==manifest
else:assert not path.exists();path.write_text(json.dumps(manifest,indent=2),encoding='utf-8')
def call(op,fields,key):return {'requestId':key,'operation':op,'fields':fields}
deleted={}
def run(label,args):
    p=DEST/(PREFIX+'-'+label+'-result.json');assert not p.exists()
    x=subprocess.run([str(EXE),*args,'--company-file',str(COMPANY),'--json'],capture_output=True,text=True,encoding='utf-8');p.write_text(x.stdout,encoding='utf-8')
    v=json.loads(x.stdout);print(json.dumps({'fixture':STAMP,'phase':label,'success':v.get('success'),'outcomes':[{k:r.get(k) for k in ('operation','statusCode','outcome')} for r in v.get('results',[]) if 'operation' in r]}),flush=True)
    warning_ok=label.endswith('-sdk') and deleted and all(r['statusCode']==0 and r['outcome']=='succeeded' or r['requestId'] in deleted and r['statusCode']==500 and r['outcome']=='warning' and not r['data'] and ('"'+deleted[r['requestId']]+'"') in r['statusMessage'] and 'could not be found' in r['statusMessage'] for r in v.get('results',[]))
    assert (x.returncode==0 and v['success'] or x.returncode==4 and not v['success'] and warning_ok) and pathlib.Path(v['companyFile'])==COMPANY,'Inspect retained result/journal; never replay'
    return v
def batch(label,requests,apply=False):
    p=DEST/(PREFIX+'-'+label+'-plan.json');assert not p.exists();p.write_text(json.dumps({'protocolVersion':'13.0','country':'US','requests':requests}),encoding='utf-8')
    return run(label,['sdk','batch','--input',str(p),*(['--apply'] if apply else [])])
queries=[call(kind+'Query',{'TxnID':payments[key],'IncludeLineItems':True},key) for key,kind in [('receive','ReceivePayment'),('pay-bills','BillPaymentCheck')]]
queries += [call(kind+'Query',{'TxnID':(ids|extra)[key],'IncludeLineItems':True,'IncludeLinkedTxns':True},key) for key,kind in [('invoice-old','Invoice'),('invoice-new','Invoice'),('customer-credit','CreditMemo'),('bill-old','Bill'),('bill-new','Bill'),('vendor-credit','VendorCredit'),('extra-customer-credit','CreditMemo'),('extra-vendor-credit','VendorCredit')]]
queries += [call('BillToPayQuery',{'PayeeEntityRef':{'ListID':party['vendor']}},'payable')]
def snapshot(stage):
    v=batch(stage+'-sdk',queries)
    for command in ('invoices','bills'):
        for open_only in (False,True):run(stage+'-'+command+('-open' if open_only else '-all'),[command,'list','--page-size','25',*(['--open'] if open_only else [])])
    return {r['requestId']:r['data'] for r in v['results']}
if options.continue_after_payments:
    value=json.loads((DEST/(PREFIX+'-delete-payments-result.json')).read_text(encoding='utf-8'))
    assert value['success'] and pathlib.Path(value['companyFile'])==COMPANY and len(value['results'])==2
    assert all(r['statusCode']==0 and r['outcome']=='succeeded' and r['operation']=='TxnDel' for r in value['results'])
    original=json.loads((DEST/(PREFIX+'-delete-payments-plan.json')).read_text(encoding='utf-8'))
    assert original['requests']==[call('TxnDel',{'TxnDelType':kind,'TxnID':payments[key]},key) for key,kind in [('receive','ReceivePayment'),('pay-bills','BillPaymentCheck')]]
else:
    before=snapshot('before')
    for key in payments:assert len(before[key])==1 and scalar(before[key][0],'TxnID')==payments[key]
    for key in ('invoice-old','invoice-new'):assert scalar(before[key][0],'BalanceRemaining')==('70.00' if key.endswith('old') else '60.00')
    for key in ('customer-credit','extra-customer-credit'):assert scalar(before[key][0],'CreditRemaining')=='0.00'
    # Selected IDs come exclusively from this fixture's successful creation results.
    batch('delete-payments',[call('TxnDel',{'TxnDelType':kind,'TxnID':payments[key]},key) for key,kind in [('receive','ReceivePayment'),('pay-bills','BillPaymentCheck')]],True)
deleted.update(payments)
after_payments=snapshot('after-payments-confirmed' if options.continue_after_payments else 'after-payments');assert all(not after_payments[k] for k in payments)
credit_keys=[('customer-credit','CreditMemo'),('extra-customer-credit','CreditMemo'),('vendor-credit','VendorCredit'),('extra-vendor-credit','VendorCredit')]
for key,_ in credit_keys:assert len(after_payments[key])==1
batch('delete-credits',[call('TxnDel',{'TxnDelType':kind,'TxnID':(ids|extra)[key]},key) for key,kind in credit_keys],True)
deleted.update({key:(ids|extra)[key] for key,_ in credit_keys})
after_credits=snapshot('after-credits');assert all(not after_credits[k] for k,_ in credit_keys)
print(json.dumps({'fixtureStamp':STAMP,'sourceFixture':SOURCE,'scope':'Independent deletion verifier required; do not infer general payment/credit unlink behavior from TxnDel status alone.'}),flush=True)
