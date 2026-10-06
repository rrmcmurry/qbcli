"""Explicit credits/discounts and multi-bill application on disposable Test.QBW."""
import datetime
import json
import pathlib
import subprocess
from decimal import Decimal
ROOT=pathlib.Path(__file__).resolve().parent.parent;DEST=ROOT/'artifacts/fixtures'
EXE=ROOT/'artifacts/win-x64/qbcli.exe';COMPANY=(ROOT/'QBData/Test.QBW').resolve()
assert COMPANY==pathlib.Path(__import__("os").environ["QBCLI_LIVE_TEST_COMPANY"]).resolve(strict=True)
STAMP=datetime.datetime.now().strftime('%H%M%S');PREFIX='CREDIT'+STAMP
def scalar(n,k):return next((c.get('value') for c in n['children'] if c['name']==k),None)
def ref(name):return {'FullName':'SDK154955 '+name}
def call(op,body,key):return {'requestId':key,'operation':op,'fields':{op:body}}
def run(label,args):
    proc=subprocess.run([str(EXE),*args,'--company-file',str(COMPANY),'--json'],capture_output=True,text=True,encoding='utf-8')
    (DEST/f'lifecycle-{STAMP}-credit-discounts-{label}-result.json').write_text(proc.stdout,encoding='utf-8')
    result=json.loads(proc.stdout,parse_float=Decimal)
    print(json.dumps({'phase':label,'success':result.get('success'),'outcomes':[{k:r.get(k) for k in ('operation','outcome','statusCode')} for r in result.get('results',[]) if 'operation' in r]}),flush=True)
    assert proc.returncode==0 and result['success'] and pathlib.Path(result['companyFile'])==COMPANY,'Inspect journals/evidence; never replay'
    return result
def batch(label,calls,apply=False):
    path=DEST/f'lifecycle-{STAMP}-credit-discounts-{label}-plan.json'
    path.write_text(json.dumps({'protocolVersion':'13.0','country':'US','requests':calls}),encoding='utf-8')
    return run(label,['sdk','batch','--input',str(path),*(['--apply'] if apply else [])])
base=batch('base',[call('CustomerAdd',{'Name':PREFIX+' Customer'},'customer'),call('VendorAdd',{'Name':PREFIX+' Vendor'},'vendor'),call('ClassAdd',{'Name':PREFIX+' Discounts'},'class')],True)
parties={r['requestId']:scalar(r['data'][0],'ListID') for r in base['results']}
calls=[]
for key,kind,amount in [('invoice','Invoice','100.00'),('customer-credit','CreditMemo','20.00'),('bill-a','Bill','100.00'),('bill-b','Bill','80.00'),('vendor-credit','VendorCredit','20.00')]:
    body={'TxnDate':'2026-10-05','Memo':PREFIX+' '+key}
    if kind in ('Invoice','CreditMemo'):body.update(CustomerRef={'ListID':parties['customer']},ARAccountRef=ref('AR'),**{kind+'LineAdd':{'ItemRef':ref('Service'),'Quantity':'1','Rate':amount}})
    else:body.update(VendorRef={'ListID':parties['vendor']},APAccountRef=ref('AP'),ExpenseLineAdd={'AccountRef':ref('Expense'),'Amount':amount})
    calls.append(call(kind+'Add',body,key))
seed=batch('seed',calls,True);ids={r['requestId']:scalar(r['data'][0],'TxnID') for r in seed['results']}
def application(key,credit,amount):return {'TxnID':ids[key],'PaymentAmount':'25.00','SetCredit':{'CreditTxnID':ids[credit],'AppliedAmount':amount},'DiscountAmount':'5.00','DiscountAccountRef':ref('Expense' if key=='invoice' else 'Income'),'DiscountClassRef':{'ListID':parties['class']}}
applied=batch('apply',[
    call('ReceivePaymentAdd',{'CustomerRef':{'ListID':parties['customer']},'ARAccountRef':ref('AR'),'DepositToAccountRef':ref('Bank'),'TxnDate':'2026-10-05','TotalAmount':'25.00','AppliedToTxnAdd':application('invoice','customer-credit','20.00')},'receive'),
    call('BillPaymentCheckAdd',{'PayeeEntityRef':{'ListID':parties['vendor']},'APAccountRef':ref('AP'),'BankAccountRef':ref('Bank'),'TxnDate':'2026-10-05','IsToBePrinted':False,'AppliedToTxnAdd':[application('bill-a','vendor-credit','10.00'),application('bill-b','vendor-credit','10.00')]},'pay-bills')],True)
