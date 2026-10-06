"""Live ordered-choice and newer read-field checks, designated test company only."""
import datetime
import json
import pathlib
import subprocess
ROOT=pathlib.Path(__file__).resolve().parent.parent
COMPANY=ROOT/'QBData/Test.QBW'
EXE=ROOT/'artifacts/win-x64/qbcli.exe'
DEST=ROOT/'artifacts/fixtures'
stamp=datetime.datetime.now().strftime('%H%M%S')
calls=[{'requestId':'ordered','operation':'JournalEntryAdd','fields':{'JournalEntryAdd':{'$choices':[
    {'JournalDebitLine':{'AccountRef':{'FullName':'SDK154955 Expense'},'Amount':'20.00','Memo':'Ordered choice first'}},
    {'JournalCreditLine':{'AccountRef':{'FullName':'SDK154955 Income'},'Amount':'30.00','Memo':'Ordered choice middle'}},
    {'JournalDebitLine':{'AccountRef':{'FullName':'SDK154955 Expense'},'Amount':'10.00','Memo':'Ordered choice last'}}]}}},
    {'requestId':'form','operation':'Form1099CategoryAccountMappingQuery','fields':{'FormType':'NEC'}}]
plan=DEST/f'lifecycle-{stamp}-supplements-plan.json'
plan.write_text(json.dumps({'protocolVersion':'16.0','country':'US','onError':'continueOnError','requests':calls}),encoding='utf-8')
run=subprocess.run([str(EXE),'sdk','batch','--input',str(plan),'--company-file',str(COMPANY),'--apply'],capture_output=True,text=True,encoding='utf-8')
(DEST/f'lifecycle-{stamp}-supplements-result.json').write_text(run.stdout,encoding='utf-8')
result=json.loads(run.stdout)
if 'error' in result:raise RuntimeError(result['error'])
print(json.dumps([{k:r[k] for k in ('operation','outcome','statusCode')} for r in result['results']]))
assert all(r['outcome'] in ('succeeded','no_match') for r in result['results'])
lines=[c for c in result['results'][0]['data'][0]['children'] if c['name'] in ('JournalDebitLine','JournalCreditLine')]
assert len(lines)==3
print(json.dumps({'returnedLineOrder':[c['name'] for c in lines]}))
