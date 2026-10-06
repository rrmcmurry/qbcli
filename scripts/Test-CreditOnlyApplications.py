"""Credit-only linked mutations via supported SDK, exact disposable-company guard."""
import datetime
import argparse
import json
import pathlib
import subprocess
from decimal import Decimal
ROOT=pathlib.Path(__file__).resolve().parent.parent;DEST=ROOT/'artifacts/fixtures'
EXE=ROOT/'artifacts/win-x64/qbcli.exe';COMPANY=(ROOT/'QBData/Test.QBW').resolve()
assert COMPANY==pathlib.Path(__import__("os").environ["QBCLI_LIVE_TEST_COMPANY"]).resolve(strict=True)
STAMP=datetime.datetime.now().strftime('%H%M%S');PREFIX='CREDITONLY'+STAMP
parser=argparse.ArgumentParser(description=__doc__);modes=parser.add_mutually_exclusive_group();modes.add_argument('--continue-before-apply',help='Reuse a successful saved seed; refuses any existing application plan/result.');modes.add_argument('--verify-recorded-apply',help='Read-only verification of fully successful recorded applications; never resubmits writes.');options=parser.parse_args()
if options.continue_before_apply or options.verify_recorded_apply:
    STAMP=options.continue_before_apply or options.verify_recorded_apply;assert len(STAMP)==6 and STAMP.isdigit();PREFIX='CREDITONLY'+STAMP
if options.continue_before_apply:
    assert not list(DEST.glob(f'lifecycle-{STAMP}-credit-only-apply-*.json')),'Application may have been attempted; inspect journal, never replay'
def scalar(n,k):return next((c.get('value') for c in n['children'] if c['name']==k),None)
def ref(name):return {'FullName':'SDK154955 '+name}
def call(op,fields,key):return {'requestId':key,'operation':op,'fields':fields}
def run(label,args):
    x=subprocess.run([str(EXE),*args,'--company-file',str(COMPANY),'--json'],capture_output=True,text=True,encoding='utf-8')
    (DEST/f'lifecycle-{STAMP}-credit-only-{label}-result.json').write_text(x.stdout,encoding='utf-8')
    v=json.loads(x.stdout,parse_float=Decimal)
    print(json.dumps({'phase':label,'success':v.get('success'),'outcomes':[{k:r.get(k) for k in ('operation','outcome','statusCode')}|{'dataRecords':len(r.get('data',[]))} for r in v.get('results',[]) if 'operation' in r]}),flush=True)
    assert x.returncode==0 and v['success'] and pathlib.Path(v['companyFile'])==COMPANY,'Inspect retained evidence, never replay'
    return v
def batch(label,requests,apply=False):
    p=DEST/f'lifecycle-{STAMP}-credit-only-{label}-plan.json';assert not p.exists()
    p.write_text(json.dumps({'protocolVersion':'13.0','country':'US','requests':requests}),encoding='utf-8')
    return run(label,['sdk','batch','--input',str(p),*(['--apply'] if apply else [])])
def saved(label):
    v=json.loads((DEST/f'lifecycle-{STAMP}-credit-only-{label}-result.json').read_text(encoding='utf-8'));assert v['success'] and pathlib.Path(v['companyFile'])==COMPANY;return v
base=saved('base') if options.continue_before_apply or options.verify_recorded_apply else batch('base',[call('CustomerAdd',{'CustomerAdd':{'Name':PREFIX+' Customer'}},'customer'),call('VendorAdd',{'VendorAdd':{'Name':PREFIX+' Vendor'}},'vendor')],True)
parties={r['requestId']:scalar(r['data'][0],'ListID') for r in base['results']}
requests=[]
for key,kind,amount in [('invoice','Invoice','100.00'),('customer-credit','CreditMemo','40.00'),('bill-check','Bill','100.00'),('bill-card','Bill','100.00'),('check-credit','VendorCredit','40.00'),('card-credit','VendorCredit','40.00')]:
    body={'TxnDate':'2026-10-05','Memo':PREFIX+' '+key}
    if kind in ('Invoice','CreditMemo'):body.update(CustomerRef={'ListID':parties['customer']},ARAccountRef=ref('AR'),**{kind+'LineAdd':{'ItemRef':ref('Service'),'Quantity':'1','Rate':amount}})
    else:body.update(VendorRef={'ListID':parties['vendor']},APAccountRef=ref('AP'),ExpenseLineAdd={'AccountRef':ref('Expense'),'Amount':amount})
    requests.append(call(kind+'Add',{kind+'Add':body},key))
