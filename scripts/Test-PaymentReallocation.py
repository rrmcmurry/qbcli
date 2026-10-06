"""Fresh synthetic payment reallocation; exact disposable file guard, no replay."""
import argparse
import datetime
import json
import pathlib
import subprocess
from decimal import Decimal
ROOT=pathlib.Path(__file__).resolve().parent.parent;DEST=ROOT/'artifacts/fixtures'
COMPANY=(ROOT/'QBData/Test.QBW').resolve();EXE=ROOT/'artifacts/win-x64/qbcli.exe'
assert COMPANY==pathlib.Path(__import__("os").environ["QBCLI_LIVE_TEST_COMPANY"]).resolve(strict=True)
STAMP=datetime.datetime.now().strftime('%H%M%S');PREFIX=f'lifecycle-{STAMP}-payment-reallocation';NAME='REALLOC'+STAMP
parser=argparse.ArgumentParser(description=__doc__);parser.add_argument('--additional-credits',action='store_true',help='Continue the fresh fixture with two new10 credits through payment modifiers.');options=parser.parse_args()
def scalar(n,k):return next((c.get('value') for c in n['children'] if c['name']==k),None)
def ref(name):return {'FullName':'SDK154955 '+name}
def call(op,fields,key):return {'requestId':key,'operation':op,'fields':fields}
def run(label,args):
    result=DEST/(PREFIX+'-'+label+'-result.json');assert not result.exists()
    x=subprocess.run([str(EXE),*args,'--company-file',str(COMPANY),'--json'],capture_output=True,text=True,encoding='utf-8')
    result.write_text(x.stdout,encoding='utf-8');v=json.loads(x.stdout,parse_float=Decimal)
    print(json.dumps({'fixture':STAMP,'phase':label,'success':v.get('success'),'outcomes':[{k:r.get(k) for k in ('operation','statusCode','outcome')} for r in v.get('results',[]) if 'operation' in r]}),flush=True)
    assert x.returncode==0 and v['success'] and pathlib.Path(v['companyFile'])==COMPANY,'Inspect saved results/journals; never replay'
    return v
def batch(label,requests,apply=False):
    path=DEST/(PREFIX+'-'+label+'-plan.json');assert not path.exists()
    path.write_text(json.dumps({'protocolVersion':'13.0','country':'US','requests':requests}),encoding='utf-8')
    return run(label,['sdk','batch','--input',str(path),*(['--apply'] if apply else [])])
base=batch('base',[call(kind+'Add',{kind+'Add':{'Name':NAME+' '+key}},key) for kind,key in [('Customer','customer'),('Vendor','vendor'),('Class','class')]],True)
party={r['requestId']:scalar(r['data'][0],'ListID') for r in base['results']};requests=[]
documents=[('invoice-old','Invoice',100),('invoice-new','Invoice',100),('customer-credit','CreditMemo',20),('bill-old','Bill',100),('bill-new','Bill',100),('vendor-credit','VendorCredit',20)]
for key,kind,amount in documents:
    body={'TxnDate':'2026-10-05','Memo':NAME+' '+key}
    if kind in ('Invoice','CreditMemo'):body.update(CustomerRef={'ListID':party['customer']},ARAccountRef=ref('AR'),**{kind+'LineAdd':{'ItemRef':ref('Service'),'Quantity':'1','Rate':str(amount)+'.00'}})
    else:body.update(VendorRef={'ListID':party['vendor']},APAccountRef=ref('AP'),ExpenseLineAdd={'AccountRef':ref('Expense'),'Amount':str(amount)+'.00'})
    requests.append(call(kind+'Add',{kind+'Add':body},key))
seed=batch('seed',requests,True);ids={r['requestId']:scalar(r['data'][0],'TxnID') for r in seed['results']}
def application(key,discount,credit=None):
    body={'TxnID':ids[key],'PaymentAmount':'30.00','DiscountAmount':discount,'DiscountAccountRef':ref('Expense' if key.startswith('invoice') else 'Income'),'DiscountClassRef':{'ListID':party['class']}}
    if credit:body['SetCredit']={'CreditTxnID':ids[credit],'AppliedAmount':'20.00'}
    return body
applied=batch('apply',[
    call('ReceivePaymentAdd',{'ReceivePaymentAdd':{'CustomerRef':{'ListID':party['customer']},'ARAccountRef':ref('AR'),'DepositToAccountRef':ref('Bank'),'TxnDate':'2026-10-05','TotalAmount':'30.00','AppliedToTxnAdd':application('invoice-old','5.00','customer-credit')}},'receive'),
    call('BillPaymentCheckAdd',{'BillPaymentCheckAdd':{'PayeeEntityRef':{'ListID':party['vendor']},'APAccountRef':ref('AP'),'BankAccountRef':ref('Bank'),'TxnDate':'2026-10-05','IsToBePrinted':False,'AppliedToTxnAdd':application('bill-old','5.00','vendor-credit')}},'pay-bills')],True)
