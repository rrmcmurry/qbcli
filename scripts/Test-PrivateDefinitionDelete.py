"""Fresh owned private-extension deletion control on disposable Test.QBW."""
import datetime
import argparse
import json
import pathlib
import subprocess
import uuid

ROOT=pathlib.Path(__file__).resolve().parent.parent;DEST=ROOT/'artifacts/fixtures'
EXE=ROOT/'artifacts/win-x64/qbcli.exe';COMPANY=(ROOT/'QBData/Test.QBW').resolve()
assert COMPANY==pathlib.Path(__import__("os").environ["QBCLI_LIVE_TEST_COMPANY"]).resolve(strict=True)
STAMP=datetime.datetime.now().strftime('%H%M%S');OWNER='{'+str(uuid.uuid4()).upper()+'}';NAME='OwnedDelete'+STAMP
parser=argparse.ArgumentParser(description=__doc__);parser.add_argument('--assigned',action='store_true');parser.add_argument('--recreate',action='store_true',help='Test deleted owner/name rejection and same-name reuse under a new private owner.');options=parser.parse_args()
def scalar(n,k):return next((c.get('value') for c in n['children'] if c['name']==k),None)
def execute(label,calls,apply=False):
    path=DEST/f'lifecycle-{STAMP}-private-definition-{label}-plan.json'
    path.write_text(json.dumps({'protocolVersion':'13.0','country':'US','requests':calls}),encoding='utf-8')
    proc=subprocess.run([str(EXE),'sdk','batch','--input',str(path),'--company-file',str(COMPANY),'--file-mode','single-user','--json',*(['--apply'] if apply else [])],capture_output=True,text=True,encoding='utf-8')
    path.with_name(path.name.replace('-plan.json','-result.json')).write_text(proc.stdout,encoding='utf-8')
    result=json.loads(proc.stdout)
    print(json.dumps({'phase':label,'success':result.get('success'),'outcomes':[{k:r.get(k) for k in ['operation','outcome','statusCode']} for r in result.get('results',[])]}),flush=True)
    assert 'results' in result and not any(r['outcome']=='unknown' for r in result['results']),'Inspect journal; do not replay'
    return result
def call(op,fields):return {'operation':op,'fields':fields}
created=execute('create',[call('DataExtDefAdd',{'DataExtDefAdd':{'OwnerID':OWNER,'DataExtName':NAME,'DataExtType':'STR255TYPE',
    **({'AssignToObject':'Customer'} if options.assigned else {})}})],True)
assert created['success']
def query(label):return execute(label,[call('DataExtDefQuery',{'OwnerID':OWNER})])
before=query('before');assert before['success']
assert sum(scalar(n,'DataExtName')==NAME for n in before['results'][0]['data'])==1
def customer(label):return execute(label,[call('CustomerQuery',{'FullName':'SDK154955 Customer','OwnerID':OWNER})])
def values(result):return {scalar(c,'DataExtName'):scalar(c,'DataExtValue') for n in result['results'][0]['data'] for c in n['children'] if c['name']=='DataExtRet'}
if options.assigned:
    assigned=execute('assign',[call('DataExtAdd',{'DataExtAdd':{'OwnerID':OWNER,'DataExtName':NAME,'ListDataExtType':'Customer','ListObjRef':{'FullName':'SDK154955 Customer'},'DataExtValue':'Owned synthetic deletion control'}})],True)
    assert assigned['success'] and values(customer('customer-before'))[NAME]=='Owned synthetic deletion control'
deleted=execute('delete',[call('DataExtDefDel',{'OwnerID':OWNER,'DataExtName':NAME})],True)
outcome=deleted['results'][0];assert outcome['outcome']=='succeeded' or (outcome['outcome']=='failed' and outcome['statusCode']==3175)
after=query('after');assert after['success']
remaining=sum(scalar(n,'DataExtName')==NAME for n in after['results'][0]['data'])
assert remaining==(0 if outcome['outcome']=='succeeded' else 1)
if options.assigned:
    fresh=customer('customer-after');assert fresh['success']
    assert (NAME not in values(fresh))==(outcome['outcome']=='succeeded')
if options.recreate:
    assert outcome['outcome']=='succeeded','Recreation control requires confirmed deletion'
    rejected=execute('recreate-same-owner',[call('DataExtDefAdd',{'DataExtDefAdd':{'OwnerID':OWNER,'DataExtName':NAME,'DataExtType':'STR255TYPE'}})],True)
    rejected_outcome=rejected['results'][0]
    assert rejected_outcome['outcome']=='failed' and rejected_outcome['statusCode'] is not None
    same_after=query('recreate-same-owner-after');assert same_after['success']
    assert not any(scalar(n,'DataExtName')==NAME for n in same_after['results'][0]['data'])
    other_owner='{'+str(uuid.uuid4()).upper()+'}'
    reused=execute('recreate-new-owner',[call('DataExtDefAdd',{'DataExtDefAdd':{'OwnerID':other_owner,'DataExtName':NAME,'DataExtType':'STR255TYPE'}})],True)
    assert reused['success']
    other_before=execute('recreate-new-owner-before',[call('DataExtDefQuery',{'OwnerID':other_owner})]);assert other_before['success']
    assert sum(scalar(n,'DataExtName')==NAME for n in other_before['results'][0]['data'])==1
    cleaned=execute('recreate-new-owner-delete',[call('DataExtDefDel',{'OwnerID':other_owner,'DataExtName':NAME})],True);assert cleaned['success']
    other_after=execute('recreate-new-owner-after',[call('DataExtDefQuery',{'OwnerID':other_owner})]);assert other_after['success']
    assert not any(scalar(n,'DataExtName')==NAME for n in other_after['results'][0]['data'])
    recreation={'sameOwnerNameRecreationRejected':True,'recreationStatusCode':rejected_outcome['statusCode'],
        'deletedDefinitionStillAbsent':True,'sameNameDifferentPrivateOwnerAccepted':True,'newOwnerDefinitionCleanedAndAbsent':True,
        'scope':'Fresh private owner/name unused definition on Test.QBW QBD2024R21 US protocol13. Same-owner rejected Add is a deliberate negative lifecycle control after confirmed deletion, not retry of an unknown request. Same name with fresh owner accepted/read/deleted/read absent. No custom fields or other editions/releases proof.'}
    (DEST/f'lifecycle-{STAMP}-private-definition-recreation-verification.json').write_text(json.dumps(recreation,indent=2),encoding='utf-8')
    print(json.dumps(recreation),flush=True)
check={'operation':'DataExtDefDel','outcome':outcome['outcome'],'statusCode':outcome['statusCode'],
    'requestedFileMode':'single-user','freshPrivateOwner':True,'unusedDefinition':not options.assigned,'freshReadMatchesOutcome':True,
    'assignedValueAbsentAfterDefinitionDelete':remaining==0 if options.assigned else None,
    'definitionAbsentAfterDelete':remaining==0,'scope':'Fresh private GUID/name definition on Test.QBW QBD2024R21 US protocol13. Optionally assigned to one owned fixture customer; definition/value reads verify outcome. Single-user BeginSession requested. No custom-field deletion, inferred cause for prior failures or prior-owner replay.'}
(DEST/f'lifecycle-{STAMP}-private-definition-verification.json').write_text(json.dumps(check,indent=2),encoding='utf-8')
print(json.dumps(check),flush=True)
