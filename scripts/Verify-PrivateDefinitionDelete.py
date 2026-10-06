"""Verify saved private definition deletion evidence without SDK requests."""
import json
import pathlib
import sys
import uuid
root=pathlib.Path(__file__).resolve().parent.parent;dest=root/'artifacts/fixtures'
stamp=sys.argv[1];assert len(stamp)==6 and stamp.isdigit()
def load(label):return json.loads((dest/f'lifecycle-{stamp}-private-definition-{label}-result.json').read_text(encoding='utf-8'))
def scalar(n,k):return next((c.get('value') for c in n['children'] if c['name']==k),None)
created=load('create');before=load('before');deleted=load('delete');after=load('after')
assert created['success'] and before['success'] and after['success']
plan=json.loads((dest/f'lifecycle-{stamp}-private-definition-create-plan.json').read_text())
definition=plan['requests'][0]['fields']['DataExtDefAdd'];owner=definition['OwnerID'];name=definition['DataExtName'];uuid.UUID(owner.strip('{}'))
assert owner!='0' and definition['DataExtType']=='STR255TYPE'
assert sum(scalar(n,'DataExtName')==name and scalar(n,'OwnerID').upper()==owner.upper() for n in before['results'][0]['data'])==1
deleteplan=json.loads((dest/f'lifecycle-{stamp}-private-definition-delete-plan.json').read_text())
assert deleteplan['requests']==[{'operation':'DataExtDefDel','fields':{'OwnerID':owner,'DataExtName':name}}]
assert len(deleted['results'])==1;outcome=deleted['results'][0]
assert outcome['outcome']=='succeeded' or (outcome['outcome']=='failed' and outcome['statusCode']==3175)
absent=not any(scalar(n,'DataExtName')==name for n in after['results'][0]['data']);assert absent==(outcome['outcome']=='succeeded')
assigned=definition.get('AssignToObject')=='Customer'
if assigned:
    added=load('assign');assert added['success']
    def values(v):return {scalar(c,'DataExtName'):scalar(c,'DataExtValue') for n in v['results'][0]['data'] for c in n['children'] if c['name']=='DataExtRet'}
    cb=load('customer-before');ca=load('customer-after');assert cb['success'] and ca['success']
    assert values(cb)[name]=='Owned synthetic deletion control' and (name not in values(ca))==absent
journal=[json.loads(line) for line in pathlib.Path(deleted['journalFile']).read_text(encoding='utf-8').splitlines()]
assert sum(r['kind']=='submitted' for r in journal)==1
assert any(r['kind']=='finished' for r in journal)
check={'operation':'DataExtDefDel','outcome':outcome['outcome'],'statusCode':outcome['statusCode'],
    'requestedFileMode':'single-user','freshPrivateOwner':True,'unusedDefinition':not assigned,'freshReadMatchesOutcome':True,
    'assignedValueAbsentAfterDefinitionDelete':absent if assigned else None,'definitionAbsentAfterDelete':absent,
    'independentRetainedEvidenceVerification':True,
    'scope':'Fresh private GUID/name definition on Test.QBW QBD2024R21 US protocol13. Optional Customer assignment with value still present before definition deletion. Single-user BeginSession requested by harness; no causal claim for prior errors, custom-field deletion or prior-owner replay.'}
(dest/f'lifecycle-{stamp}-private-definition-verification.json').write_text(json.dumps(check,indent=2),encoding='utf-8')
print(json.dumps({'privateDefinitionDeletionVerified':absent,'assignedValueDeletionVerified':absent if assigned else None}))
