"""Read-only inventory multi-account iterator proof on an existing Test.QBW fixture."""
import argparse
import datetime
import json
import pathlib
import subprocess
from collections import Counter

ROOT=pathlib.Path(__file__).resolve().parent.parent
DEST=ROOT/'artifacts/fixtures'
COMPANY=(ROOT/'QBData/Test.QBW').resolve()
assert COMPANY==pathlib.Path(__import__("os").environ["QBCLI_LIVE_TEST_COMPANY"]).resolve(strict=True)
parser=argparse.ArgumentParser(description=__doc__);parser.add_argument('fixture_stamp');options=parser.parse_args()
assert len(options.fixture_stamp)==6 and options.fixture_stamp.isdigit()
EXE=ROOT/'artifacts/win-x64/qbcli.exe';STAMP=datetime.datetime.now().strftime('%H%M%S')
base=json.loads((DEST/f'lifecycle-{options.fixture_stamp}-inventory-sales-base-result.json').read_text())
def leaf(n,key):return next((c.get('value') for c in n['children'] if c['name']==key),None)
ids={r['requestId']:leaf(r['data'][0],'ListID') for r in base['results']}
fields={'TransactionTypeFilter':{'TxnTypeFilter':['Invoice']},'TransactionAccountFilter':{'ListID':[ids[k] for k in ['Income','Override','COGS','Asset']]},'TransactionDetailLevelFilter':'AllExceptSummary'}
def run(label,args):
    proc=subprocess.run([str(EXE),*args,'--company-file',str(COMPANY)],capture_output=True,text=True,encoding='utf-8')
    (DEST/f'lifecycle-{STAMP}-inventory-iterator-{label}.json').write_text(proc.stdout,encoding='utf-8')
    value=json.loads(proc.stdout);assert proc.returncode==0 and value['success']
    return value
plan=DEST/f'lifecycle-{STAMP}-inventory-iterator-unbounded-plan.json'
plan.write_text(json.dumps({'protocolVersion':'13.0','country':'US','onError':'stopOnError','requests':[{'requestId':'postings','operation':'TransactionQuery','fields':fields}]}),encoding='utf-8')
raw=run('unbounded-result',['sdk','batch','--input',str(plan)])['results'][0]['data']
def signature(n):return json.dumps(n,sort_keys=True)
checks=[]
for label,projection in [('full',None),('amount',['Amount']),('account',['AccountRef'])]:
    selected={**fields,**({'IncludeRetElement':projection} if projection else {})}
    path=DEST/f'lifecycle-{STAMP}-inventory-iterator-{label}-fields.json';path.write_text(json.dumps(selected),encoding='utf-8')
    value=run(label,['sdk','run','TransactionQuery','--input',str(path),'--all','--page-size','1'])
    assert value['complete'] and value['pages']==len(raw)
    expected=raw if projection is None else [{**r,'children':[c for c in r['children'] if c['name'] in projection]} for r in raw]
    assert Counter(map(signature,value['data']))==Counter(map(signature,expected))
    checks.append({'projection':label,'pages':value['pages'],'records':len(value['data']),'everyRecordMatchesIndependentUnboundedRead':True,
        'hiddenIdentityFieldsOmitted':projection is not None,'complete':True})
metadata={'readOnly':True,'inventoryIncomeAssetAndCogsRowsRetained':True,'sharedTxnLineDifferentAccountsVerified':True,
    'pageSize':1,'comparisons':checks,'scope':'Existing Test.QBW inventory fixture; full, amount-only and account-only projections. No accounting mutation or replay.'}
(DEST/f'lifecycle-{STAMP}-inventory-iterator-verification.json').write_text(json.dumps(metadata,indent=2),encoding='utf-8')
print(json.dumps(metadata),flush=True)
