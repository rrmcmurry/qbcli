"""Verify retained inventory default-change evidence; no SDK calls or mutations."""
import argparse
import json
import pathlib
from collections import Counter

ROOT=pathlib.Path(__file__).resolve().parent.parent;DEST=ROOT/'artifacts/fixtures'
parser=argparse.ArgumentParser(description=__doc__);parser.add_argument('fixture_stamp');parser.add_argument('probe_stamp');options=parser.parse_args()
assert all(len(s)==6 and s.isdigit() for s in [options.fixture_stamp,options.probe_stamp])
def load(stamp,label):return json.loads((DEST/f'lifecycle-{stamp}-inventory-sales-{label}.json').read_text(encoding='utf-8'))
def leaf(n,key):return next((c.get('value') for c in n['children'] if c['name']==key),None)
def account(n):
    a=next((c for c in n['children'] if c['name']=='AccountRef'),None)
    return leaf(a,'ListID') if a else None
base=load(options.fixture_stamp,'base-result');seed=load(options.fixture_stamp,'seed-result')
ids={r['requestId']:leaf(r['data'][0],'ListID') for r in base['results']}
txns={r['requestId']:leaf(r['data'][0],'TxnID') for r in seed['results']}
before=load(options.probe_stamp,'original-postings');after=load(options.probe_stamp,'changed-default-postings')
changed=load(options.probe_stamp,'change-defaults-result');plan=load(options.probe_stamp,'change-defaults-plan')
assert before['success'] and before['complete'] and after['success'] and after['complete'] and changed['success']
body=plan['requests'][0]['fields']['ItemInventoryMod']
assert body['ApplyIncomeAccountRefToExistingTxns'] is False and body['ApplyCOGSAccountRefToExistingTxns'] is False
assert len(changed['results'])==1 and changed['results'][0]['outcome']=='succeeded'
checks=[]
for kind,txn in txns.items():
    old=[r for r in before['data'] if leaf(r,'TxnID')==txn and leaf(r,'TxnType')==kind]
    new=[r for r in after['data'] if leaf(r,'TxnID')==txn and leaf(r,'TxnType')==kind]
    assert len(old)==len(new)==12
    amounts=lambda rows:Counter((leaf(r,'TxnLineID'),leaf(r,'Amount')) for r in rows)
    assert amounts(old)==amounts(new)
    filtered=lambda rows,allowed:Counter((leaf(r,'TxnLineID'),account(r),leaf(r,'Amount')) for r in rows if account(r) in allowed)
    assert filtered(old,[ids['Income'],ids['Override'],ids['Asset']])==filtered(new,[ids['Income'],ids['Override'],ids['Asset']])
    old_cogs=[r for r in old if account(r)==ids['COGS']];new_cogs=[r for r in new if account(r)==ids['NewCOGS']]
    assert len(old_cogs)==len(new_cogs)==3 and amounts(old_cogs)==amounts(new_cogs)
    assert not any(account(r)==ids['COGS'] for r in new)
    checks.append({'transactionType':kind,'postingRows':len(old),'cogsPostingAccountReferencesChanged':3,
        'incomeAndAssetPostingReferencesPreserved':True,'postingAmountsAndLineIdentitiesPreserved':True,
        'sdkModAcceptedWithBothApplyToExistingFlagsFalse':True,'historicalCogsAccountReferencesPreserved':False,
        'classModificationPerformed':False,'scope':'Retained TransactionQuery reads on Test.QBW inventory fixtures. SDK COGS account references changed after ItemInventoryMod; no independent before/after ledger-report comparison or class-preservation claim.'})
(DEST/f'lifecycle-{options.probe_stamp}-inventory-defaults-verification.json').write_text(json.dumps(checks,indent=2),encoding='utf-8')
print(json.dumps(checks),flush=True)
