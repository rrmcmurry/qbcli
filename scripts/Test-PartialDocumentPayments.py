"""Invoice/bill unpaid -> partially paid -> paid lifecycle, designated Test.QBW only."""
import datetime
import argparse
import json
import pathlib
import subprocess
from decimal import Decimal

ROOT=pathlib.Path(__file__).resolve().parent.parent;DEST=ROOT/'artifacts/fixtures'
EXE=ROOT/'artifacts/win-x64/qbcli.exe';COMPANY=(ROOT/'QBData/Test.QBW').resolve()
assert COMPANY==pathlib.Path(__import__("os").environ["QBCLI_LIVE_TEST_COMPANY"]).resolve(strict=True)
STAMP=datetime.datetime.now().strftime('%H%M%S');PREFIX='PARTIAL'+STAMP
parser=argparse.ArgumentParser(description=__doc__);parser.add_argument('--continue-before-payments',help='Use a saved successful seed after reads stopped; refuses any existing payment plan/result.');options=parser.parse_args()
if options.continue_before_payments:
    STAMP=options.continue_before_payments;assert len(STAMP)==6 and STAMP.isdigit();PREFIX='PARTIAL'+STAMP
    assert not list(DEST.glob(f'lifecycle-{STAMP}-partial-documents-*-payments-*.json')),'Payments may already have been attempted; never replay'
def scalar(n,k):return next((c.get('value') for c in n['children'] if c['name']==k),None)
def ref(name):return {'FullName':'SDK154955 '+name}
def call(op,fields,key):return {'requestId':key,'operation':op,'fields':fields}
def run(label,args):
    proc=subprocess.run([str(EXE),*args,'--company-file',str(COMPANY),'--json'],capture_output=True,text=True,encoding='utf-8')
    (DEST/f'lifecycle-{STAMP}-partial-documents-{label}-result.json').write_text(proc.stdout,encoding='utf-8')
    value=json.loads(proc.stdout,parse_float=Decimal)
    print(json.dumps({'phase':label,'success':value.get('success'),'outcomes':[{k:r.get(k) for k in ('operation','outcome','statusCode')} for r in value.get('results',[]) if 'operation' in r]}),flush=True)
    assert proc.returncode==0 and value['success'] and pathlib.Path(value['companyFile'])==COMPANY,'Inspect retained evidence; never replay'
    return value
def batch(label,calls,apply=False):
    path=DEST/f'lifecycle-{STAMP}-partial-documents-{label}-plan.json'
    path.write_text(json.dumps({'protocolVersion':'13.0','country':'US','requests':calls}),encoding='utf-8')
    return run(label,['sdk','batch','--input',str(path),*(['--apply'] if apply else [])])
def saved(label):
    value=json.loads((DEST/f'lifecycle-{STAMP}-partial-documents-{label}-result.json').read_text(encoding='utf-8'));assert value['success'] and pathlib.Path(value['companyFile'])==COMPANY;return value
base=saved('base') if options.continue_before_payments else batch('base',[call('CustomerAdd',{'CustomerAdd':{'Name':PREFIX+' Customer'}},'customer'),call('VendorAdd',{'VendorAdd':{'Name':PREFIX+' Vendor'}},'vendor')],True)
parties={r['requestId']:scalar(r['data'][0],'ListID') for r in base['results']}
specs=[('invoice','Invoice','100.25','30.10'),('bill-check','Bill','101.35','31.10'),('bill-card','Bill','102.45','32.10')]
calls=[]
for key,kind,total,partial in specs:
    body={'TxnDate':'2026-10-05','DueDate':'2026-11-05','RefNumber':'P'+STAMP+key[:1],'Memo':PREFIX+' '+key}
    if kind=='Invoice':body.update(CustomerRef={'ListID':parties['customer']},ARAccountRef=ref('AR'),InvoiceLineAdd={'ItemRef':ref('Service'),'Quantity':'1','Rate':total})
    else:body.update(VendorRef={'ListID':parties['vendor']},APAccountRef=ref('AP'),ExpenseLineAdd={'AccountRef':ref('Expense'),'Amount':total})
    calls.append(call(kind+'Add',{kind+'Add':body},key))
