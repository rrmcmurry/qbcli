"""Prove a stale fixture edit fails without replacing the current memo/class."""
import datetime
import json
import pathlib
import subprocess
ROOT=pathlib.Path(__file__).resolve().parent.parent
DEST=ROOT/'artifacts/fixtures'
EXE=ROOT/'artifacts/win-x64/qbcli.exe'
COMPANY=ROOT/'QBData/Test.QBW'
stamp=datetime.datetime.now().strftime('%H%M%S')
old=json.loads((DEST/'SDK154955-transactions-result.json').read_text(encoding='utf-8'))
check=next(r['data'][0] for r in old['results'] if r['operation']=='CheckAdd')
def leaf(n,k):return next((c['value'] for c in n['children'] if c['name']==k),None)
verified=sorted(DEST.glob('lifecycle-*-modifier-verify-result.json'))[-1]
current=json.loads(verified.read_text(encoding='utf-8'))
expected=next(r['data'][0] for r in current['results'] if r['requestId']=='CheckMod')
calls=[{'requestId':'stale','operation':'CheckMod','fields':{'CheckMod':{'TxnID':leaf(check,'TxnID'),'EditSequence':leaf(check,'EditSequence'),'Memo':'STALE REQUEST MUST NOT APPLY'}}},
       {'requestId':'verify','operation':'CheckQuery','fields':{'TxnID':leaf(check,'TxnID'),'IncludeLineItems':True}}]
plan=DEST/f'lifecycle-{stamp}-stale-edit-plan.json'
plan.write_text(json.dumps({'protocolVersion':'13.0','country':'US','onError':'continueOnError','requests':calls}),encoding='utf-8')
run=subprocess.run([str(EXE),'sdk','batch','--input',str(plan),'--company-file',str(COMPANY),'--apply'],capture_output=True,text=True,encoding='utf-8')
(DEST/f'lifecycle-{stamp}-stale-edit-result.json').write_text(run.stdout,encoding='utf-8')
result=json.loads(run.stdout)
if 'error' in result:raise RuntimeError(result['error'])
stale,read=result['results']
assert stale['outcome']=='failed' and stale['statusCode']==3200
assert read['outcome']=='succeeded'
actual=read['data'][0]
assert leaf(actual,'Memo')==leaf(expected,'Memo')
assert leaf(actual,'Amount')==leaf(expected,'Amount')
line=next(c for c in actual['children'] if c['name']=='ExpenseLineRet')
assert leaf(next(c for c in line['children'] if c['name']=='ClassRef'),'FullName')=='RentalA'
print(json.dumps({'staleStatusCode':stale['statusCode'],'memoAndAmountPreserved':True,'classPreserved':True}))
