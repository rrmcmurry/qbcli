"""Synthetic balanced journals. Mutations only in designated expendable Test.QBW."""
import datetime
import json
import pathlib
import subprocess
import time
ROOT=pathlib.Path(__file__).resolve().parent.parent
COMPANY=(ROOT/'QBData/Test.QBW').resolve()
assert COMPANY==pathlib.Path(__import__("os").environ["QBCLI_LIVE_TEST_COMPANY"]).resolve(strict=True)
EXE=ROOT/'artifacts/win-x64/qbcli.exe'
DEST=ROOT/'artifacts/fixtures'
STAMP=datetime.datetime.now().strftime('%H%M%S')
PREFIX='JCLASS'+STAMP
def leaf(node,key):return next((c.get('value') for c in node['children'] if c['name']==key),None)
def call(op,body,key):return {'requestId':key,'operation':op,'fields':{op:body}}
def run(label,calls):
    path=DEST/f'lifecycle-{STAMP}-journal-bulk-{label}-plan.json'
    path.write_text(json.dumps({'protocolVersion':'13.0','requests':calls}),encoding='utf-8')
    result=subprocess.run([str(EXE),'sdk','batch','--input',str(path),'--company-file',str(COMPANY),
        '--apply','--batch-size','7'],capture_output=True,text=True,encoding='utf-8',timeout=120)
    path.with_name(path.name.replace('-plan.json','-result.json')).write_text(result.stdout,encoding='utf-8')
    value=json.loads(result.stdout)
    assert value.get('success'),value.get('error') or [(r['operation'],r['statusCode'],r.get('statusMessage')) for r in value['results'] if r['outcome']!='succeeded']
    return value
base=run('base',[call('AccountAdd',{'Name':PREFIX+' Target','AccountType':'Expense'},'account'),
    call('AccountAdd',{'Name':PREFIX+' Other','AccountType':'Expense'},'other'),
    call('ClassAdd',{'Name':PREFIX+' Class'},'class')])
ids={r['requestId']:leaf(r['data'][0],'ListID') for r in base['results']}
calls=[]
for index in range(20):
    specs=[('Debit','account','11.00',None),('Credit','other','15.00',None),
        ('Debit','account','13.00','SDK154955 Class'),('Credit','account','12.00',None),
        ('Debit','other','14.00',None),('Credit','other','11.00',None)]
    choices=[]
    for number,(side,account,amount,cls) in enumerate(specs):
        line={'AccountRef':{'ListID':ids[account]},'Amount':amount,'Memo':f'{PREFIX} keep line {number}',
            'EntityRef':{'FullName':'SDK154955 Customer'},'BillableStatus':'NotBillable'}
        if cls:line['ClassRef']={'FullName':cls}
        choices.append({'Journal'+side+'Line':line})
    calls.append(call('JournalEntryAdd',{'TxnDate':'2026-10-05','RefNumber':'J'+STAMP+str(index),
        'IsAdjustment':index%2==0,'$choices':choices},str(index)))
expense_types=['Check','Bill','CreditCardCharge','CreditCardCredit','VendorCredit','ItemReceipt']
ref=lambda suffix:{'FullName':'SDK154955 '+suffix}
for kind in expense_types:
    body={'TxnDate':'2026-10-05','Memo':PREFIX+' mixed '+kind,'ExpenseLineAdd':[
        {'AccountRef':{'ListID':ids['account']},'Amount':'11.00','Memo':'blank one'},
        {'AccountRef':{'ListID':ids['account']},'Amount':'12.00','Memo':'blank two'},
        {'AccountRef':{'ListID':ids['account']},'Amount':'13.00','ClassRef':ref('Class'),'Memo':'keep class'},
        {'AccountRef':{'ListID':ids['other']},'Amount':'14.00','Memo':'keep other account'}]}
    if kind in ('Check','CreditCardCharge','CreditCardCredit'):
        body.update(AccountRef=ref('Bank' if kind=='Check' else 'Card'),PayeeEntityRef=ref('Vendor'))
    else:body.update(VendorRef=ref('Vendor'),APAccountRef=ref('AP'))
    calls.append(call(kind+'Add',body,kind))
seed=run('seed',calls)
selection=DEST/f'lifecycle-{STAMP}-journal-bulk-selection.json'
selection.write_text(json.dumps({'accountId':ids['account'],'classId':ids['class']}),encoding='utf-8')
def bulk(label,apply=False):
    args=[str(EXE),'bulk','reclassify','--input',str(selection),'--company-file',str(COMPANY),'--page-size','3','--batch-size','7']
    if apply:args+=['--apply']
    start=time.perf_counter()
    result=subprocess.run(args,capture_output=True,text=True,encoding='utf-8',timeout=120)
    elapsed=time.perf_counter()-start
    (DEST/f'lifecycle-{STAMP}-journal-bulk-{label}.json').write_text(result.stdout,encoding='utf-8')
    value=json.loads(result.stdout)
    assert value.get('success'),value.get('error') or value.get('verification')
    return value,elapsed
preview,_=bulk('preview')
assert preview['counts']['transactionsToChange']==26,preview['counts']
assert preview['counts']['linesToChange']==52
for request in (r for r in preview['plan']['requests'] if r['operation']=='JournalEntryMod'):
    lines=request['fields']['JournalEntryMod']['JournalLineMod']
    assert len(lines)==6 and all('Amount' not in line and 'JournalLineType' not in line for line in lines)
applied,elapsed=bulk('apply',True)
assert applied['planDigest']==preview['planDigest']
assert len(applied['verification'])==26
assert all(v['classVerified'] and v['otherFieldsPreserved'] for v in applied['verification'])
after,_=bulk('after')
assert after['counts']['linesToChange']==0
(DEST/f'lifecycle-{STAMP}-journal-bulk-execution-result.json').write_text(json.dumps(applied['execution']),encoding='utf-8')
metadata={'transactionTypes':expense_types+['JournalEntry'],'transactionsModified':26,'linesClassified':52,'journalEntriesModified':20,
    'scannedTransactionTypes':preview['selection']['transactionTypes'],
    'debitAndCreditClassesVerified':True,'otherReturnedFieldsPreserved':True,'allSixLinesRetainedPerJournal':True,
    'repeatSelectionEmpty':True,'batchSize':7,'iteratorPageSize':3,'endToEndApplySeconds':round(elapsed,3),
    'scope':'Balanced synthetic journals, selected debit and credit plus existing-class and other-account lines. Local timings include scans and read-back, not production throughput guarantees.'}
(DEST/f'lifecycle-{STAMP}-journal-bulk-verification.json').write_text(json.dumps(metadata,indent=2),encoding='utf-8')
print(json.dumps(metadata),flush=True)
