"""Fresh non-inventory/other-charge bulk fixtures only on expendable Test.QBW."""
import datetime
import argparse
import json
import pathlib
import subprocess

ROOT=pathlib.Path(__file__).resolve().parent.parent
DEST=ROOT/'artifacts/fixtures'; EXE=ROOT/'artifacts/win-x64/qbcli.exe'
COMPANY=(ROOT/'QBData/Test.QBW').resolve()
assert COMPANY==pathlib.Path(__import__("os").environ["QBCLI_LIVE_TEST_COMPANY"]).resolve(strict=True)
STAMP=datetime.datetime.now().strftime('%H%M%S'); PREFIX='MOREITEM'+STAMP
parser=argparse.ArgumentParser(description=__doc__)
parser.add_argument('--existing-base',help='Reuse confirmed base after item names failed local validation before SDK submission.')
options=parser.parse_args()
if options.existing_base:
    assert len(options.existing_base)==6 and options.existing_base.isdigit()
    previous=json.loads((DEST/f'lifecycle-{options.existing_base}-additional-sales-items-result.json').read_text(encoding='utf-8'))
    assert previous.get('success') is False and previous.get('error',{}).get('code')=='invalid_arguments' and not previous.get('results')
TYPES=['Invoice','SalesReceipt','CreditMemo']
def scalar(n,key): return next((c.get('value') for c in n['children'] if c['name']==key),None)
def run(label,args):
    proc=subprocess.run([str(EXE),*args,'--company-file',str(COMPANY),'--json'],capture_output=True,text=True,encoding='utf-8')
    (DEST/f'lifecycle-{STAMP}-additional-sales-{label}-result.json').write_text(proc.stdout,encoding='utf-8')
    value=json.loads(proc.stdout)
    print(json.dumps({'phase':label,'success':value.get('success'),'outcomes':[{k:r.get(k) for k in ('operation','outcome','statusCode')} for r in value.get('results',[])]}),flush=True)
    assert proc.returncode==0 and value['success'], 'Keep result/journal and do not replay'
    return value
def call(operation,body,identity): return {'requestId':identity,'operation':operation,'fields':{operation:body}}
def batch(label,requests,apply=False):
    path=DEST/f'lifecycle-{STAMP}-additional-sales-{label}-plan.json'
    path.write_text(json.dumps({'protocolVersion':'13.0','country':'US','onError':'stopOnError','requests':requests}),encoding='utf-8')
    return run(label,['sdk','batch','--input',str(path),*(['--apply'] if apply else [])])
if options.existing_base:
    base=json.loads((DEST/f'lifecycle-{options.existing_base}-additional-sales-base-result.json').read_text(encoding='utf-8'))
    assert base['success'] and len(base['results'])==5 and all(r['outcome']=='succeeded' for r in base['results'])
else:
    base=batch('base',[call('AccountAdd',{'Name':PREFIX+' '+name,'AccountType':kind},name) for name,kind in [('Income','Income'),('Override','Income'),('Expense','Expense')]]+
        [call('ClassAdd',{'Name':PREFIX+' '+name},name) for name in ['Target','Existing']],True)
ids={r['requestId']:scalar(r['data'][0],'ListID') for r in base['results']}
requests=[]
for kind in ['ItemNonInventory','ItemOtherCharge']:
    for dual in [False,True]:
        identity=kind+('Dual' if dual else 'Single')
        fields={'Name':PREFIX+' '+('NI' if kind=='ItemNonInventory' else 'OC')+(' Dual' if dual else ' Single')}
        if dual:
            fields['SalesAndPurchase']={'SalesPrice':'3.00','IncomeAccountRef':{'ListID':ids['Income']},'PurchaseCost':'2.00','ExpenseAccountRef':{'ListID':ids['Expense']}}
        else: fields['SalesOrPurchase']={'Price':'3.00','AccountRef':{'ListID':ids['Income']}}
        requests.append(call(kind+'Add',fields,identity))
items=batch('items',requests,True)
item_ids={r['requestId']:scalar(r['data'][0],'ListID') for r in items['results']}
group=batch('group',[call('ItemGroupAdd',{'Name':PREFIX+' Group','IsPrintItemsInGroup':True,'ItemGroupLine':[
    {'ItemRef':{'ListID':item_ids['ItemNonInventoryDual']},'Quantity':'1'},
    {'ItemRef':{'ListID':item_ids['ItemOtherChargeDual']},'Quantity':'1'},
    {'ItemRef':{'FullName':'SDK154955 Service'},'Quantity':'1'}]},'group')],True)
