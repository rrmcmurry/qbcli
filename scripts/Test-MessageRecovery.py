"""SDK cache/replay/checksum/clear checks on expendable Test.QBW only."""
import datetime
import json
import pathlib
import subprocess
import uuid
ROOT=pathlib.Path(__file__).resolve().parent.parent
DEST=ROOT/'artifacts/fixtures'
EXE=ROOT/'artifacts/win-x64/qbcli.exe'
COMPANY=ROOT/'QBData/Test.QBW'
stamp=datetime.datetime.now().strftime('%H%M%S')
message_id='R'+uuid.uuid4().hex[:22]
name='Recovery'+stamp
def run(label,requests,attributes,apply=True):
    path=DEST/f'lifecycle-{stamp}-{label}-plan.json'
    path.write_text(json.dumps({'protocolVersion':'13.0','country':'US','onError':'stopOnError','messageSetAttributes':attributes,'requests':requests}),encoding='utf-8')
    args=[str(EXE),'sdk','batch','--input',str(path),'--company-file',str(COMPANY)]
    if apply:args+=['--apply']
    result=subprocess.run(args,capture_output=True,text=True,encoding='utf-8')
    path.with_name(path.name.replace('-plan','-result')).write_text(result.stdout,encoding='utf-8')
    value=json.loads(result.stdout)
    if 'error' in value:raise RuntimeError(value['error'])
    print(json.dumps({'phase':label,'success':value['success'],'messages':value['messageSetResponses'],
                      'results':[{k:r.get(k) for k in ('operation','outcome','statusCode','statusMessage')} for r in value['results']]}),flush=True)
    return value
request=[{'requestId':'create','operation':'ClassAdd','fields':{'ClassAdd':{'Name':name}}}]
first=run('recovery-create',request,{'newMessageSetID':message_id})
assert first['success']
replay=run('recovery-replay',request,{'newMessageSetID':message_id})
assert replay['messageSetResponses'][0]['attributes']['messageSetStatusCode']=='0'
def leaf(n,k):return next((c['value'] for c in n['children'] if c['name']==k),None)
if replay['results'][0]['data']:
    assert leaf(first['results'][0]['data'][0],'ListID')==leaf(replay['results'][0]['data'][0],'ListID')
else:
    # Local SDK returns a status-only cached response without per-request data.
    # Do not invent success/identity for missing responses.
    assert replay['results'][0]['outcome']=='unknown'
mismatch=run('recovery-mismatch',[{'requestId':'create','operation':'ClassAdd','fields':{'ClassAdd':{'Name':name+'Other'}}}],{'newMessageSetID':message_id})
assert not mismatch['success']
assert mismatch['messageSetResponses'][0]['attributes']['messageSetStatusCode']=='9001'
cleared=run('recovery-clear',[],{'oldMessageSetID':message_id})
assert cleared['success'] and cleared['mutatesRecoveryState'] and not cleared['mutatesCompany']
assert cleared['messageSetResponses'][0]['attributes']['messageSetStatusCode']=='0'
verified=run('recovery-verify',[{'requestId':'verify','operation':'ClassQuery','fields':{'FullName':name}}],{},False)
assert verified['success'] and len(verified['results'][0]['data'])==1
assert leaf(first['results'][0]['data'][0],'ListID')==leaf(verified['results'][0]['data'][0],'ListID')
print(json.dumps({'statusCheckAcknowledged':True,'checksumMismatchRejected':True,'recoveryStateCleared':True,'originalIdentityReadBack':True}),flush=True)
