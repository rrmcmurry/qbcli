"""Synthetic multi-type bulk test. Mutations are hard-bounded to designated Test.QBW."""
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
PREFIX='BULK'+STAMP
TYPES=['Check','Bill','CreditCardCharge','CreditCardCredit','VendorCredit','ItemReceipt']
def leaf(record,key):return next((c['value'] for c in record['children'] if c['name']==key),None)
def call(op,body,key):return {'requestId':key,'operation':op,'fields':{op:body}}
def run(label,calls):
    path=DEST/f'lifecycle-{STAMP}-{label}-plan.json'
    path.write_text(json.dumps({'protocolVersion':'13.0','country':'US','onError':'stopOnError','requests':calls}),encoding='utf-8')
    result=subprocess.run([str(EXE),'sdk','batch','--input',str(path),'--company-file',str(COMPANY),'--apply','--batch-size','100'],capture_output=True,text=True,encoding='utf-8')
    path.with_name(path.name.replace('-plan','-result')).write_text(result.stdout,encoding='utf-8')
    value=json.loads(result.stdout)
    assert value.get('success'),value.get('error') or [(r['operation'],r['statusCode'],r.get('statusMessage')) for r in value['results'] if r['outcome']!='succeeded']
    return value
base=run('bulk-base',[
    call('AccountAdd',{'Name':PREFIX+' Expense','AccountType':'Expense'},'account'),
    call('AccountAdd',{'Name':PREFIX+' Other','AccountType':'Expense'},'other'),
    call('ClassAdd',{'Name':PREFIX+' Target'},'class')])
ids={r['requestId']:leaf(r['data'][0],'ListID') for r in base['results']}
ref=lambda suffix:{'FullName':'SDK154955 '+suffix}
calls=[]
for kind in TYPES:
    for index in range(10):
        body={'TxnDate':'2026-10-05','Memo':PREFIX+' '+kind+' '+str(index), 'ExpenseLineAdd':[
            {'AccountRef':{'ListID':ids['account']},'Amount':'11.00','Memo':'blank one'},
            {'AccountRef':{'ListID':ids['account']},'Amount':'12.00','Memo':'blank two'},
            {'AccountRef':{'ListID':ids['account']},'Amount':'13.00','ClassRef':ref('Class'),'Memo':'keep class'},
            {'AccountRef':{'ListID':ids['other']},'Amount':'14.00','Memo':'keep other account'}]}
        if kind in ('Check','CreditCardCharge','CreditCardCredit'):
            body.update(AccountRef=ref('Bank' if kind=='Check' else 'Card'),PayeeEntityRef=ref('Vendor'))
        else:body.update(VendorRef=ref('Vendor'),APAccountRef=ref('AP'))
        # An untouched item line exercises preservation of a separate posting branch.
        if kind=='Check':body['ItemLineAdd']=[{'ItemRef':ref('Supplies'),'Quantity':'1','Cost':'3.00'}]
        calls.append(call(kind+'Add',body,kind+'-'+str(index)))
seed=run('bulk-seed',calls)
selection=DEST/f'lifecycle-{STAMP}-bulk-selection.json'
selection.write_text(json.dumps({'accountId':ids['account'],'classId':ids['class']}),encoding='utf-8')
def bulk(label,apply=False):
    args=[str(EXE),'bulk','reclassify','--input',str(selection),'--company-file',str(COMPANY),'--page-size','7','--batch-size','7']
    if apply:args+=['--apply']
    start=time.perf_counter()
    output=subprocess.run(args,capture_output=True,text=True,encoding='utf-8')
    elapsed=time.perf_counter()-start
    (DEST/f'lifecycle-{STAMP}-bulk-{label}.json').write_text(output.stdout,encoding='utf-8')
    value=json.loads(output.stdout)
    assert value.get('success'),value.get('error') or value.get('verification')
    return value,elapsed
preview,_=bulk('preview')
assert preview['counts']['transactionsToChange']==60,preview['counts']
assert preview['counts']['linesToChange']==120
applied,elapsed=bulk('apply',True)
assert applied['planDigest']==preview['planDigest']
assert len(applied['verification'])==60
assert all(v['classVerified'] and v['otherFieldsPreserved'] for v in applied['verification'])
after,_=bulk('after')
assert after['counts']['linesToChange']==0,after['counts']
# Preserve operation acceptance in the existing metadata exporter without company values.
(DEST/f'lifecycle-{STAMP}-bulk-execution-result.json').write_text(json.dumps(applied['execution']),encoding='utf-8')
metadata={'transactionTypes':TYPES,'transactionsModified':60,'linesClassified':120,'batchSize':7,'iteratorPageSize':7,
          'everyChangedClassReadBack':True,'otherReturnedFieldsPreserved':True,'repeatSelectionEmpty':True,
          'endToEndApplySeconds':round(elapsed,3),'scope':'Includes six scans, reference resolution, modification batches and read-back; synthetic local fixture, not a production throughput guarantee.'}
(DEST/f'lifecycle-{STAMP}-bulk-verification.json').write_text(json.dumps(metadata,indent=2),encoding='utf-8')
print(json.dumps(metadata),flush=True)
