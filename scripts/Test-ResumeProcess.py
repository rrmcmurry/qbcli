"""Observe a synthetic journal writer's real process lifetime; no SDK sessions."""
import datetime
import json
import pathlib
import subprocess

ROOT=pathlib.Path(__file__).resolve().parent.parent
DEST=ROOT/'artifacts/fixtures'
STAMP=datetime.datetime.now().strftime('%H%M%S')
JOURNAL=DEST/f'lifecycle-{STAMP}-resume-process.jsonl'
HELPER=ROOT/'scripts/journal-process-evidence/bin/Release/net10.0/journal-process-evidence.dll'
EXE=ROOT/'artifacts/win-x64-0.8.10/qbcli.exe'
process=subprocess.Popen(['dotnet',str(HELPER),str(JOURNAL)],stdout=subprocess.PIPE,text=True,encoding='utf-8')
assert process.stdout.readline().strip()=='finished_but_process_alive'
assert process.poll() is None
records=[json.loads(line) for line in JOURNAL.read_text(encoding='utf-8').splitlines()]
assert records[-1]['kind']=='finished' and records[0]['sourceProcess']['processId']==process.pid

def run(label, apply=False):
    result=subprocess.run([str(EXE),'sdk','resume','--input',str(JOURNAL),'--company-file',r'C:\synthetic\Test.QBW',*(['--apply'] if apply else [])],capture_output=True,text=True,encoding='utf-8')
    (DEST/f'lifecycle-{STAMP}-resume-process-{label}.json').write_text(result.stdout,encoding='utf-8')
    return result.returncode,json.loads(result.stdout)

code,preview=run('live-preview')
assert code==0 and preview['resumption']['sourceProcessState']=='running' and not preview['resumption']['canApply']
code,refused=run('live-apply',True)
assert code==4 and refused['error']['code']=='source_process_running'
assert process.wait(timeout=30)==0
code,stopped=run('stopped-preview')
assert code==0 and stopped['resumption']['sourceProcessState']=='stopped' and stopped['resumption']['canApply']
assert [r['requestId'] for r in stopped['resumption']['pendingPlan']['requests']]==['3']
metadata={'syntheticExecutionOnly':True,'finishedJournalWhileWriterAlive':True,'exactOriginatingPidRecorded':True,
    'livePreviewBlocksApply':True,'liveApplyRefusedBeforeConnection':True,'processExitAuthoritativelyObserved':True,
    'stoppedPreviewAllowsOnlyUnsubmittedRemainder':True,'sdkConnectionOrResidualSubmissionPerformed':False}
(DEST/f'lifecycle-{STAMP}-resume-process-verification.json').write_text(json.dumps(metadata,indent=2),encoding='utf-8')
print(json.dumps(metadata),flush=True)
