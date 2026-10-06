"""Read back one recorded modifier batch without repeating its writes."""
import json
import pathlib
import subprocess
import sys
ROOT=pathlib.Path(__file__).resolve().parent.parent
DEST=ROOT/'artifacts/fixtures'
stamp=sys.argv[1]
if len(stamp)!=6 or not stamp.isdigit():raise ValueError('Provide recorded fixture HHmmss')
queries=DEST/f'lifecycle-{stamp}-modifier-query-plan.json'
result=subprocess.run([str(ROOT/'artifacts/win-x64/qbcli.exe'),'sdk','batch','--input',str(queries),'--company-file',str(ROOT/'QBData/Test.QBW'),'--batch-size','100'],capture_output=True,text=True,encoding='utf-8')
(DEST/f'lifecycle-{stamp}-modifier-verify-result.json').write_text(result.stdout,encoding='utf-8')
after=json.loads(result.stdout)
if 'error' in after:raise RuntimeError(after['error'])
before=json.loads((DEST/f'lifecycle-{stamp}-modifier-query-result.json').read_text(encoding='utf-8'))
plan=json.loads((DEST/f'lifecycle-{stamp}-modifier-apply-plan.json').read_text(encoding='utf-8'))
applied=json.loads((DEST/f'lifecycle-{stamp}-modifier-apply-result.json').read_text(encoding='utf-8'))
def leaf(n,k):return next((c['value'] for c in n['children'] if c['name']==k),None)
verified=[]
for outcome in applied['results']:
    if outcome['outcome']!='succeeded':continue
    key=outcome['requestId'];old=next(r for r in before['results'] if r['requestId']==key)['data'][0]
    query=next(r for r in after['results'] if r['requestId']==key)
    assert query['outcome']=='succeeded' and len(query['data'])==1
    new=query['data'][0]
    body=next(iter(next(c for c in plan['requests'] if c['requestId']==key)['fields'].values()))
    changed=next(n for n in ('Memo','Notes','Desc','ItemDesc','IsActive','RefNumber') if n in body)
    expected=str(body[changed]).lower() if isinstance(body[changed],bool) else body[changed]
    assert leaf(new,changed)==expected,(key,changed,'mismatch')
    kept=[]
    for field in ('Amount','TotalAmount','Subtotal','Balance','QuantityOnHand','TotalValue','Duration'):
        if leaf(old,field) is not None:
            assert leaf(new,field)==leaf(old,field),(key,field,'changed')
            kept.append(field)
    verified.append({'operation':key,'readBackField':changed,'preservedScalarFields':kept})
(DEST/f'lifecycle-{stamp}-modifier-verification.json').write_text(json.dumps(verified,indent=2),encoding='utf-8')
print(json.dumps({'verifiedModifiers':len(verified)}))
