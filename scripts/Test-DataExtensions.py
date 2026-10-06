"""Modify/read/delete a private SDK fixture value and definition in Test.QBW."""
import datetime
import json
import pathlib
import subprocess
ROOT=pathlib.Path(__file__).resolve().parent.parent
DEST=ROOT/'artifacts/fixtures'
EXE=ROOT/'artifacts/win-x64/qbcli.exe'
COMPANY=ROOT/'QBData/Test.QBW'
stamp=datetime.datetime.now().strftime('%H%M%S')
owner='{F6A6DC28-4823-4CEE-B497-8B20CE70F34B}'
name='EXT162827 Private'
customer={'FullName':'SDK154955 Customer'}
def body(op,fields):return (op,{op:fields})
def run(label,calls,apply=True):
    path=DEST/f'lifecycle-{stamp}-{label}-plan.json'
    path.write_text(json.dumps({'protocolVersion':'13.0','country':'US','onError':'continueOnError','requests':[
        {'requestId':str(i+1),'operation':op,'fields':fields} for i,(op,fields) in enumerate(calls)]}),encoding='utf-8')
    args=[str(EXE),'sdk','batch','--input',str(path),'--company-file',str(COMPANY)]
    if apply:args+=['--apply']
    result=subprocess.run(args,capture_output=True,text=True,encoding='utf-8')
    path.with_name(path.name.replace('-plan','-result')).write_text(result.stdout,encoding='utf-8')
    value=json.loads(result.stdout)
    if 'error' in value:raise RuntimeError(value['error'])
    print(json.dumps({'phase':label,'outcomes':[{k:r.get(k) for k in ('operation','outcome','statusCode','statusMessage')} for r in value['results']]}),flush=True)
    return value['results']
def leaf(n,k):return next((c['value'] for c in n['children'] if c['name']==k),None)
query=('CustomerQuery',{'FullName':'SDK154955 Customer','OwnerID':owner})
def values(result):return {leaf(c,'DataExtName'):leaf(c,'DataExtValue') for record in result['data'] for c in record['children'] if c['name']=='DataExtRet'}
changed=run('extension-modify',[
    body('DataExtMod',{'OwnerID':owner,'DataExtName':name,'ListDataExtType':'Customer','ListObjRef':customer,'DataExtValue':'Updated synthetic value'}),query])
assert changed[0]['outcome']=='succeeded' and values(changed[1])[name]=='Updated synthetic value'
renamed=run('extension-definition-modify',[
    body('DataExtDefMod',{'OwnerID':owner,'DataExtName':name,'DataExtNewName':name+'R'}),query])
if renamed[0]['outcome']=='succeeded':
    name+='R'
    assert values(renamed[1])[name]=='Updated synthetic value'
deleted=run('extension-delete',[
    body('DataExtDel',{'OwnerID':owner,'DataExtName':name,'ListDataExtType':'Customer','ListObjRef':customer}),query])
assert deleted[0]['outcome']=='succeeded' and name not in values(deleted[1])
definitions=run('extension-definition-delete',[
    ('DataExtDefDel',{'OwnerID':owner,'DataExtName':name}),('DataExtDefQuery',{'OwnerID':owner})])
if definitions[0]['outcome']=='succeeded':
    assert not any(leaf(record,'DataExtName')==name for record in definitions[1]['data'])
else:
    # QuickBooks can retain an in-use definition after a value is removed.
    assert definitions[0]['statusCode']==3175
unused='Unused'+stamp
created=run('extension-unused-create',[body('DataExtDefAdd',{'OwnerID':owner,'DataExtName':unused,'DataExtType':'STR255TYPE'})])
assert created[0]['outcome']=='succeeded'
unused_deleted=run('extension-unused-delete',[('DataExtDefDel',{'OwnerID':owner,'DataExtName':unused}),('DataExtDefQuery',{'OwnerID':owner})])
if unused_deleted[0]['outcome']=='succeeded':
    assert not any(leaf(record,'DataExtName')==unused for record in unused_deleted[1]['data'])
else:assert unused_deleted[0]['statusCode']==3175
print(json.dumps({'modifiedValueReadBack':True,'deletedValueAbsent':True,'unusedDefinitionDeleted':unused_deleted[0]['outcome']=='succeeded'}),flush=True)
