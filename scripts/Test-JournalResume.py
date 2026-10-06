"""Live residual-plan resume, only in designated expendable Test.QBW."""
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
PREFIX='RESUME'+STAMP
def invoke(label,args):
    result=subprocess.run([str(EXE),*args,'--company-file',str(COMPANY)],capture_output=True,text=True,encoding='utf-8')
    (DEST/f'lifecycle-{STAMP}-resume-{label}.json').write_text(result.stdout,encoding='utf-8')
    return json.loads(result.stdout)
calls=[{'requestId':str(i+1),'operation':'ClassAdd','fields':{'ClassAdd':{'Name':PREFIX+suffix}}}
       for i,suffix in enumerate([' First',' First',' Pending'])]
path=DEST/f'lifecycle-{STAMP}-resume-origin-plan.json'
path.write_text(json.dumps({'protocolVersion':'13.0','country':'US','onError':'stopOnError','requests':calls}),encoding='utf-8')
origin=invoke('origin-result',['sdk','batch','--input',str(path),'--batch-size','1','--apply'])
assert not origin['success']
assert [r['outcome'] for r in origin['results']]==['succeeded','failed','unprocessed']
preview=invoke('preview',['sdk','resume','--input',origin['journalFile']])
resume=preview['resumption']
assert resume['canApply'] and resume['confirmedCompleted']==['1'] and resume['failedExcluded']==['2']
assert [r['requestId'] for r in resume['pendingPlan']['requests']]==['3']
applied=invoke('apply',['sdk','resume','--input',origin['journalFile'],'--apply'])
assert applied['success'] and [r['requestId'] for r in applied['execution']['results']]==['3']
assert applied['execution']['journalFile']!=origin['journalFile']
(DEST/f'lifecycle-{STAMP}-resume-applied-result.json').write_text(json.dumps(applied['execution']),encoding='utf-8')
query=DEST/f'lifecycle-{STAMP}-resume-verify-plan.json'
query.write_text(json.dumps({'requests':[{'requestId':'q','operation':'ClassQuery','fields':{'FullName':[PREFIX+' First',PREFIX+' Pending']}}]}),encoding='utf-8')
read=invoke('verify-result',['sdk','batch','--input',str(query)])
assert read['success'] and len(read['results'][0]['data'])==2
metadata={'confirmedSuccessSkipped':True,'failedRequestExcluded':True,'neverSubmittedRequestResumed':True,'newJournalCreated':True,'freshReadVerified':True}
(DEST/f'lifecycle-{STAMP}-resume-verification.json').write_text(json.dumps(metadata,indent=2),encoding='utf-8')
print(json.dumps(metadata),flush=True)
