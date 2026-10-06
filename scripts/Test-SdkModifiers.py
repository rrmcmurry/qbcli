"""Read current edit sequences, then exercise supported fixture modifiers.
Only designated Test.QBW; no stale-write retries or production-file parameter.
"""
import datetime
import json
import pathlib
import subprocess
ROOT=pathlib.Path(__file__).resolve().parent.parent
DEST=ROOT/'artifacts/fixtures'
COMPANY=ROOT/'QBData/Test.QBW'
EXE=ROOT/'artifacts/win-x64/qbcli.exe'
STAMP=datetime.datetime.now().strftime('%H%M%S')
SCHEMAS={o['name']:o for o in json.loads((ROOT/'src/QbCli.QuickBooks/Resources/sdk-reference.json').read_text(encoding='utf-8'))['operations']}
def visible(node):
    for c in node.get('children',[]):
        if c['type'] in ('OR','skip'):yield from visible(c)
        elif c['type']!='skipwithchildren':yield c
def leaf(record,key):return next((c['value'] for c in record['children'] if c['name']==key),None)
def run(label,calls,apply=False):
    path=DEST/f'lifecycle-{STAMP}-{label}-plan.json'
    path.write_text(json.dumps({'protocolVersion':'13.0','country':'US','onError':'continueOnError','requests':calls}),encoding='utf-8')
    args=[str(EXE),'sdk','batch','--input',str(path),'--company-file',str(COMPANY),'--batch-size','100']
    if apply:args+=['--apply']
    result=subprocess.run(args,capture_output=True,text=True,encoding='utf-8')
    path.with_name(path.name.replace('-plan','-result')).write_text(result.stdout,encoding='utf-8')
    value=json.loads(result.stdout)
    if 'error' in value:raise RuntimeError(value['error'])
    print(json.dumps({'phase':label,'results':[{k:r.get(k) for k in ('operation','outcome','statusCode','statusMessage')} for r in value['results']]}),flush=True)
    return value['results']
fixtures={}
for path in [DEST/'SDK154955-base-result.json',DEST/'SDK154955-transactions-result.json',*sorted(DEST.glob('lifecycle-*-result.json'))]:
    value=json.loads(path.read_text(encoding='utf-8-sig'))
    for r in value.get('results',[]):
        op=r['operation']
        if op.endswith('Add') and r['outcome']=='succeeded' and r['data']:
            modifier=op[:-3]+'Mod';query=op[:-3]+'Query'
            if modifier in SCHEMAS and query in SCHEMAS and modifier not in fixtures:
                record=r['data'][0];key='TxnID' if leaf(record,'TxnID') else 'ListID'
                if leaf(record,key) and key in {c['name'] for c in visible(SCHEMAS[query]['request'])}:
                    fixtures[modifier]=(query,key,leaf(record,key))
calls=[{'requestId':modifier,'operation':query,'fields':{key:identity}} for modifier,(query,key,identity) in fixtures.items()]
results=run('modifier-query',calls)
changes=[];notes=[]
for r in results:
    modifier=r['requestId']
    if r['outcome']!='succeeded' or len(r['data'])!=1:continue
    record=r['data'][0];_,key,_=fixtures[modifier]
    fields={key:leaf(record,key),'EditSequence':leaf(record,'EditSequence')}
    if not fields['EditSequence']:continue
    root=next(c for c in SCHEMAS[modifier]['request']['children'] if c['type']=='Aggregate')
    available={c['name']:c for c in visible(root) if c.get('supports',{}).get('US',True)}
    selected=next((n for n in ('Memo','Notes','Desc','ItemDesc') if n in available and available[n]['type']=='STRTYPE'),None)
    if selected:fields[selected]='SDK modifier fixture '+STAMP
    elif 'IsActive' in available:fields['IsActive']=True
    elif 'RefNumber' in available:fields['RefNumber']='M'+STAMP
    else:continue
    # Some modifiers (notably TimeTrackingMod) require unchanged source fields
    # in addition to identity/edit sequence. Copy only documented required tags.
    def value(node):
        return {c['name']:value(c) for c in node['children']} if node['children'] else node['value']
    for required in visible(root):
        if required.get('required') and required['name'] not in fields:
            source=next((c for c in record['children'] if c['name']==required['name']),None)
            if source is not None:fields[required['name']]=value(source)
    changes.append({'requestId':modifier,'operation':modifier,'fields':{root['name']:fields}})
    notes.append({'operation':modifier,'changedField':selected or ('IsActive' if 'IsActive' in fields else 'RefNumber'),
                  'kind':'field_change' if selected or 'RefNumber' in fields else 'request_acceptance_active_true'})
(DEST/f'lifecycle-{STAMP}-modifier-notes.json').write_text(json.dumps(notes,indent=2),encoding='utf-8')
applied=run('modifier-apply',changes,True)
after=run('modifier-verify',calls)
before_by_id={r['requestId']:r for r in results}
after_by_id={r['requestId']:r for r in after}
verified=[]
for outcome in applied:
    if outcome['outcome']!='succeeded':continue
    key=outcome['requestId'];previous=before_by_id[key]['data'][0]
    current=after_by_id[key]
    assert current['outcome']=='succeeded' and len(current['data'])==1
    current=current['data'][0]
    change=next(c for c in changes if c['requestId']==key)
    body=next(iter(change['fields'].values()))
    changed=next(n for n in ('Memo','Notes','Desc','ItemDesc','IsActive','RefNumber') if n in body)
    expected=str(body[changed]).lower() if isinstance(body[changed],bool) else body[changed]
    assert leaf(current,changed)==expected,(key,changed,'read-back mismatch')
    preserved=[]
    for field in ('Amount','TotalAmount','Subtotal','Balance','QuantityOnHand','TotalValue','Duration'):
        original=leaf(previous,field)
        if original is not None:
            assert leaf(current,field)==original,(key,field,'unrelated field changed')
            preserved.append(field)
    verified.append({'operation':key,'readBackField':changed,'preservedScalarFields':preserved})
(DEST/f'lifecycle-{STAMP}-modifier-verification.json').write_text(json.dumps(verified,indent=2),encoding='utf-8')
print(json.dumps({'verifiedModifiers':len(verified)}),flush=True)
