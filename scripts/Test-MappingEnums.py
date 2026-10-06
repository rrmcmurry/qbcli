"""Live documented SDK14/16/17 mapping enum reads, designated Test.QBW only."""
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
checks=[]
for protocol in ('14.0','16.0','17.0'):
    categories=('BOX15','BOX1','ALL') if protocol=='14.0' else (('BOX14','BOX16','BOX3','ALL') if protocol=='16.0' else ('BOX16','BOX3','ALL'))
    plan={'protocolVersion':protocol,'country':'US','onError':'continueOnError','requests':[
        {'requestId':form+'-'+category,'operation':'Form1099CategoryAccountMappingQuery',
         'fields':{'FormType':form,**({'MappingCategory':category} if category!='ALL' else {})}}
        for form in ('MISC','NEC') for category in categories]}
    path=DEST/f'lifecycle-{STAMP}-mapping-{protocol}-plan.json'
    path.write_text(json.dumps(plan),encoding='utf-8')
    run=subprocess.run([str(EXE),'sdk','batch','--input',str(path),'--company-file',str(COMPANY)],
        capture_output=True,text=True,encoding='utf-8',timeout=90)
    path.with_name(path.name.replace('-plan.json','-result.json')).write_text(run.stdout,encoding='utf-8')
    result=json.loads(run.stdout)
    assert pathlib.Path(result.get('companyFile',''))==COMPANY and len(result.get('results',[]))==len(plan['requests']),'Incomplete SDK response; inspect retained evidence'
    for response in result.get('results',[]):
        form,category=response['requestId'].split('-')
        check={'protocol':protocol,'formType':form,'enumValue':category,
               'operation':response['operation'],'outcome':response['outcome'],'statusCode':response['statusCode']}
        assert response['outcome']!='unknown'
        check['scope']='Read-only category filtering on Test.QBW QBD2024R21 US. Schema version/country gates are tested separately; request acceptance or no_match does not verify category modification, migration, tax filing semantics or another host.'
        checks.append(check)
        print(json.dumps(check),flush=True)
    if 'results' not in result:
        print(json.dumps({'protocol':protocol,'error':result.get('error')}),flush=True)
        break
(DEST/f'lifecycle-{STAMP}-mapping-enum-verification.json').write_text(json.dumps(checks,indent=2),encoding='utf-8')
