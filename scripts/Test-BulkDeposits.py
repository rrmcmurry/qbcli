"""Deposit fixtures, SDK-only, hard bounded to designated expendable Test.QBW."""
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
PREFIX='DEPCLASS'+STAMP
def leaf(node,key):return next((c.get('value') for c in node['children'] if c['name']==key),None)
def refid(node,key):return next((leaf(c,'ListID') for c in node['children'] if c['name']==key),None)
def call(op,body,key):return {'requestId':key,'operation':op,'fields':{op:body}}
def run(label,calls):
    path=DEST/f'lifecycle-{STAMP}-deposit-bulk-{label}-plan.json'
    path.write_text(json.dumps({'protocolVersion':'13.0','requests':calls}),encoding='utf-8')
    result=subprocess.run([str(EXE),'sdk','batch','--input',str(path),'--company-file',str(COMPANY),
        '--apply','--batch-size','7'],capture_output=True,text=True,encoding='utf-8',timeout=120)
    path.with_name(path.name.replace('-plan.json','-result.json')).write_text(result.stdout,encoding='utf-8')
    value=json.loads(result.stdout)
    assert value.get('success'),value.get('error') or [(r['operation'],r['statusCode'],r.get('statusMessage')) for r in value['results'] if r['outcome']!='succeeded']
    return value
base=run('base',[call('AccountAdd',{'Name':PREFIX+' Target','AccountType':'Income'},'account'),
    call('AccountAdd',{'Name':PREFIX+' Other','AccountType':'Income'},'other'),
    call('ClassAdd',{'Name':PREFIX+' Class'},'class')])
ids={r['requestId']:leaf(r['data'][0],'ListID') for r in base['results']}
ref=lambda suffix:{'FullName':'SDK154955 '+suffix}
payment=run('payment',[call('ReceivePaymentAdd',{'CustomerRef':ref('Customer'),'ARAccountRef':ref('AR'),
    'TxnDate':'2026-10-05','TotalAmount':'25.00','IsAutoApply':False},str(i)) for i in range(20)])
payment_ids=[leaf(r['data'][0],'TxnID') for r in payment['results']]
deposits=[]
for i in range(10):
    lines=[{'AccountRef':{'ListID':ids['account']},'Amount':'11.00','Memo':'blank one','CheckNumber':'D1'},
        {'AccountRef':{'ListID':ids['account']},'Amount':'12.00','Memo':'blank two','CheckNumber':'D2'},
        {'AccountRef':{'ListID':ids['account']},'Amount':'13.00','ClassRef':ref('Class'),'Memo':'keep class'},
        {'AccountRef':{'ListID':ids['other']},'Amount':'14.00','Memo':'keep other account'},
        {'PaymentTxnID':payment_ids[i*2],'OverrideMemo':'keep linked payment','OverrideClassRef':ref('Class')},
        {'PaymentTxnID':payment_ids[i*2+1],'OverrideMemo':'blank linked payment'}]
    deposits.append(call('DepositAdd',{'TxnDate':'2026-10-05','DepositToAccountRef':ref('Bank'),
        'Memo':PREFIX+' '+str(i),'CashBackInfoAdd':{'AccountRef':ref('Expense'),'Amount':'2.00','Memo':'keep cash back'},
        'DepositLineAdd':lines},str(i)))
seed=run('seed',deposits)
txns=[leaf(r['data'][0],'TxnID') for r in seed['results']]
read=run('read',[{'operation':'DepositQuery','fields':{'TxnID':txns,'IncludeLineItems':True}}])
record=read['results'][0]['data'][0]
print(json.dumps({'depositLineShapes':[{'fields':[f['name'] for f in c['children']],
    'linkedPayment':leaf(c,'TxnID') is not None,'hasAccountId':refid(c,'AccountRef') is not None}
    for c in record['children'] if c['name']=='DepositLineRet']}),flush=True)
