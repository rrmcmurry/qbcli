"""Independently verify retained payment-item public bulk evidence, without replay."""
from collections import Counter
import json
import pathlib
import sys
root=pathlib.Path(__file__).resolve().parent.parent;dest=root/'artifacts/fixtures'
stamp=sys.argv[1];assert len(stamp)==6 and stamp.isdigit()
def load(label):
    v=json.loads((dest/f'lifecycle-{stamp}-payment-sales-{label}-result.json').read_text(encoding='utf-8'));assert v['success'];return v
def scalar(n,k):return next((c.get('value') for c in n['children'] if c['name']==k),None)
def ref(n,k):return next((scalar(c,'ListID') for c in n['children'] if c['name']==k),None)
def canonical(n,selected=()):return (n['name'],tuple(sorted(n['attributes'].items())),n.get('value'),tuple(canonical(c,selected) for c in n['children']
    if c['name'] not in ('TimeModified','EditSequence') and not(c['name']=='ClassRef' and scalar(n,'TxnLineID') in selected)))
old={r['requestId']:r['data'][0] for r in load('before-forms')['results']};new={r['requestId']:r['data'][0] for r in load('after-forms')['results']}
before=load('before-postings');after=load('after-postings');assert before['complete'] and after['complete']
base={r['requestId']:scalar(r['data'][0],'ListID') for r in load('base')['results']}
items={r['requestId']:scalar(r['data'][0],'ListID') for r in load('items')['results']}
applied=load('bulk-apply');preview=load('bulk-preview');repeat=load('bulk-repeat-preview')
assert preview['execution'] is None and applied['counts']['transactionsToChange']==3 and applied['counts']['linesToChange']==3
assert repeat['counts']['transactionsToChange']==repeat['counts']['linesToChange']==0
assert len(applied['changes'])==len(applied['execution']['results'])==3 and all(r['outcome']=='succeeded' for r in applied['execution']['results'])
assert all(v['classVerified'] and v['otherFieldsPreserved'] and v['postingAccountsAndAmountsPreserved'] for v in applied['verification'])
journal=[json.loads(line) for line in pathlib.Path(applied['execution']['journalFile']).read_text(encoding='utf-8').splitlines()]
assert [len(r['requestIds']) for r in journal if r['kind']=='submitted']==[2,1]
checks=[]
for change,request in zip(applied['changes'],applied['plan']['requests']):
    kind=change['transactionType'];txn=change['txnId'];selected=set(change['lineIds']);assert len(selected)==1
    body=request['fields'][kind+'Mod'];assert body['TxnID']==txn and body['EditSequence']==change['editSequence'] and len(body['$choices'])==3
    lines=[n for n in old[kind]['children'] if n['name']==kind+'LineRet'];assert len(lines)==3
    for line,choice in zip(lines,body['$choices']):
        mod=choice[kind+'LineMod'];assert mod['TxnLineID']==scalar(line,'TxnLineID')
        if ref(line,'ItemRef')==items['payment']:
            assert mod['TxnLineID'] in selected and mod['OverrideItemAccountRef']['ListID']==base['bank'] and mod['ClassRef']['ListID']==base['class']
        else:
            assert mod['TxnLineID'] not in selected and mod['OverrideItemAccountRef']['ListID']==base['income'] and 'ClassRef' not in mod
    assert canonical(old[kind],selected)==canonical(new[kind],selected)==canonical(change['original'],selected)
    returned={scalar(n,'TxnLineID'):n for n in new[kind]['children'] if n['name']==kind+'LineRet'}
    assert all(ref(returned[x],'ClassRef')==base['class'] for x in selected)
    oldrows=[n for n in before['data'] if scalar(n,'TxnType')==kind and scalar(n,'TxnID')==txn];newrows=[n for n in after['data'] if scalar(n,'TxnType')==kind and scalar(n,'TxnID')==txn]
    assert oldrows and Counter(map(canonical,oldrows))==Counter(map(canonical,newrows))==Counter(map(canonical,change['originalPostings']))
    checks.append({'transactionType':kind,'classesChanged':1,'retainedLeaves':3,'paymentClassVerified':True,
        'allOtherReturnedFormFieldsPreserved':True,'allReturnedPostingFieldsPreserved':True,'publicBulkVerified':True,
        'repeatPreviewHasNoResidualChanges':True,'journalMutationBatchSizes':[2,1],'independentRetainedEvidenceVerification':True,
        'scope':'Public bulk flat service/payment recipe on Test.QBW QBD2024R21 US protocol13. No salestax items, groups or other releases proof.'})
(dest/f'lifecycle-{stamp}-payment-sales-bulk-verification.json').write_text(json.dumps(checks,indent=2),encoding='utf-8')
print(json.dumps({'independentlyVerifiedSalesTypes':len(checks),'classesChanged':3}))
