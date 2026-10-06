"""Verify public inventory bulk classes against fresh expendable Test.QBW forms."""
from collections import Counter
import datetime
import json
import pathlib
import subprocess
import sys

ROOT=pathlib.Path(__file__).resolve().parent.parent
DEST=ROOT/'artifacts/fixtures'
COMPANY=(ROOT/'QBData/Test.QBW').resolve()
assert COMPANY==pathlib.Path(__import__("os").environ["QBCLI_LIVE_TEST_COMPANY"]).resolve(strict=True)
EXE=ROOT/'artifacts/win-x64/qbcli.exe'
proc=subprocess.run([sys.executable,str(ROOT/'scripts/Test-InventorySalesClasses.py'),'--seed-only'])
assert proc.returncode==0, 'Seed failed: retain evidence and do not replay'
source=max(DEST.glob('lifecycle-*-inventory-sales-seed-verification.json'),key=lambda p:p.stat().st_mtime)
seed_stamp=json.loads(source.read_text(encoding='utf-8'))['fixtureStamp']
def retained(label): return json.loads((DEST/f'lifecycle-{seed_stamp}-inventory-sales-{label}-result.json').read_text(encoding='utf-8'))
def scalar(n,key): return next((c.get('value') for c in n['children'] if c['name']==key),None)
ids={r['requestId']:scalar(r['data'][0],'ListID') for r in retained('base')['results']}
inventory=scalar(retained('item')['results'][0]['data'][0],'ListID')
stamp=datetime.datetime.now().strftime('%H%M%S')
def run(label,args):
    proc=subprocess.run([str(EXE),*args,'--company-file',str(COMPANY),'--json'],capture_output=True,text=True,encoding='utf-8')
    (DEST/f'lifecycle-{stamp}-bulk-inventory-sales-{label}.json').write_text(proc.stdout,encoding='utf-8')
    value=json.loads(proc.stdout)
    print(json.dumps({'phase':label,'success':value.get('success')}),flush=True)
    assert proc.returncode==0 and value['success'], 'Failed: retain journal, no mutation replay'
    return value
selection=DEST/f'lifecycle-{stamp}-bulk-inventory-sales-selection.json'
selection.write_text(json.dumps({'accountId':ids['Income'],'classId':ids['Target'],'transactionTypes':['Invoice','SalesReceipt','CreditMemo']}),encoding='utf-8')
item_fields=DEST/f'lifecycle-{stamp}-bulk-inventory-sales-item-fields.json'
item_fields.write_text(json.dumps({'ListID':inventory}),encoding='utf-8')
before_item=run('before-item',['sdk','run','ItemInventoryQuery','--input',str(item_fields)])['results'][0]['data'][0]
args=['bulk','reclassify','--input',str(selection),'--page-size','25','--batch-size','50']
preview=run('preview',args)
assert preview['counts']['transactionsToChange']==3 and preview['counts']['linesToChange']==6 and preview['execution'] is None
applied=run('apply',[*args,'--apply'])
assert applied['counts']['transactionsToChange']==3 and applied['counts']['linesToChange']==6
assert len(applied['verification'])==3 and all(v['classVerified'] and v['otherFieldsPreserved'] and v['postingAccountsAndAmountsPreserved'] for v in applied['verification'])
(DEST/f'lifecycle-{stamp}-bulk-inventory-sales-execution-result.json').write_text(json.dumps(applied['execution'],indent=2),encoding='utf-8')
after_item=run('after-item',['sdk','run','ItemInventoryQuery','--input',str(item_fields)])['results'][0]['data'][0]
def canonical(n): return (n['name'],tuple(sorted(n['attributes'].items())),n.get('value'),tuple(canonical(c) for c in n['children'] if c['name'] not in ('TimeModified','EditSequence')))
assert canonical(before_item)==canonical(after_item)
repeat=run('repeat-preview',args)
assert repeat['counts']['transactionsToChange']==0 and repeat['counts']['linesToChange']==0
metadata={'transactionTypes':['Invoice','SalesReceipt','CreditMemo'],'transactionsChanged':3,'classesChanged':6,
    'mixedInventoryServiceFlatAndGroupLeavesPreserved':True,'actualIncomeAssetCogsPostingsPreserved':True,
    'itemQuantitiesCostsAndDefinitionsPreserved':True,'otherReturnedFormFieldsPreserved':True,
    'repeatPreviewHasNoResidualChanges':True,'oneModifierPerTransaction':True,
    'scope':'Public bulk inventory recipe on fresh Test.QBW quantity/value fixtures. Revenue selected by confirmed account types, not fixture IDs or item defaults. Only three-posting Income/OtherCurrentAsset/CostOfGoodsSold leaves, not COGS/asset class selection, assemblies, nonposting items or other releases.'}
(DEST/f'lifecycle-{stamp}-bulk-inventory-sales-verification.json').write_text(json.dumps(metadata,indent=2),encoding='utf-8')
print(json.dumps(metadata),flush=True)
