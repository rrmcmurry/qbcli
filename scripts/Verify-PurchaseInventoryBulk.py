"""Verify saved public inventory purchase bulk evidence without SDK calls or replay."""
from collections import Counter
import json
import pathlib
import sys

root=pathlib.Path(__file__).resolve().parent.parent
dest=root/'artifacts/fixtures'
stamp=sys.argv[1]; assert len(stamp)==6 and stamp.isdigit()
def load(label):
    v=json.loads((dest/f'lifecycle-{stamp}-purchase-item-{label}-result.json').read_text(encoding='utf-8'))
    assert v['success'];return v
def scalar(n,k):return next((c.get('value') for c in n['children'] if c['name']==k),None)
def ref(n,k):return next((scalar(c,'ListID') for c in n['children'] if c['name']==k),None)
def walk(n):
    yield n
    for c in n['children']:yield from walk(c)
def canonical(n,selected=()):
    return (n['name'],tuple(sorted(n['attributes'].items())),n.get('value'),tuple(canonical(c,selected) for c in n['children']
        if c['name'] not in ('TimeModified','EditSequence') and not(c['name']=='ClassRef' and scalar(n,'TxnLineID') in selected)))
old={r['requestId']:r['data'][0] for r in load('before-forms')['results']}
new={r['requestId']:r['data'][0] for r in load('after-forms')['results']}
before=load('before-postings');after=load('after-postings');assert before['complete'] and after['complete']
applied=load('bulk-apply');preview=load('bulk-preview');repeat=load('bulk-repeat-preview')
assert preview['execution'] is None
assert applied['counts']['transactionsToChange']==6 and applied['counts']['linesToChange']==12
assert repeat['counts']['transactionsToChange']==repeat['counts']['linesToChange']==0
assert len(applied['changes'])==len(applied['execution']['results'])==6
assert all(r['outcome']=='succeeded' for r in applied['execution']['results'])
assert all(v['classVerified'] and v['otherFieldsPreserved'] and v['postingAccountsAndAmountsPreserved'] for v in applied['verification'])
assert canonical(load('before-inventory')['results'][0]['data'][0])==canonical(load('after-inventory')['results'][0]['data'][0])
base={r['requestId']:scalar(r['data'][0],'ListID') for r in load('base')['results']}
journal=[json.loads(line) for line in pathlib.Path(applied['execution']['journalFile']).read_text(encoding='utf-8').splitlines()]
assert [len(r['requestIds']) for r in journal if r['kind']=='submitted']==[3,3]
checks=[]
for change,request in zip(applied['changes'],applied['plan']['requests']):
    kind=change['transactionType'];txn=change['txnId'];selected=set(change['lineIds']);assert len(selected)==2
    body=request['fields'][kind+'Mod'];assert body['TxnID']==txn and body['EditSequence']==change['editSequence']
    assert 'ExpenseLineMod' not in body and len(body['$choices'])==5
    assert canonical(old[kind],selected)==canonical(new[kind],selected)==canonical(change['original'],selected)
    lines={scalar(n,'TxnLineID'):n for n in walk(new[kind]) if n['name']=='ItemLineRet'};assert len(lines)==6
    assert all(ref(lines[line],'ClassRef')==base['Target'] for line in selected)
    oldrows=[n for n in before['data'] if scalar(n,'TxnType')==kind and scalar(n,'TxnID')==txn]
    newrows=[n for n in after['data'] if scalar(n,'TxnType')==kind and scalar(n,'TxnID')==txn]
    assert oldrows and Counter(map(canonical,oldrows))==Counter(map(canonical,newrows))==Counter(map(canonical,change['originalPostings']))
    checks.append({'transactionType':kind,'classesChanged':2,'retainedItemLeaves':6,'expenseLinesUntouched':True,
        'flatAndGroupClassesVerified':True,'unselectedReturnedFormFieldsPreserved':True,'allReturnedPostingFieldsPreserved':True,
        'itemQuantitiesCostsAndDefinitionsPreserved':True,'oneModifierPerTransaction':True,'repeatPreviewHasNoResidualChanges':True,
        'journalMutationBatchSizes':[3,3],'independentRetainedEvidenceVerification':True,
        'scope':'Public inventory purchase bulk on Test.QBW QBD2024R21 US protocol13: actual asset posting selection, flat inventory and mixed noninventory group; credit asset/COGS adjustments preserved. No assemblies, PO links, changed defaults or other releases.'})
(dest/f'lifecycle-{stamp}-purchase-item-inventory-bulk-verification.json').write_text(json.dumps(checks,indent=2),encoding='utf-8')
print(json.dumps({'independentlyVerifiedPurchaseTypes':len(checks),'classesChanged':12}))