def snapshot(stage):
    queries=[{'requestId':key,'operation':kind+'Query','fields':{'TxnID':ids[key],'IncludeLinkedTxns':True}} for key,kind in [('invoice','Invoice'),('customer-credit','CreditMemo'),('bill-a','Bill'),('bill-b','Bill'),('vendor-credit','VendorCredit')]]
    queries.append({'requestId':'payable','operation':'BillToPayQuery','fields':{'PayeeEntityRef':{'ListID':parties['vendor']}}})
    read=batch(stage+'-sdk',queries);rows={r['requestId']:r['data'] for r in read['results']}
    balances={scalar(n['children'][0],'TxnID'):Decimal(scalar(n['children'][0],'AmountDue')) for n in rows['payable'] if n['children'][0]['name']=='BillToPay'}
    assert Decimal(scalar(rows['invoice'][0],'BalanceRemaining'))==(Decimal(50) if stage=='partial' else 0)
    for key,amount in [('bill-a',60),('bill-b',40)]:
        assert balances.get(ids[key],Decimal(0))==(Decimal(amount) if stage=='partial' else 0)
        assert scalar(rows[key][0],'IsPaid')==('false' if stage=='partial' else 'true')
    assert Decimal(scalar(rows['customer-credit'][0],'CreditRemaining'))==0
    assert not any(n['children'][0]['name']=='CreditToApply' and scalar(n['children'][0],'TxnID')==ids['vendor-credit'] for n in rows['payable'])
    for kind,command,keys in [('Invoice','invoices',['invoice']),('Bill','bills',['bill-a','bill-b'])]:
        for open_only in (False,True):
            normalized=run(stage+'-'+command+('-open' if open_only else '-all'),[command,'list','--page-size','25',*(['--open'] if open_only else [])]);assert normalized['complete']
            selected={r['id']:r for r in normalized['results'] if r['id'] in [ids[k] for k in keys]}
            assert set(selected)==(set() if open_only and stage=='paid' else {ids[k] for k in keys})
            for key in keys:
                if ids[key] in selected:
                    assert selected[ids[key]]['remainingAmount']==(Decimal(scalar(rows[key][0],'BalanceRemaining')) if kind=='Invoice' else balances.get(ids[key],Decimal(0)))
snapshot('partial')
finished=batch('finish',[
    call('ReceivePaymentAdd',{'CustomerRef':{'ListID':parties['customer']},'ARAccountRef':ref('AR'),'DepositToAccountRef':ref('Bank'),'TxnDate':'2026-10-05','TotalAmount':'50.00','AppliedToTxnAdd':{'TxnID':ids['invoice'],'PaymentAmount':'50.00'}},'receive'),
    call('BillPaymentCheckAdd',{'PayeeEntityRef':{'ListID':parties['vendor']},'APAccountRef':ref('AP'),'BankAccountRef':ref('Bank'),'TxnDate':'2026-10-05','IsToBePrinted':False,'AppliedToTxnAdd':[{'TxnID':ids['bill-a'],'PaymentAmount':'60.00'},{'TxnID':ids['bill-b'],'PaymentAmount':'40.00'}]},'pay-bills')],True)
snapshot('paid')
payment_queries=[]
for label,result in [('apply',applied),('finish',finished)]:
    for row in result['results']:
        kind='ReceivePayment' if row['requestId']=='receive' else 'BillPaymentCheck'
        payment_queries.append({'requestId':label+'-'+row['requestId'],'operation':kind+'Query','fields':{'TxnID':scalar(row['data'][0],'TxnID'),'IncludeLineItems':True}})
batch('payment-readback',payment_queries)
print(json.dumps({'fixtureStamp':STAMP,'documents':3,'creditDefinitions':2,'creditApplications':3,'discountApplications':3,'scope':'Invoke independent retained-evidence verifier; no automatic retry or applied-credit modification.'}),flush=True)
