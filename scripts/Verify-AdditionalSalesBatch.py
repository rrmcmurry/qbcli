"""Verify retained additional-item batch boundaries without SDK calls or replay."""
import argparse
import json
import pathlib

ROOT=pathlib.Path(__file__).resolve().parent.parent; DEST=ROOT/'artifacts/fixtures'
parser=argparse.ArgumentParser(description=__doc__); parser.add_argument('stamp'); options=parser.parse_args()
assert len(options.stamp)==6 and options.stamp.isdigit()
def load(label): return json.loads((DEST/f'lifecycle-{options.stamp}-additional-sales-{label}.json').read_text(encoding='utf-8'))
applied=load('apply-result'); proof=load('verification')
assert applied['success'] and applied['execution']['success']
assert pathlib.Path(applied['companyFile']).resolve()==(ROOT/'QBData/Test.QBW').resolve()
records=[json.loads(line) for line in pathlib.Path(applied['execution']['journalFile']).read_text(encoding='utf-8').splitlines()]
submitted=[r for r in records if r['kind']=='submitted']
assert [len(r['requestIds']) for r in submitted]==[5,5,2]
assert len({identity for r in submitted for identity in r['requestIds']})==12
assert len(applied['changes'])==len(applied['verification'])==len(applied['execution']['results'])==12
assert all(r['outcome']=='succeeded' for r in applied['execution']['results'])
assert all(r['classVerified'] and r['otherFieldsPreserved'] and r['postingAccountsAndAmountsPreserved'] for r in applied['verification'])
def leaves(n):
    yield n
    for c in n['children']: yield from leaves(c)
assert sum(len(c['lineIds']) for c in applied['changes'])==48
assert sum(sum(n['name']==c['transactionType']+'LineRet' for n in leaves(c['original'])) for c in applied['changes'])==84
repeat=load('repeat-preview-result')
assert repeat['success'] and repeat['counts']['transactionsToChange']==repeat['counts']['linesToChange']==0
proof.pop('minimumMutationMessageSets',None)
proof.update(mutationMessageSets=3,mutationBatchSizes=[5,5,2],retainedJournalBatchBoundariesVerified=True)
(DEST/f'lifecycle-{options.stamp}-additional-sales-verification.json').write_text(json.dumps(proof,indent=2),encoding='utf-8')
print(json.dumps({'transactions':12,'classes':48,'retainedLeaves':84,'journalBatchSizes':[5,5,2]}))
