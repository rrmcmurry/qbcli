"""Independently verify saved combined purchase bulk results without replay."""
from collections import Counter
import argparse
import json
import pathlib

ROOT=pathlib.Path(__file__).resolve().parent.parent; DEST=ROOT/'artifacts/fixtures'
parser=argparse.ArgumentParser(description=__doc__);parser.add_argument('stamp');parser.add_argument('--linked-billable',action='store_true');parser.add_argument('--billable-only',action='store_true');options=parser.parse_args()
assert not(options.linked_billable and options.billable_only)
assert len(options.stamp)==6 and options.stamp.isdigit()
def load(label):
    value=json.loads((DEST/f'lifecycle-{options.stamp}-purchase-item-{label}-result.json').read_text(encoding='utf-8'))
    assert value['success']; return value
def scalar(n,key):
    values=[c for c in n['children'] if c['name']==key];assert len(values)<=1
    return values[0].get('value') if values else None
def reference(n,key):
    values=[c for c in n['children'] if c['name']==key];assert len(values)<=1
    return scalar(values[0],'ListID') if values else None
def canonical(n,selected=()):
    return (n['name'],tuple(sorted(n['attributes'].items())),n.get('value'),tuple(canonical(c,selected) for c in n['children']
        if c['name'] not in ('TimeModified','EditSequence') and not(c['name']=='ClassRef' and scalar(n,'TxnLineID') in selected)))
def walk(n):
    yield n
    for c in n['children']:yield from walk(c)
base=load('base'); target=next(scalar(r['data'][0],'ListID') for r in base['results'] if r['requestId']=='Target')
old={r['requestId']:r['data'][0] for r in load('before-forms')['results']}
new={r['requestId']:r['data'][0] for r in load('after-forms')['results']}
old_posts=load('before-postings');new_posts=load('after-postings')
assert old_posts['complete'] and new_posts['complete']
applied=load('bulk-apply');repeat=load('bulk-repeat-preview')
assert applied['counts']['transactionsToChange']==6 and applied['counts']['linesToChange']==30
assert len(applied['changes'])==len(applied['execution']['results'])==6 and all(r['outcome']=='succeeded' for r in applied['execution']['results'])
assert all(v['classVerified'] and v['otherFieldsPreserved'] and v['postingAccountsAndAmountsPreserved'] for v in applied['verification'])
assert repeat['counts']['transactionsToChange']==repeat['counts']['linesToChange']==0
records=[json.loads(line) for line in pathlib.Path(applied['execution']['journalFile']).read_text(encoding='utf-8').splitlines()]
submitted=[r for r in records if r['kind']=='submitted'];assert [len(r['requestIds']) for r in submitted]==[3,3]
assert len({identity for r in submitted for identity in r['requestIds']})==6
checks=[]
for change,request in zip(applied['changes'],applied['plan']['requests']):
    kind=change['transactionType'];txn=change['txnId'];selected=set(change['lineIds']);assert len(selected)==5
    body=request['fields'][kind+'Mod'];assert body['TxnID']==txn and body['EditSequence']==change['editSequence']
    assert len(body['ExpenseLineMod'])==2 and len(body['$choices'])==5
    assert canonical(old[kind],selected)==canonical(new[kind],selected)==canonical(change['original'],selected)
    lines={scalar(n,'TxnLineID'):n for n in walk(new[kind]) if n['name'] in ('ExpenseLineRet','ItemLineRet')}
    assert len(lines)==8 and sum(n['name']=='ItemLineRet' for n in lines.values())==6
    assert all(reference(lines[line],'ClassRef')==target for line in selected)
    assert sum(lines[line]['name']=='ExpenseLineRet' for line in selected)==1
    before=[n for n in old_posts['data'] if scalar(n,'TxnType')==kind and scalar(n,'TxnID')==txn]
    after=[n for n in new_posts['data'] if scalar(n,'TxnType')==kind and scalar(n,'TxnID')==txn]
    assert before and Counter(map(canonical,before))==Counter(map(canonical,after))==Counter(map(canonical,change['originalPostings']))
    checks.append({'transactionType':kind,'classesChanged':5,'retainedItemLeaves':6,'retainedExpenseLines':2,
        'simultaneousExpenseAndItemClassesVerified':True,'unselectedReturnedFormFieldsPreserved':True,
        'allReturnedPostingFieldsPreserved':True,'oneModifierPerTransaction':True,'repeatPreviewHasNoResidualChanges':True,
        'journalMutationBatchSizes':[3,3],'independentRetainedEvidenceVerification':True,
        'scope':'Public combined purchase item/expense bulk on fresh Test.QBW service/non-inventory/other-charge flat/group fixtures with overrides and existing classes. No inventory, PO links, billable/customer allocation or other releases.'})
if options.linked_billable or options.billable_only:
    old_orders={r['requestId']:r['data'][0] for r in load('before-orders')['results']} if options.linked_billable else {}
    new_orders={r['requestId']:r['data'][0] for r in load('after-orders')['results']} if options.linked_billable else {}
    if options.linked_billable:assert set(old_orders)==set(new_orders)=={'Bill','ItemReceipt'}
    for kind,record in old.items():
        flat=[n for n in record['children'] if n['name'] in ('ItemLineRet','ExpenseLineRet')]
        assert len(flat)==6 and all(reference(n,'CustomerRef') is not None and scalar(n,'BillableStatus')=='Billable' for n in flat)
        if kind in old_orders:
            po=old_orders[kind];assert canonical(po)==canonical(new_orders[kind])
            assert scalar(po,'IsFullyReceived')=='false'
            po_lines=[n for n in po['children'] if n['name']=='PurchaseOrderLineRet']
            assert len(po_lines)==1 and scalar(po_lines[0],'Quantity')=='2' and scalar(po_lines[0],'ReceivedQuantity')=='1'
            assert any(scalar(link,'TxnID')==scalar(po,'TxnID') for link in record['children'] if link['name']=='LinkedTxn')
            assert any(scalar(link,'TxnID')==scalar(record,'TxnID') for link in po['children'] if link['name']=='LinkedTxn')
    for check in checks:
        linked=check['transactionType'] in old_orders
        check.update(customerAndBillableFieldsPreserved=True,purchaseOrderLinked=linked,
            purchaseOrderReturnedFieldsAndReceivingStatePreserved=True if linked else None,
            scope='Public combined purchase fixture with Billable customer allocations on flat items/expense lines. Independent verification of returned customer/billable fields and form/posting fields, plus both PO link directions and receiving state if linked. No already-billed, billable group-child, inventory, every PO link shape or other-release proof.')
suffix='linked-billable' if options.linked_billable else 'billable' if options.billable_only else 'bulk'
(DEST/f'lifecycle-{options.stamp}-purchase-item-{suffix}-verification.json').write_text(json.dumps(checks,indent=2),encoding='utf-8')
print(json.dumps(checks))