seed=saved('seed') if options.continue_before_payments else batch('seed',calls,True);ids={r['requestId']:scalar(r['data'][0],'TxnID') for r in seed['results']}
def snapshot(stage):
    raw=batch(stage+'-confirmed-sdk',[call(kind+'Query',{'TxnID':ids[key],'IncludeLineItems':False,'IncludeLinkedTxns':True},key) for key,kind,total,partial in specs])
    records={r['requestId']:r['data'][0] for r in raw['results']}
    payable=batch(stage+'-payable',[call('BillToPayQuery',{'PayeeEntityRef':{'ListID':parties['vendor']}},'vendor')])
    balances={scalar(n['children'][0],'TxnID'):Decimal(scalar(n['children'][0],'AmountDue')) for n in payable['results'][0]['data'] if n['children'][0]['name']=='BillToPay'}
    for key,kind,total,partial in specs:
        record=records[key];balance=Decimal(scalar(record,'BalanceRemaining')) if kind=='Invoice' else balances.get(ids[key],Decimal('0'))
        expected=Decimal(total) if stage=='unpaid' else Decimal(total)-Decimal(partial) if stage=='partial' else Decimal('0')
        assert balance==expected and scalar(record,'IsPaid')==('true' if stage=='paid' else 'false')
        if kind=='Bill':assert Decimal(scalar(record,'AmountDue'))==Decimal(total),'AmountDue must remain original total'
    for kind,command in [('Invoice','invoices'),('Bill','bills')]:
        wanted={ids[key]:key for key,k,_,_ in specs if k==kind}
        for open_only in (False,True):
            value=run(stage+'-'+command+('-open' if open_only else '-all'),[command,'list','--page-size','25',*(['--open'] if open_only else [])]);assert value['complete']
            found={r['id']:r for r in value['results'] if r['id'] in wanted}
            assert set(found)==(set() if open_only and stage=='paid' else set(wanted))
            for txn,summary in found.items():
                record=records[wanted[txn]]
                assert summary['remainingAmount']==(Decimal(scalar(record,'BalanceRemaining')) if kind=='Invoice' else balances.get(txn,Decimal('0')))
                assert summary['paid']==(stage=='paid')
def pay(stage):
    calls=[]
    for key,kind,total,partial in specs:
        amount=partial if stage=='partial' else str(Decimal(total)-Decimal(partial))
        application={'TxnID':ids[key],'PaymentAmount':amount}
        body={'TxnDate':'2026-10-05','Memo':PREFIX+' '+stage+' '+key,'AppliedToTxnAdd':application}
        if kind=='Invoice':
            op='ReceivePaymentAdd';body.update(CustomerRef={'ListID':parties['customer']},ARAccountRef=ref('AR'),DepositToAccountRef=ref('Bank'),TotalAmount=amount)
        else:
            op='BillPaymentCheckAdd' if key=='bill-check' else 'BillPaymentCreditCardAdd'
            body.update(PayeeEntityRef={'ListID':parties['vendor']},APAccountRef=ref('AP'))
            if key=='bill-check':body.update(BankAccountRef=ref('Bank'),IsToBePrinted=False)
            else:body.update(CreditCardAccountRef=ref('Card'))
        calls.append(call(op,{op:body},key))
    batch(stage+'-payments',calls,True)
snapshot('unpaid');pay('partial');snapshot('partial');pay('paid');snapshot('paid')
print(json.dumps({'fixtureStamp':STAMP,'documents':3,'paymentTypes':3,'stagesVerified':3,'scope':'Test.QBW only; invoke independent saved-evidence verifier before exporting coverage.'}),flush=True)