group_id=scalar(group['results'][0]['data'][0],'ListID')
requests=[]
for kind in TYPES:
    for number in range(4):
        fields={'CustomerRef':{'FullName':'SDK154955 Customer'},'TxnDate':'2026-10-05','Memo':PREFIX+' '+kind+' '+str(number),
            kind+'LineAdd':[
                {'ItemRef':{'ListID':item_ids['ItemNonInventorySingle']},'Amount':'11.00'},
                {'ItemRef':{'ListID':item_ids['ItemOtherChargeSingle']},'Amount':'12.00'},
                {'ItemRef':{'ListID':item_ids['ItemNonInventoryDual']},'Amount':'13.00','ClassRef':{'ListID':ids['Existing']},'OverrideItemAccountRef':{'ListID':ids['Override']}},
                {'ItemRef':{'ListID':item_ids['ItemOtherChargeDual']},'Amount':'14.00','OverrideItemAccountRef':{'ListID':ids['Override']}}],
            kind+'LineGroupAdd':{'ItemGroupRef':{'ListID':group_id},'Quantity':'1'}}
        fields['DepositToAccountRef' if kind=='SalesReceipt' else 'ARAccountRef']={'FullName':'SDK154955 Bank' if kind=='SalesReceipt' else 'SDK154955 AR'}
        requests.append(call(kind+'Add',fields,kind+str(number)))
batch('seed',requests,True)
def item_query(label):
    return batch(label,[{'requestId':kind,'operation':kind+'Query','fields':{'ListID':[item_ids[k] for k in item_ids if k.startswith(kind)]}} for kind in ['ItemNonInventory','ItemOtherCharge']])
before=item_query('before-items')
selection=DEST/f'lifecycle-{STAMP}-additional-sales-selection.json'
selection.write_text(json.dumps({'accountId':ids['Income'],'classId':ids['Target'],'transactionTypes':TYPES}),encoding='utf-8')
args=['bulk','reclassify','--input',str(selection),'--page-size','25','--batch-size','5']
preview=run('preview',args)
assert preview['counts']['transactionsToChange']==12 and preview['counts']['linesToChange']==48 and preview['execution'] is None
applied=run('apply',[*args,'--apply'])
assert applied['counts']['transactionsToChange']==12 and applied['counts']['linesToChange']==48
assert len(applied['verification'])==12 and all(v['classVerified'] and v['otherFieldsPreserved'] and v['postingAccountsAndAmountsPreserved'] for v in applied['verification'])
records=[json.loads(line) for line in pathlib.Path(applied['execution']['journalFile']).read_text(encoding='utf-8').splitlines()]
submitted=[record for record in records if record['kind']=='submitted']
assert [len(record['requestIds']) for record in submitted]==[5,5,2]
assert len({identity for record in submitted for identity in record['requestIds']})==12
(DEST/f'lifecycle-{STAMP}-additional-sales-execution-result.json').write_text(json.dumps(applied['execution'],indent=2),encoding='utf-8')
after=item_query('after-items')
def canonical(n): return (n['name'],tuple(sorted(n['attributes'].items())),n.get('value'),tuple(canonical(c) for c in n['children'] if c['name'] not in ('TimeModified','EditSequence')))
assert [[canonical(n) for n in r['data']] for r in before['results']]==[[canonical(n) for n in r['data']] for r in after['results']]
repeat=run('repeat-preview',args)
assert repeat['counts']['transactionsToChange']==0 and repeat['counts']['linesToChange']==0
metadata={'transactionTypes':TYPES,'itemKinds':['ItemNonInventory','ItemOtherCharge'],'itemDefinitions':['SalesOrPurchase','SalesAndPurchase'],
    'transactionsChanged':12,'classesChanged':48,'retainedLeafLines':84,'batchSize':5,'mutationMessageSets':len(submitted),'mutationBatchSizes':[len(record['requestIds']) for record in submitted],
    'actualPostingAccountsAndAmountsPreserved':True,'otherReturnedFormFieldsPreserved':True,'itemDefinitionsPreserved':True,
    'flatAndMixedGroupChildrenPreserved':True,'repeatPreviewHasNoResidualChanges':True,
    'scope':'Fresh Test.QBW public bulk fixtures with non-inventory/other-charge sales lines, shared/separate sales-purchase definitions, overrides, existing classes and mixed service groups. Only one confirmed actual posting per retained leaf. No purchase-line recipe, item-default-change, nonposting, other releases or universal item coverage claim.'}
(DEST/f'lifecycle-{STAMP}-additional-sales-verification.json').write_text(json.dumps(metadata,indent=2),encoding='utf-8')
print(json.dumps(metadata),flush=True)
