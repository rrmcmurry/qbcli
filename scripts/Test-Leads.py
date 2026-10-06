"""Isolated lead lifecycle; mutates only the designated disposable company."""
import datetime
import json
import pathlib
import subprocess
ROOT=pathlib.Path(__file__).resolve().parent.parent
COMPANY=(ROOT/'QBData/Test.QBW').resolve()
assert COMPANY==pathlib.Path(__import__("os").environ["QBCLI_LIVE_TEST_COMPANY"]).resolve(strict=True)
EXE=ROOT/'artifacts/win-x64/qbcli.exe'
DEST=ROOT/'artifacts/fixtures'
STAMP=datetime.datetime.now().strftime('%H%M%S')
NAME='Synthetic Lead '+STAMP
checks=[]
def invoke(label,operation,fields,apply=False):
    path=DEST/f'lifecycle-{STAMP}-lead-{label}-plan.json'
    path.write_text(json.dumps({'protocolVersion':'13.0','requests':[{'operation':operation,'fields':fields}]}),encoding='utf-8')
    args=[str(EXE),'sdk','batch','--input',str(path),'--company-file',str(COMPANY)]
    if apply:args+=['--apply']
    run=subprocess.run(args,capture_output=True,text=True,encoding='utf-8',timeout=90)
    path.with_name(path.name.replace('-plan.json','-result.json')).write_text(run.stdout,encoding='utf-8')
    result=json.loads(run.stdout)
    if 'results' not in result:raise RuntimeError(result.get('error'))
    response=result['results'][0]
    check={k:response[k] for k in ('operation','outcome','statusCode')}
    checks.append(check);print(json.dumps(check),flush=True)
    return response
def leaf(node,name):return next((c.get('value') for c in node['children'] if c['name']==name),None)
add=invoke('add','LeadAdd',{'LeadAdd':{'FullName':NAME,'Status':'Warm'}},True)
query=invoke('query','LeadQuery',{'Name':NAME})
if add['outcome']=='succeeded' and query['outcome']=='succeeded':
    record=query['data'][0]
    identifier=leaf(record,'ListID')
    sequence=leaf(record,'EditSequence')
    assert identifier and sequence
    modify=invoke('mod','LeadMod',{'LeadMod':{'ListID':identifier,'EditSequence':sequence,'FullName':NAME,'Status':'Hot'}},True)
    if modify['outcome']=='succeeded':
        fresh=invoke('fresh','LeadQuery',{'Name':NAME})
        assert fresh['outcome']=='succeeded' and leaf(fresh['data'][0],'Status')=='Hot'
        checks.append({'modifiedStatusFreshReadVerified':True})
(DEST/f'lifecycle-{STAMP}-lead-verification.json').write_text(json.dumps(checks,indent=2),encoding='utf-8')