payments={r['requestId']:scalar(r['data'][0],'TxnID') for r in applied['results']}
queries=[call(kind+'Query',{'TxnID':payments[key],'IncludeLineItems':True},key) for key,kind in [('receive','ReceivePayment'),('pay-bills','BillPaymentCheck')]]
queries += [call(kind+'Query',{'TxnID':ids[key],'IncludeLineItems':True,'IncludeLinkedTxns':True},key) for key,kind,_ in documents]
queries += [call('BillToPayQuery',{'PayeeEntityRef':{'ListID':party['vendor']}},'payable')]
def snapshot(stage):
    v=batch(stage+'-sdk',queries);rows={r['requestId']:r['data'] for r in v['results']}
    balances={scalar(n['children'][0],'TxnID'):Decimal(scalar(n['children'][0],'AmountDue')) for n in rows['payable'] if n['children'][0]['name']=='BillToPay'}
    for key,kind,_ in documents:
        if kind not in ('Invoice','Bill'):continue
        expected=(45 if key.endswith('old') else 100) if stage=='before' else ((70 if stage=='extra-after' else 80) if key.endswith('old') else 60)
        actual=Decimal(scalar(rows[key][0],'BalanceRemaining')) if kind=='Invoice' else balances[ids[key]]
        assert actual==expected and scalar(rows[key][0],'IsPaid')=='false'
    assert Decimal(scalar(rows['customer-credit'][0],'CreditRemaining'))==0
    for command,keys in [('invoices',['invoice-old','invoice-new']),('bills',['bill-old','bill-new'])]:
        for open_only in (False,True):
            normalized=run(stage+'-'+command+('-open' if open_only else '-all'),[command,'list','--page-size','25',*(['--open'] if open_only else [])]);assert normalized['complete']
            for key in keys:
                found=[r for r in normalized['results'] if r['id']==ids[key]];assert len(found)==1
                assert found[0]['remainingAmount']==((45 if key.endswith('old') else 100) if stage=='before' else ((70 if stage=='extra-after' else 80) if key.endswith('old') else 60))
    return rows
before=snapshot('before')
batch('reallocate',[
    call(kind+'Mod',{kind+'Mod':{'TxnID':payments[key],'EditSequence':scalar(before[key][0],'EditSequence'),'AppliedToTxnMod':application(document,'10.00')}},key)
    for key,kind,document in [('receive','ReceivePayment','invoice-new'),('pay-bills','BillPaymentCheck','bill-new')]],True)
snapshot('after')
if options.additional_credits:
    extra=batch('extra-seed',[
        call('CreditMemoAdd',{'CreditMemoAdd':{'CustomerRef':{'ListID':party['customer']},'ARAccountRef':ref('AR'),'TxnDate':'2026-10-05','Memo':NAME+' extra customer credit','CreditMemoLineAdd':{'ItemRef':ref('Service'),'Quantity':'1','Rate':'10.00'}}},'extra-customer-credit'),
        call('VendorCreditAdd',{'VendorCreditAdd':{'VendorRef':{'ListID':party['vendor']},'APAccountRef':ref('AP'),'TxnDate':'2026-10-05','Memo':NAME+' extra vendor credit','ExpenseLineAdd':{'AccountRef':ref('Expense'),'Amount':'10.00'}}},'extra-vendor-credit')],True)
    extra_ids={r['requestId']:scalar(r['data'][0],'TxnID') for r in extra['results']}
    queries += [call(kind+'Query',{'TxnID':extra_ids[key],'IncludeLineItems':True,'IncludeLinkedTxns':True},key) for key,kind in [('extra-customer-credit','CreditMemo'),('extra-vendor-credit','VendorCredit')]]
    extra_before=snapshot('extra-before')
    assert Decimal(scalar(extra_before['extra-customer-credit'][0],'CreditRemaining'))==10
    requests=[]
    for key,kind,new,old,credit in [('receive','ReceivePayment','invoice-new','invoice-old','extra-customer-credit'),('pay-bills','BillPaymentCheck','bill-new','bill-old','extra-vendor-credit')]:
        body={'TxnID':payments[key],'EditSequence':scalar(extra_before[key][0],'EditSequence'),'AppliedToTxnMod':[application(new,'10.00'),{'TxnID':ids[old],'SetCredit':{'CreditTxnID':extra_ids[credit],'AppliedAmount':'10.00'}}]}
        requests.append(call(kind+'Mod',{kind+'Mod':body},key))
    batch('extra-apply',requests,True)
    extra_after=snapshot('extra-after')
    assert Decimal(scalar(extra_after['extra-customer-credit'][0],'CreditRemaining'))==0
print(json.dumps({'fixtureStamp':STAMP,'scope':'Run independent verifier; cash/discount moved, existing credits not resubmitted.'}),flush=True)
