"""Fresh service-item sales bulk test on expendable Test.QBW; no backups/restores."""
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
# A new fixture is necessary: the previous proof already classified its target lines.
seed=subprocess.run([sys.executable,str(ROOT/'scripts/Test-SalesPostings.py'),'--executable',str(EXE),'--test-item-account-change'])
assert seed.returncode==0, 'Fixture did not finish successfully; do not replay writes'
proof=max(DEST.glob('lifecycle-*-sales-posting-verification.json'),key=lambda p:p.stat().st_mtime)
source_stamp=proof.name.split('-')[1]
assert all(v['historicalPostingsPreservedAfterItemDefaultChange'] for v in json.loads(proof.read_text(encoding='utf-8')))
base=json.loads((DEST/f'lifecycle-{source_stamp}-sales-posting-base-result.json').read_text(encoding='utf-8'))
def leaf(n,key):
    return next((c.get('value') for c in n['children'] if c['name']==key),None)
ids={r['requestId']:leaf(r['data'][0],'ListID') for r in base['results']}
stamp=datetime.datetime.now().strftime('%H%M%S')
selection=DEST/f'lifecycle-{stamp}-bulk-sales-selection.json'
selection.write_text(json.dumps({'accountId':ids['original'],'classId':ids['class'],'transactionTypes':['Invoice','SalesReceipt','CreditMemo']}),encoding='utf-8')
def run(label,apply=False):
    proc=subprocess.run([str(EXE),'bulk','reclassify','--input',str(selection),'--company-file',str(COMPANY),'--page-size','25','--batch-size','50',*(['--apply'] if apply else [])],capture_output=True,text=True,encoding='utf-8')
    (DEST/f'lifecycle-{stamp}-bulk-sales-{label}.json').write_text(proc.stdout,encoding='utf-8')
    value=json.loads(proc.stdout)
    assert proc.returncode==0 and value['success'], 'Bulk failed; retain journal and do not replay'
    return value
preview=run('preview')
assert preview['counts']['transactionsToChange']==3 and preview['counts']['linesToChange']==6
assert preview['execution'] is None and preview['coverageCompleteForAllAccountPostings'] is False
applied=run('apply',True)
assert len(applied['verification'])==3 and applied['execution']['success']
assert all(v['classVerified'] and v['otherFieldsPreserved'] and v['postingAccountsAndAmountsPreserved'] for v in applied['verification'])
assert applied['counts']['transactionsToChange']==3 and applied['counts']['linesToChange']==6
(DEST/f'lifecycle-{stamp}-bulk-sales-execution-result.json').write_text(json.dumps(applied['execution'],indent=2),encoding='utf-8')
repeat=run('repeat-preview')
assert repeat['counts']['transactionsToChange']==0 and repeat['counts']['linesToChange']==0
metadata={'transactionTypes':['Invoice','SalesReceipt','CreditMemo'],'transactionsChanged':3,'classesChanged':6,
    'flatAndGroupLeavesPreserved':True,'actualPostingAccountsAndAmountsPreserved':True,'otherReturnedFormFieldsPreserved':True,
    'changedItemDefaultFixture':True,'repeatPreviewHasNoResidualChanges':True,'oneModifierPerTransaction':True,
    'scope':'Opt-in bulk service-item recipe on fresh Test.QBW fixtures with actual posting-account overrides. Inventory/COGS, nonposting items, multiple postings per leaf and other releases remain unverified.'}
(DEST/f'lifecycle-{stamp}-bulk-sales-verification.json').write_text(json.dumps(metadata,indent=2),encoding='utf-8')
print(json.dumps(metadata),flush=True)
