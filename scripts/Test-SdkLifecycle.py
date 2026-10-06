"""Sequential, destructive SDK checks on the explicitly authorized Test.QBW only."""
import json
import pathlib
import subprocess
import datetime
import sys

ROOT = pathlib.Path(__file__).resolve().parent.parent
EXE = ROOT / 'artifacts/win-x64/qbcli.exe'
COMPANY = ROOT / 'QBData/Test.QBW'
DEST = ROOT / 'artifacts/fixtures'
PREFIX = 'SDK154955'
STAMP = datetime.datetime.now().strftime('%H%M%S')
RESUME = pathlib.Path(sys.argv[1]).resolve() if len(sys.argv)>1 else None
if RESUME:
    if RESUME.parent != DEST.resolve() or not RESUME.name.endswith('-mod-and-payments-result.json'):
        raise ValueError('Resume requires a recorded fixture result in artifacts/fixtures')
    STAMP = RESUME.name.split('-')[1]

def leaf(node, name):
    return next(c['value'] for c in node['children'] if c['name'] == name)

def run(label, calls, apply=True):
    path = DEST / f'lifecycle-{STAMP}-{label}-plan.json'
    path.write_text(json.dumps({'protocolVersion':'13.0','country':'US','onError':'continueOnError','requests':[
        {'requestId':str(i+1),'operation':op,'fields':fields} for i,(op,fields) in enumerate(calls)]}),encoding='utf-8')
    args = [str(EXE),'sdk','batch','--input',str(path),'--company-file',str(COMPANY)]
    if apply: args += ['--apply']
    result = subprocess.run(args,capture_output=True,text=True,encoding='utf-8')
    (DEST / f'lifecycle-{STAMP}-{label}-result.json').write_text(result.stdout,encoding='utf-8')
    value = json.loads(result.stdout)
    if 'error' in value: raise RuntimeError(value['error'])
    print(json.dumps({'label':label,'results':[{k:r[k] for k in ('operation','outcome','statusCode')} for r in value['results']]}),flush=True)
    return value['results']

def ref(name): return {'FullName':f'{PREFIX} {name}'}
def body(op,fields): return (op,{op:fields})
tx = json.loads((DEST / f'{PREFIX}-transactions-result.json').read_text(encoding='utf-8'))
records = {r['operation']:r['data'][0] for r in tx['results']}
check = records['CheckAdd']
line = next(c for c in check['children'] if c['name']=='ExpenseLineRet')
modified = json.loads(RESUME.read_text(encoding='utf-8'))['results'] if RESUME else run('mod-and-payments',[
    body('CheckMod',{'TxnID':leaf(check,'TxnID'),'EditSequence':leaf(check,'EditSequence'),
        'ExpenseLineMod':{'TxnLineID':leaf(line,'TxnLineID'),'ClassRef':{'FullName':'RentalA'}}}),
    body('ReceivePaymentAdd',{'CustomerRef':ref('Customer'),'ARAccountRef':ref('AR'),'TotalAmount':'10.00',
        'DepositToAccountRef':ref('Bank'),'AppliedToTxnAdd':{'TxnID':leaf(records['InvoiceAdd'],'TxnID'),'PaymentAmount':'10.00'}}),
    body('BillPaymentCheckAdd',{'PayeeEntityRef':ref('Vendor'),'APAccountRef':ref('AP'),'BankAccountRef':ref('Bank'),
        'IsToBePrinted':False,'AppliedToTxnAdd':{'TxnID':leaf(records['BillAdd'],'TxnID'),'PaymentAmount':'3.00'}}),
    body('TimeTrackingAdd',{'TxnDate':'2026-10-05','EntityRef':ref('Vendor'),'CustomerRef':ref('Customer'),'ItemServiceRef':ref('Service'),
        'Duration':'PT1H','BillableStatus':'NotBillable'}),
    body('CustomerAdd',{'Name':f'LIF{STAMP} A'}),
    body('CustomerAdd',{'Name':f'LIF{STAMP} B','IsActive':False}),
    body('ClassAdd',{'Name':f'LIF{STAMP} Delete'}),
    body('ClassAdd',{'Name':f'LIF{STAMP} MergeFrom'}),
    body('ClassAdd',{'Name':f'LIF{STAMP} MergeTo'}),
])
if any(r['outcome'] != 'succeeded' for r in modified if r['operation']!='TimeTrackingAdd'): raise RuntimeError('Inspect failed fixture before proceeding')
new_check = modified[0]['data'][0]
new_line = next(c for c in new_check['children'] if c['name']=='ExpenseLineRet')
assert leaf(new_line,'Amount') == leaf(line,'Amount'), 'Reclassification changed amount'
assert next(c for c in new_line['children'] if c['name']=='AccountRef') == next(c for c in line['children'] if c['name']=='AccountRef')
assert leaf(next(c for c in new_line['children'] if c['name']=='ClassRef'),'FullName') == 'RentalA'
deleted,source,target = (modified[i]['data'][0] for i in (6,7,8))
run('delete-void-merge',[
    ('ListDel',{'ListDelType':'Class','ListID':leaf(deleted,'ListID')}),
    ('ListMerge',{'ListMergeType':'Class','MergeFrom':{'ListID':leaf(source,'ListID'),'EditSequence':leaf(source,'EditSequence')},
                  'MergeTo':{'ListID':leaf(target,'ListID'),'EditSequence':leaf(target,'EditSequence')}}),
    ('TxnDel',{'TxnDelType':'Estimate','TxnID':leaf(records['EstimateAdd'],'TxnID')}),
    ('TxnVoid',{'TxnVoidType':'CreditCardCredit','TxnID':leaf(records['CreditCardCreditAdd'],'TxnID')}),
])
# Deliberate failure confirms partial success; no automatic retry of submitted writes.
partial = run('partial-failure',[
    body('CustomerAdd',{'Name':f'LIF{STAMP} A'}),
    body('ClassAdd',{'Name':f'LIF{STAMP} AfterFailure'}),
])
assert partial[0]['outcome']=='failed' and partial[1]['outcome']=='succeeded'
filter_path = DEST / f'lifecycle-{STAMP}-all-customers.json'
filter_path.write_text(json.dumps({'ActiveStatus':'All'}),encoding='utf-8')
query = subprocess.run([str(EXE),'sdk','run','CustomerQuery','--input',str(filter_path),'--all','--page-size','1','--company-file',str(COMPANY)],capture_output=True,text=True,encoding='utf-8')
value=json.loads(query.stdout)
(DEST / f'lifecycle-{STAMP}-pagination-result.json').write_text(query.stdout,encoding='utf-8')
assert value['success'] and value['complete'] and value['pages']>=3
print(json.dumps({'paginationPages':value['pages'],'records':len(value['data'])}),flush=True)
