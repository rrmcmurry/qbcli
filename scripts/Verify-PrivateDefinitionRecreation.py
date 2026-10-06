"""Independently verify saved private-definition recreation evidence; no SDK calls."""
import json
import pathlib
import sys
import uuid

root=pathlib.Path(__file__).resolve().parent.parent
dest=root/'artifacts/fixtures'
stamp=sys.argv[1];assert len(stamp)==6 and stamp.isdigit()
prefix=f'lifecycle-{stamp}-private-definition-'
company=str((root/'QBData/Test.QBW').resolve())
assert pathlib.Path(company)==pathlib.Path(__import__("os").environ["QBCLI_LIVE_TEST_COMPANY"]).resolve(strict=True)
def load(label,suffix):return json.loads((dest/f'{prefix}{label}-{suffix}.json').read_text(encoding='utf-8-sig'))
def scalar(node,key):return next((c.get('value') for c in node['children'] if c['name']==key),None)
def definition(label):
    plan=load(label,'plan');assert plan['protocolVersion']=='13.0' and plan['country']=='US'
    assert len(plan['requests'])==1 and plan['requests'][0]['operation']=='DataExtDefAdd'
    value=plan['requests'][0]['fields']['DataExtDefAdd']
    uuid.UUID(value['OwnerID'].strip('{}'));assert value['OwnerID']!='0' and value['DataExtType']=='STR255TYPE'
    return value
original=definition('create');assert definition('recreate-same-owner')==original
other=definition('recreate-new-owner');assert other['OwnerID']!=original['OwnerID'] and other['DataExtName']==original['DataExtName']
def result(label,operation,owner,name,failed=False):
    value=load(label,'result');assert len(value['results'])==1
    assert pathlib.Path(value['companyFile'])==pathlib.Path(company)
    row=value['results'][0];assert row['operation']==operation
    assert row['outcome']==('failed' if failed else 'succeeded')
    assert row['statusCode']!=0 if failed else row['statusCode']==0
    plan=load(label,'plan');fields=plan['requests'][0]['fields']
    if operation=='DataExtDefDel':assert fields=={'OwnerID':owner,'DataExtName':name}
    journal=[json.loads(line) for line in pathlib.Path(value['journalFile']).read_text(encoding='utf-8').splitlines()]
    assert sum(r['kind']=='submitted' for r in journal)==1 and any(r['kind']=='finished' for r in journal)
    return row
name=original['DataExtName'];owner=original['OwnerID']
result('create','DataExtDefAdd',owner,name)
result('delete','DataExtDefDel',owner,name)
rejected=result('recreate-same-owner','DataExtDefAdd',owner,name,True)
result('recreate-new-owner','DataExtDefAdd',other['OwnerID'],name)
result('recreate-new-owner-delete','DataExtDefDel',other['OwnerID'],name)
for label,expected_owner,present in [('before',owner,True),('after',owner,False),('recreate-same-owner-after',owner,False),('recreate-new-owner-before',other['OwnerID'],True),('recreate-new-owner-after',other['OwnerID'],False)]:
    plan=load(label,'plan');assert plan['requests']==[{'operation':'DataExtDefQuery','fields':{'OwnerID':expected_owner}}]
    value=load(label,'result');assert value['success'] and len(value['results'])==1
    assert pathlib.Path(value['companyFile'])==pathlib.Path(company)
    matches=[n for n in value['results'][0]['data'] if scalar(n,'DataExtName')==name]
    assert len(matches)==int(present)
    assert all(scalar(n,'OwnerID').upper()==expected_owner.upper() for n in matches)
check={'sameOwnerNameRecreationRejected':True,'recreationStatusCode':rejected['statusCode'],
    'deletedDefinitionStillAbsent':True,'sameNameDifferentPrivateOwnerAccepted':True,'newOwnerDefinitionCleanedAndAbsent':True,
    'independentRetainedEvidenceVerification':True,
    'scope':'Fresh private unused definition on Test.QBW QBD2024R21 US protocol13. Saved plans, five mutation journals and fresh queries independently verified without SDK calls or replay. Same-owner/name Add deliberately rejected after confirmed deletion; new-owner same-name accepted then deleted. No custom-field or other releases proof.'}
(dest/f'{prefix}recreation-verification.json').write_text(json.dumps(check,indent=2),encoding='utf-8')
print(json.dumps(check))
