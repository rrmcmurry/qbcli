"""Inspect retained unbounded SDK rows for posting-key collisions; no SDK calls."""
import argparse
from collections import defaultdict
import json
import pathlib
ROOT=pathlib.Path(__file__).resolve().parent.parent;DEST=ROOT/'artifacts/fixtures'
parser=argparse.ArgumentParser(description=__doc__);parser.add_argument('stamp');options=parser.parse_args()
assert len(options.stamp)==6 and options.stamp.isdigit()
value=json.loads((DEST/f'lifecycle-{options.stamp}-purchase-item-unbounded-result.json').read_text(encoding='utf-8'))
assert value['success'] and value['results'][0]['outcome']=='succeeded'
def scalar(n,key):return next((c.get('value') for c in n['children'] if c['name']==key),None)
def account(n):
    ref=next((c for c in n['children'] if c['name']=='AccountRef'),None)
    return scalar(ref,'ListID') if ref else None
groups=defaultdict(list)
for row in value['results'][0]['data']:groups[(scalar(row,'TxnType'),scalar(row,'TxnID'),scalar(row,'TxnLineID'),account(row))].append(row)
seed=json.loads((DEST/f'lifecycle-{options.stamp}-purchase-item-seed-result.json').read_text(encoding='utf-8'))
fixture_ids={scalar(r['data'][0],'TxnID') for r in seed['results']}
checks=[]
for key,rows in groups.items():
    if len(rows)<2:continue
    def children(row):return {n['name']:json.dumps(n,sort_keys=True) for n in row['children']}
    fields=[children(row) for row in rows]
    differing=[name for name in set().union(*(set(f) for f in fields)) if len({f.get(name) for f in fields})>1]
    checks.append({'transactionType':key[0],'rowsSharingPostingKey':len(rows),'belongsToFreshInventoryPurchaseFixture':key[1] in fixture_ids,
        'differingReturnedFieldNames':sorted(differing),'distinctFullRecords':len({json.dumps(row,sort_keys=True) for row in rows})})
metadata={'unboundedReturnedRows':len(value['results'][0]['data']),'collisions':checks,
    'scope':'Independent retained SDK query, six purchase transaction types on current Test.QBW. Posting key TxnType/TxnID/TxnLineID/AccountRef ListID is not unique for every row. No class changes or universal identity rule established.'}
(DEST/f'lifecycle-{options.stamp}-purchase-posting-identity-verification.json').write_text(json.dumps(metadata,indent=2),encoding='utf-8')
print(json.dumps(metadata))