seed=saved('seed') if options.continue_before_apply or options.verify_recorded_apply else batch('seed',requests,True);ids={r['requestId']:scalar(r['data'][0],'TxnID') for r in seed['results']}
payment_queries=[call(kind+'Query',{'EntityFilter':{'ListID':parties['customer' if kind=='ReceivePayment' else 'vendor']},'IncludeLineItems':True},kind) for kind in ('ReceivePayment','BillPaymentCheck','BillPaymentCreditCard')]
before=saved('payments-before-confirmed') if options.verify_recorded_apply else batch('payments-before-confirmed',payment_queries);assert all(not r['data'] for r in before['results'])
def application(key,credit):return {'TxnID':ids[key],'SetCredit':{'CreditTxnID':ids[credit],'AppliedAmount':'40.00'}}
applied=saved('apply') if options.verify_recorded_apply else batch('apply',[
    call('ReceivePaymentAdd',{'ReceivePaymentAdd':{'CustomerRef':{'ListID':parties['customer']},'ARAccountRef':ref('AR'),'TxnDate':'2026-10-05','TotalAmount':'0.00','AppliedToTxnAdd':application('invoice','customer-credit')}},'receive'),
    call('BillPaymentCheckAdd',{'BillPaymentCheckAdd':{'PayeeEntityRef':{'ListID':parties['vendor']},'APAccountRef':ref('AP'),'BankAccountRef':ref('Bank'),'TxnDate':'2026-10-05','IsToBePrinted':False,'AppliedToTxnAdd':application('bill-check','check-credit')}},'check'),
    call('BillPaymentCreditCardAdd',{'BillPaymentCreditCardAdd':{'PayeeEntityRef':{'ListID':parties['vendor']},'APAccountRef':ref('AP'),'CreditCardAccountRef':ref('Card'),'TxnDate':'2026-10-05','AppliedToTxnAdd':application('bill-card','card-credit')}},'card')],True)
assert len(applied['results'])==3 and all(r['outcome']=='succeeded' and r['statusCode']==0 for r in applied['results'])
after=saved('payments-after') if options.verify_recorded_apply else batch('payments-after',payment_queries)
assert not after['results'][0]['data']
for i in (1,2):
    assert len(after['results'][i]['data'])==1 and scalar(after['results'][i]['data'][0],'TxnID')==scalar(applied['results'][i]['data'][0],'TxnID')
stubs=[scalar(applied['results'][i]['data'][0],'TxnID') for i in (1,2)]
batch('stub-transactions',[call('TransactionQuery',{'TxnID':stubs},'stubs')])
fresh=batch('verify',[call(kind+'Query',{'TxnID':ids[key],'IncludeLinkedTxns':True},key) for key,kind in [('invoice','Invoice'),('customer-credit','CreditMemo'),('bill-check','Bill'),('bill-card','Bill'),('check-credit','VendorCredit'),('card-credit','VendorCredit')]]+
    [call('BillToPayQuery',{'PayeeEntityRef':{'ListID':parties['vendor']}},'payable')])
rows={r['requestId']:r['data'] for r in fresh['results']}
balances={scalar(n['children'][0],'TxnID'):Decimal(scalar(n['children'][0],'AmountDue')) for n in rows['payable'] if n['children'][0]['name']=='BillToPay'}
assert Decimal(scalar(rows['invoice'][0],'BalanceRemaining'))==60 and Decimal(scalar(rows['customer-credit'][0],'CreditRemaining'))==0
for key,credit in [('invoice','customer-credit'),('bill-check','check-credit'),('bill-card','card-credit')]:
    if key!='invoice':assert balances[ids[key]]==60
    links=[n for n in rows[key][0]['children'] if n['name']=='LinkedTxn' and scalar(n,'TxnID')==ids[credit]]
    assert len(links)==1 and abs(Decimal(scalar(links[0],'Amount')))==40
assert not any(n['children'][0]['name']=='CreditToApply' and scalar(n['children'][0],'TxnID') in (ids['check-credit'],ids['card-credit']) for n in rows['payable'])
for command,keys in [('invoices',['invoice']),('bills',['bill-check','bill-card'])]:
    for open_only in (False,True):
        value=run(command+('-open' if open_only else '-all'),[command,'list','--page-size','25',*(['--open'] if open_only else [])]);assert value['complete']
        found=[r for r in value['results'] if r['id'] in [ids[k] for k in keys]]
        assert len(found)==len(keys) and all(r['remainingAmount']==60 and r['paid'] is False for r in found)
print(json.dumps({'fixtureStamp':STAMP,'creditOnlyRequestsAccepted':3,'receivePaymentTransactions':0,'billPaymentStubTransactions':2,'documentsRetainedOpen':3,'scope':'Run independent verifier; empty/lean response is not proof of no mutation. Tested host generated bill-payment stubs, despite general documentation describing links only.'}),flush=True)
