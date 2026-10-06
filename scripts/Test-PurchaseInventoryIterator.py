"""Compare inventory-credit iterator rows/projections with an independent SDK read."""
from collections import Counter
import argparse
import json
import pathlib
import subprocess
ROOT=pathlib.Path(__file__).resolve().parent.parent;DEST=ROOT/'artifacts/fixtures';EXE=ROOT/'artifacts/win-x64/qbcli.exe'
COMPANY=(ROOT/'QBData/Test.QBW').resolve();assert COMPANY==pathlib.Path(__import__("os").environ["QBCLI_LIVE_TEST_COMPANY"]).resolve(strict=True)
parser=argparse.ArgumentParser(description=__doc__);parser.add_argument('stamp');options=parser.parse_args()
assert len(options.stamp)==6 and options.stamp.isdigit()
def scalar(n,key):return next((c.get('value') for c in n['children'] if c['name']==key),None)
def canonical(n):return json.dumps(n,sort_keys=True,separators=(',',':'))
base=json.loads((DEST/f'lifecycle-{options.stamp}-purchase-item-base-result.json').read_text(encoding='utf-8'))
assert base['success'];ids={r['requestId']:scalar(r['data'][0],'ListID') for r in base['results']}
fields={'TransactionAccountFilter':{'ListID':[ids['Asset'],ids['COGS']]},'TransactionTypeFilter':{'TxnTypeFilter':['CreditCardCredit','VendorCredit']},'TransactionDetailLevelFilter':'AllExceptSummary'}
def run(label,args):
    proc=subprocess.run([str(EXE),*args,'--company-file',str(COMPANY),'--json'],capture_output=True,text=True,encoding='utf-8')
    (DEST/f'lifecycle-{options.stamp}-purchase-inventory-iterator-{label}.json').write_text(proc.stdout,encoding='utf-8')
    value=json.loads(proc.stdout);assert proc.returncode==0 and value['success']
    return value
plan=DEST/f'lifecycle-{options.stamp}-purchase-inventory-iterator-independent-plan.json'
plan.write_text(json.dumps({'protocolVersion':'13.0','country':'US','onError':'stopOnError','requests':[{'requestId':'postings','operation':'TransactionQuery','fields':fields}]}),encoding='utf-8')
baseline=run('independent-result',['sdk','batch','--input',str(plan)])['results'][0]['data']
assert baseline
checks=[]
for label,projection in [('full',None),('amount',['Amount']),('account',['AccountRef'])]:
    projected=dict(fields)
    if projection is not None:projected['IncludeRetElement']=projection
    path=DEST/f'lifecycle-{options.stamp}-purchase-inventory-iterator-{label}-fields.json'
    path.write_text(json.dumps(projected),encoding='utf-8')
    result=run(label+'-result',['sdk','run','TransactionQuery','--input',str(path),'--all','--page-size','1'])
    expected=baseline if projection is None else [{**n,'children':[c for c in n['children'] if c['name'] in projection]} for n in baseline]
    assert result['complete'] and result['pages']==len(expected) and Counter(map(canonical,result['data']))==Counter(map(canonical,expected))
    if projection is not None:assert all(c['name'] in projection for n in result['data'] for c in n['children'])
    checks.append({'projection':label,'records':len(expected),'pages':result['pages'],'independentExactFieldComparison':True,'internallyAddedIdentityFieldsNotLeaked':True})
metadata={'transactionTypes':['CreditCardCredit','VendorCredit'],'checks':checks,'pageSize':1,
    'scope':'Read-only Test.QBW inventory purchase credits with distinct amounts sharing transaction/line/account identities. Full/amount/account iterator reads matched an independent unbounded SDK query. No universal identity or inventory class-modification proof.'}
(DEST/f'lifecycle-{options.stamp}-purchase-inventory-iterator-verification.json').write_text(json.dumps(metadata,indent=2),encoding='utf-8')
print(json.dumps(metadata))