selection=DEST/f'lifecycle-{STAMP}-deposit-bulk-selection.json'
selection.write_text(json.dumps({'accountId':ids['account'],'classId':ids['class'],'transactionTypes':['Deposit']}),encoding='utf-8')
def bulk(label,apply=False):
    args=[str(EXE),'bulk','reclassify','--input',str(selection),'--company-file',str(COMPANY),'--page-size','3','--batch-size','7']
    if apply:args+=['--apply']
    start=time.perf_counter()
    result=subprocess.run(args,capture_output=True,text=True,encoding='utf-8',timeout=120)
    elapsed=time.perf_counter()-start
    (DEST/f'lifecycle-{STAMP}-deposit-bulk-{label}.json').write_text(result.stdout,encoding='utf-8')
    value=json.loads(result.stdout)
    assert value.get('success'),value.get('error') or value.get('verification') or value.get('execution')
    return value,elapsed
source_before=run('source-before',[{'operation':'ReceivePaymentQuery','fields':{'TxnID':payment_ids}}])
preview,_=bulk('preview')
assert preview['counts']['transactionsToChange']==10 and preview['counts']['linesToChange']==20,preview['counts']
applied,elapsed=bulk('apply',True)
assert len(applied['verification'])==10
assert all(v['classVerified'] and v['otherFieldsPreserved'] for v in applied['verification'])
after,_=bulk('after')
assert after['counts']['linesToChange']==0
(DEST/f'lifecycle-{STAMP}-deposit-bulk-execution-result.json').write_text(json.dumps(applied['execution']),encoding='utf-8')
undeposited=next(refid(line,'AccountRef') for line in record['children'] if line['name']=='DepositLineRet' and leaf(line,'TxnID'))
selection.write_text(json.dumps({'accountId':undeposited,'classId':ids['class'],'transactionTypes':['Deposit']}),encoding='utf-8')
linked_preview,_=bulk('linked-preview')
# Prior disposable fixtures may also have blank source-account lines. Every
# submitted record is independently checked; never assume only our ten exist.
assert set(txns).issubset({c['txnId'] for c in linked_preview['changes']}),linked_preview['counts']
assert linked_preview['counts']['linesToChange']>=10
linked,linked_elapsed=bulk('linked-apply',True)
assert all(v['classVerified'] and v['otherFieldsPreserved'] for v in linked['verification'])
linked_after,_=bulk('linked-after')
assert linked_after['counts']['linesToChange']==0
(DEST/f'lifecycle-{STAMP}-deposit-linked-execution-result.json').write_text(json.dumps(linked['execution']),encoding='utf-8')
source_after=run('source-after',[{'operation':'ReceivePaymentQuery','fields':{'TxnID':payment_ids}}])
def normalized_sources(response):
    return sorted([{**r,'children':[c for c in r['children'] if c['name'] not in ('TimeModified','EditSequence')]}
        for r in response['results'][0]['data']],key=lambda r:leaf(r,'TxnID'))
assert normalized_sources(source_before)==normalized_sources(source_after)
metadata={'transactionTypes':['Deposit'],'transactionsModified':10,'linesClassified':20+linked_preview['counts']['linesToChange'],
    'directLinesClassified':20,'linkedLinesClassified':linked_preview['counts']['linesToChange'],'linkedPaymentClassesVerified':True,
    'sourcePaymentFieldsPreserved':True,'allSixLinesRetainedPerFixtureDeposit':True,
    'linkedPaymentPreserved':True,'cashBackPreserved':True,'otherReturnedFieldsPreserved':True,'repeatSelectionEmpty':True,
    'endToEndApplySeconds':round(elapsed+linked_elapsed,3),'scope':'Separate direct-account and linked-payment account selections; fixture has ten deposits with twenty received-payment links and cash back. Linked-account scan may include earlier disposable fixtures; all returned fields checked. Local synthetic evidence only.'}
(DEST/f'lifecycle-{STAMP}-deposit-bulk-verification.json').write_text(json.dumps(metadata,indent=2),encoding='utf-8')
print(json.dumps(metadata),flush=True)
