"""Fresh applied check/card payments and credits voided only in Test.QBW."""
import datetime
import json
import pathlib
import subprocess
ROOT=pathlib.Path(__file__).resolve().parent.parent;DEST=ROOT/'artifacts/fixtures'
COMPANY=(ROOT/'QBData/Test.QBW').resolve();EXE=ROOT/'artifacts/win-x64/qbcli.exe'
assert COMPANY==pathlib.Path(__import__("os").environ["QBCLI_LIVE_TEST_COMPANY"]).resolve(strict=True)
STAMP=datetime.datetime.now().strftime('%H%M%S');PREFIX=f'lifecycle-{STAMP}-applied-void';NAME='VOID'+STAMP
def scalar(n,k):return next((c.get('value') for c in n['children'] if c['name']==k),None)
def ref(name):return {'FullName':'SDK154955 '+name}
def call(op,fields,key):return {'requestId':key,'operation':op,'fields':fields}
def run(label,args):
    path=DEST/(PREFIX+'-'+label+'-result.json');assert not path.exists()
    x=subprocess.run([str(EXE),*args,'--company-file',str(COMPANY),'--json'],capture_output=True,text=True,encoding='utf-8');path.write_text(x.stdout,encoding='utf-8');v=json.loads(x.stdout)
    print(json.dumps({'fixture':STAMP,'phase':label,'success':v.get('success'),'outcomes':[{k:r.get(k) for k in ('operation','statusCode','outcome')} for r in v.get('results',[]) if 'operation' in r]}),flush=True)
    assert x.returncode==0 and v['success'] and pathlib.Path(v['companyFile'])==COMPANY,'Inspect journal/result; never replay'
    return v
def batch(label,requests,apply=False):
    path=DEST/(PREFIX+'-'+label+'-plan.json');assert not path.exists();path.write_text(json.dumps({'protocolVersion':'13.0','country':'US','requests':requests}),encoding='utf-8')
    return run(label,['sdk','batch','--input',str(path),*(['--apply'] if apply else [])])
base=batch('base',[call('VendorAdd',{'VendorAdd':{'Name':NAME+' Vendor'}},'vendor'),call('ClassAdd',{'ClassAdd':{'Name':NAME+' Discounts'}},'class')],True)
party={r['requestId']:scalar(r['data'][0],'ListID') for r in base['results']}
seed=batch('seed',[call(kind+'Add',{kind+'Add':{'VendorRef':{'ListID':party['vendor']},'APAccountRef':ref('AP'),'TxnDate':'2026-10-05','Memo':NAME+' '+key,'ExpenseLineAdd':{'AccountRef':ref('Expense'),'Amount':amount}}},key) for key,kind,amount in [('check-bill','Bill','100.00'),('card-bill','Bill','100.00'),('check-credit','VendorCredit','20.00'),('card-credit','VendorCredit','20.00')]],True)
ids={r['requestId']:scalar(r['data'][0],'TxnID') for r in seed['results']}
requests=[]
for key,kind in [('check','BillPaymentCheck'),('card','BillPaymentCreditCard')]:
    body={'PayeeEntityRef':{'ListID':party['vendor']},'APAccountRef':ref('AP'),'TxnDate':'2026-10-05','AppliedToTxnAdd':{'TxnID':ids[key+'-bill'],'PaymentAmount':'30.00','DiscountAmount':'5.00','DiscountAccountRef':ref('Income'),'DiscountClassRef':{'ListID':party['class']},'SetCredit':{'CreditTxnID':ids[key+'-credit'],'AppliedAmount':'20.00'}}}
    if key=='check':body.update(BankAccountRef=ref('Bank'),IsToBePrinted=False)
    else:body.update(CreditCardAccountRef=ref('Card'))
    requests.append(call(kind+'Add',{kind+'Add':body},key))
applied=batch('apply',requests,True);payments={r['requestId']:scalar(r['data'][0],'TxnID') for r in applied['results']}
queries=[call(kind+'Query',{'TxnID':payments[key],'IncludeLineItems':True},key) for key,kind in [('check','BillPaymentCheck'),('card','BillPaymentCreditCard')]]
queries += [call(kind+'Query',{'TxnID':ids[key],'IncludeLineItems':True,'IncludeLinkedTxns':True},key) for key,kind in [('check-bill','Bill'),('card-bill','Bill'),('check-credit','VendorCredit'),('card-credit','VendorCredit')]]
queries += [call('BillToPayQuery',{'PayeeEntityRef':{'ListID':party['vendor']}},'payable')]
def snapshot(stage):
    v=batch(stage+'-sdk',queries)
    for open_only in (False,True):run(stage+'-bills'+('-open' if open_only else '-all'),['bills','list','--page-size','25',*(['--open'] if open_only else [])])
    return {r['requestId']:r['data'] for r in v['results']}
before=snapshot('before')
for key in payments:assert scalar(before[key][0],'Amount')=='30.00'
batch('void-payments',[call('TxnVoid',{'TxnVoidType':kind,'TxnID':payments[key]},key) for key,kind in [('check','BillPaymentCheck'),('card','BillPaymentCreditCard')]],True)
snapshot('after-payments')
batch('void-credits',[call('TxnVoid',{'TxnVoidType':'VendorCredit','TxnID':ids[key+'-credit']},key+'-credit') for key in ('check','card')],True)
snapshot('after-credits')
print(json.dumps({'fixtureStamp':STAMP,'scope':'Independent verifier required; voided transaction identities should remain queryable.'}),flush=True)
