"""Live UTF-8 batching read probes, only against the designated Test.QBW."""
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
for limit in (1024,2048):
    requests=[{'requestId':str(i),'operation':'ClassQuery','fields':{'FullName':'RentalA'}} for i in range(48)]
    path=DEST/f'lifecycle-{STAMP}-byte-bound-{limit}-plan.json'
    path.write_text(json.dumps({'protocolVersion':'13.0','maximumBatchBytes':limit,'requests':requests}),encoding='utf-8')
    journal=DEST/f'lifecycle-{STAMP}-byte-bound-{limit}.jsonl'
    run=subprocess.run([str(EXE),'sdk','batch','--input',str(path),'--company-file',str(COMPANY),
        '--batch-size','1000','--apply','--journal',str(journal)],capture_output=True,text=True,encoding='utf-8',timeout=90)
    path.with_name(path.name.replace('-plan.json','-result.json')).write_text(run.stdout,encoding='utf-8')
    result=json.loads(run.stdout)
    assert result['success'],result.get('error')
    assert [r['requestId'] for r in result['results']]==[r['requestId'] for r in requests]
    assert all(r['outcome']=='succeeded' for r in result['results'])
    records=[json.loads(line) for line in journal.read_text(encoding='utf-8').splitlines()]
    submitted=[r for r in records if r['kind']=='submitted']
    assert len(submitted)>1
    sizes=[len(r['requestXml'].encode('utf-8')) for r in submitted]
    assert all(size<=limit for size in sizes)
    check={'maximumBatchBytes':limit,'requestCount':len(requests),'messageSetCount':len(submitted),
        'maximumSubmittedBytes':max(sizes),'allResultsCorrelated':True,'allQueriesSucceeded':True}
    checks.append(check);print(json.dumps(check),flush=True)
assert checks[0]['messageSetCount']>checks[1]['messageSetCount']
(DEST/f'lifecycle-{STAMP}-byte-bound-verification.json').write_text(json.dumps(checks,indent=2),encoding='utf-8')
