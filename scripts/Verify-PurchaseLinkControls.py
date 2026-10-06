"""Read PO receiving state and verify eight rejected linking controls; no writes."""
import argparse
import json
import pathlib
import subprocess
ROOT=pathlib.Path(__file__).resolve().parent.parent;DEST=ROOT/'artifacts/fixtures'
COMPANY=(ROOT/'QBData/Test.QBW').resolve();EXE=ROOT/'artifacts/win-x64/qbcli.exe'
assert COMPANY==pathlib.Path(__import__("os").environ["QBCLI_LIVE_TEST_COMPANY"]).resolve(strict=True)
parser=argparse.ArgumentParser(description=__doc__);parser.add_argument('stamp');options=parser.parse_args()
assert len(options.stamp)==6 and options.stamp.isdigit()
def load(label):return json.loads((DEST/f'lifecycle-{options.stamp}-po-links-{label}.json').read_text(encoding='utf-8'))
def scalar(n,key):return next((c.get('value') for c in n['children'] if c['name']==key),None)
def walk(n):
    yield n
    for c in n['children']:yield from walk(c)
original=load('link-result');orders=load('orders-result');preview=load('resume-preview');resumed=load('resume-apply')
assert len(orders['results'])==8 and all(r['outcome']=='succeeded' for r in orders['results'])
assert [(r['outcome'],r['statusCode']) for r in original['results'][:4]]==[('failed',3250)]*4
assert all(r['outcome']=='unprocessed' for r in original['results'][4:])
assert preview['resumption']['canApply'] and preview['resumption']['sourceProcessState']=='stopped' and not preview['resumption']['requiresReconciliation']
assert len(preview['resumption']['failedExcluded'])==4
assert [r['requestId'] for r in preview['resumption']['pendingPlan']['requests']]==[r['requestId'] for r in original['results'][4:]]
assert len(resumed['execution']['results'])==4 and all(r['operation']=='ItemReceiptAdd' and r['outcome']=='failed' and r['statusCode']==3250 for r in resumed['execution']['results'])
# Preserve actual resumed statuses for the SDK fixture exporter, without duplicating originals.
(DEST/f'lifecycle-{options.stamp}-po-links-resumed-execution-result.json').write_text(json.dumps(resumed['execution'],indent=2),encoding='utf-8')
plan={'protocolVersion':'13.0','country':'US','onError':'stopOnError','requests':[
    {'requestId':r['requestId'],'operation':'PurchaseOrderQuery','fields':{'TxnID':scalar(r['data'][0],'TxnID'),'IncludeLineItems':True,'IncludeLinkedTxns':True}} for r in orders['results']]}
plan_path=DEST/f'lifecycle-{options.stamp}-po-links-po-state-plan.json'
result_path=DEST/f'lifecycle-{options.stamp}-po-links-po-state-result.json'
# Never repeat this independent read merely to overwrite retained evidence.
if result_path.exists():state=json.loads(result_path.read_text(encoding='utf-8'))
else:
    plan_path.write_text(json.dumps(plan),encoding='utf-8')
    proc=subprocess.run([str(EXE),'sdk','batch','--input',str(plan_path),'--company-file',str(COMPANY),'--json'],capture_output=True,text=True,encoding='utf-8')
    result_path.write_text(proc.stdout,encoding='utf-8');state=json.loads(proc.stdout)
    assert proc.returncode==0
assert state['success'] and len(state['results'])==8 and all(r['outcome']=='succeeded' for r in state['results'])
for outcome in state['results']:
    assert len(outcome['data'])==1;record=outcome['data'][0]
    assert scalar(record,'IsFullyReceived')=='false' and not any(n['name']=='LinkedTxn' for n in record['children'])
    lines=[n for n in record['children'] if n['name']=='PurchaseOrderLineRet'];assert len(lines)==1
    assert scalar(lines[0],'Quantity')=='2' and scalar(lines[0],'ReceivedQuantity')=='0'
preferences=load('preferences-result');assert preferences['success']
inventory=[n for record in preferences['results'][0]['data'] for n in walk(record) if n['name']=='IsUsingInventory']
assert len(inventory)==1 and inventory[0]['value']=='false'
checks=[]
for r in [*original['results'][:4],*resumed['execution']['results']]:
    kind,item,mode=r['requestId'].split('-')
    checks.append({'transactionType':kind,'itemKind':item,'linkMode':mode,'outcome':'failed','statusCode':3250,
        'billableFieldsRequested':False,'isUsingInventoryObserved':False,'classModificationVerified':False,
        'purchaseOrderUnreceivedAndUnlinkedAfterAttempt':True,'resumedProvenUnsubmittedRequest':kind=='ItemReceipt',
        'scope':'Small Test.QBW Bill/ItemReceipt whole-order/line linking controls with non-inventory/inventory items, no billable fields. All returned SDK3250; eight POs remained unreceived/unlinked. IsUsingInventory=false is an observation, not proven cause or universal operation-availability gate. No linked-form/class preservation proof.'})
(DEST/f'lifecycle-{options.stamp}-po-links-verification.json').write_text(json.dumps(checks,indent=2),encoding='utf-8')
print(json.dumps({'rejectedLinkControls':8,'poStatesVerified':8,'inventoryPreference':False,'resumedUnsubmittedReceipts':4}))
