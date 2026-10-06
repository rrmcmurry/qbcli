"""Read-only report type and required enum probes on the designated Test.QBW."""
import copy,datetime,json,pathlib,subprocess
from collections import Counter
ROOT=pathlib.Path(__file__).resolve().parent.parent
DEST=ROOT/'artifacts/fixtures'
EXE=ROOT/'artifacts/win-x64/qbcli.exe'
COMPANY=ROOT/'QBData/Test.QBW'
stamp=datetime.datetime.now().strftime('%H%M%S')
reference=json.loads((ROOT/'src/QbCli.QuickBooks/Resources/sdk-reference.json').read_text(encoding='utf-8'))
def available(n):return n.get('supports',{}).get('US',True) and 0<float(n.get('versions',{}).get('US',1))<=13
def add(n,out):
    if not available(n) or n['type']=='skipwithchildren':return
    kind=n['type']
    if kind=='OR':add(next(c for c in n['children'] if available(c) and c['type']!='skipwithchildren'),out)
    elif kind=='skip':
        for c in n.get('children',[]):
            if c['required']:add(c,out)
    elif kind=='Aggregate':out[n['name']]=sample(n)
    else:out[n['name']]=n.get('enumValues',['X'])[0] if kind=='ENUMTYPE' else {'BOOLTYPE':True,'INTTYPE':1,'DATETYPE':'2026-10-05','DATETIMETYPE':'2026-10-05T12:00:00'}.get(kind,'X')
def sample(n):
    out={}
    for c in n.get('children',[]):
        if c['required']:add(c,out)
    return out
calls=[];labels=[]
for op in reference['operations']:
    if not op['name'].endswith('ReportQuery') or float(op['versions'].get('US',0))<=0:continue
    fields=sample(op['request']);names={c['name'] for c in op['request'].get('children',[])}
    if 'DisplayReport' in names:fields['DisplayReport']=False
    for choice in op['request'].get('children',[]):
        if choice['type']=='OR' and any(c['name']=='ReportPeriod' for c in choice.get('children',[])):
            fields['ReportPeriod']={'FromReportDate':'2026-01-01','ToReportDate':'2026-12-31'}
    for enum in op['request'].get('children',[]):
        if enum['type']!='ENUMTYPE' or not enum['required'] or not available(enum):continue
        for value in enum.get('enumValues',[]):
            if '[PRIVATE]' in value.upper():continue
            current=copy.deepcopy(fields);current[enum['name']]=value
            identity=str(len(calls)+1)
            calls.append({'requestId':identity,'operation':op['name'],'fields':current})
            labels.append({'requestId':identity,'operation':op['name'],'field':enum['name'],'value':value})
plan=DEST/f'lifecycle-{stamp}-report-variants-plan.json'
plan.write_text(json.dumps({'protocolVersion':'13.0','country':'US','onError':'continueOnError','requests':calls}),encoding='utf-8')
(DEST/f'lifecycle-{stamp}-report-variant-labels.json').write_text(json.dumps(labels,indent=2),encoding='utf-8')
run=subprocess.run([str(EXE),'sdk','batch','--input',str(plan),'--company-file',str(COMPANY),'--batch-size','1000'],capture_output=True,text=True,encoding='utf-8')
plan.with_name(plan.name.replace('-plan','-result')).write_text(run.stdout,encoding='utf-8')
result=json.loads(run.stdout)
if 'error' in result:raise RuntimeError(result['error'])
summary={'requests':len(calls),'outcomes':dict(Counter(r['outcome'] for r in result['results']))}
print(json.dumps(summary))
metadata=[{**label,'outcome':r['outcome'],'statusCode':r['statusCode']} for label,r in zip(labels,result['results'],strict=True)]
(DEST/f'lifecycle-{stamp}-report-variant-coverage.json').write_text(json.dumps(metadata,indent=2),encoding='utf-8')
print(json.dumps({'rejectedPayroll':[{**label,'statusCode':r['statusCode'],'statusMessage':r['statusMessage']} for label,r in zip(labels,result['results'],strict=True) if label['operation'].startswith('Payroll') and r['outcome'] not in ('succeeded','no_match')]}))
