"""Live read-only smoke probes against the explicitly designated company. No private data in coverage metadata."""
import json
import pathlib
import subprocess

ROOT = pathlib.Path(__file__).resolve().parent.parent
EXE = ROOT / 'artifacts/win-x64/qbcli.exe'
COMPANY = ROOT / 'QBData/Test.QBW'
DEST = ROOT / 'artifacts/fixtures'
REFERENCE = json.loads((ROOT / 'src/QbCli.QuickBooks/Resources/sdk-reference.json').read_text(encoding='utf-8'))

def available(node):
    return node.get('supports',{}).get('US',True) and 0 < float(node.get('versions',{}).get('US',1)) <= 13

def add(node, output):
    if not available(node) or node['type']=='skipwithchildren': return
    kind = node['type']
    if kind == 'OR':
        choice = next((c for c in node.get('children',[]) if available(c) and c['type']!='skipwithchildren'), None)
        if choice is None: raise ValueError('No documented US choice')
        add(choice,output)
    elif kind=='skip':
        for child in node.get('children',[]):
            if child.get('required'):add(child,output)
    elif kind=='Aggregate':
        output[node['name']] = sample(node)
    else:
        values = {'BOOLTYPE':True,'INTTYPE':int(node.get('minimum',1)), 'DATETYPE':'2026-10-05', 'DATETIMETYPE':'2026-10-05T12:00:00',
                  'GUIDTYPE':'0','UUIDTYPE':'{11111111-1111-1111-1111-111111111111}', 'TIMEINTERVALTYPE':'PT1H'}
        output[node['name']] = node.get('enumValues',['X'])[0] if kind=='ENUMTYPE' else values.get(kind,'1.00' if kind in ('AMTTYPE','PRICETYPE','QUANTYPE','PERCENTTYPE','FLOATTYPE') else 'NONEXISTENT')

def sample(node):
    output = {}
    for child in node.get('children',[]):
        if child.get('required'): add(child,output)
    return output

calls=[]
for operation in REFERENCE['operations']:
    if operation['name'].endswith('Query') and 0 < float(operation['versions'].get('US',0)) <= 13:
        calls.append({'requestId':operation['name'],'operation':operation['name'],'fields':sample(operation['request'])})
DEST.mkdir(parents=True,exist_ok=True)
plan = DEST / 'read-probes-plan.json'
plan.write_text(json.dumps({'protocolVersion':'13.0','country':'US','onError':'continueOnError','requests':calls}),encoding='utf-8')
completed = subprocess.run([str(EXE),'sdk','batch','--input',str(plan),'--company-file',str(COMPANY),'--batch-size','1000'],capture_output=True,text=True,encoding='utf-8')
(DEST/'read-probes-result.json').write_text(completed.stdout,encoding='utf-8')
result=json.loads(completed.stdout)
if 'error' in result:
    print(json.dumps(result))
    raise SystemExit(1)
coverage=[{'operation':r['operation'],'kind':'read_smoke','outcome':r['outcome'],'statusCode':r['statusCode'],'country':'US','protocol':'13.0','tested':'2026-10-05'} for r in result['results']]
(DEST/'read-probes-coverage.json').write_text(json.dumps(coverage,indent=2),encoding='utf-8')
from collections import Counter
print(json.dumps({'requests':len(calls),'outcomes':dict(Counter(r['outcome'] for r in coverage)), 'rejected':[{k:r[k] for k in ('operation','statusCode')} for r in coverage if r['outcome'] not in ('succeeded','no_match')]}))
