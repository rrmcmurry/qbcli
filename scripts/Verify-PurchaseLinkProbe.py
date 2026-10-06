"""Record exact rejected linked/billable probe scope without mutation or replay."""
import argparse
import json
import pathlib
ROOT=pathlib.Path(__file__).resolve().parent.parent;DEST=ROOT/'artifacts/fixtures'
parser=argparse.ArgumentParser(description=__doc__);parser.add_argument('stamp');options=parser.parse_args()
assert len(options.stamp)==6 and options.stamp.isdigit()
def load(label):return json.loads((DEST/f'lifecycle-{options.stamp}-purchase-item-{label}.json').read_text(encoding='utf-8'))
seed=load('seed-result');plan=load('seed-plan');orders=load('purchase-orders-result')
assert orders['success'] and len(orders['results'])==2 and all(r['outcome']=='succeeded' for r in orders['results'])
assert seed['success'] is False
assert [(r['operation'],r['outcome'],r['statusCode']) for r in seed['results'][:3]]==[('CheckAdd','succeeded',0),('BillAdd','failed',3250),('CreditCardChargeAdd','unprocessed',3231)]
assert all(r['outcome']=='unprocessed' for r in seed['results'][3:])
body=next(r['fields']['BillAdd'] for r in plan['requests'] if r['operation']=='BillAdd')
assert body['ItemLineAdd'][0]['LinkToTxn'] and 'ItemRef' not in body['ItemLineAdd'][0]
assert all(line['BillableStatus']=='Billable' and line['CustomerRef'] for line in body['ItemLineAdd']+body['ExpenseLineAdd'])
assert not (DEST/f'lifecycle-{options.stamp}-purchase-item-bulk-apply-result.json').exists()
metadata={'operation':'BillAdd','outcome':'failed','statusCode':3250,'purchaseOrdersAdded':2,'unlinkedBillableCheckAdded':True,
    'successfulLinkedPurchaseForm':False,'classModificationPerformed':False,
    'scope':'Exact Test.QBW BillAdd combination: linked non-inventory PO line with Billable customer allocations, other item/group/expense lines. SDK feature unavailable; no linked-form preservation evidence. Does not isolate the failing field or prove all PO linking unavailable. Later unprocessed requests were not replayed by this verifier.'}
(DEST/f'lifecycle-{options.stamp}-purchase-link-probe-verification.json').write_text(json.dumps(metadata,indent=2),encoding='utf-8')
print(json.dumps(metadata))
