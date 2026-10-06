"""Read-only live feature discovery and independent SDK preference comparison."""
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
def invoke(label,args):
    result=subprocess.run([str(EXE),*args],capture_output=True,text=True,encoding='utf-8',timeout=90)
    (DEST/f'lifecycle-{STAMP}-feature-{label}.json').write_text(result.stdout,encoding='utf-8')
    assert result.returncode==0,result.stderr
    value=json.loads(result.stdout);assert value['success']
    return value
offline=invoke('offline',['capabilities'])
assert offline['companyFeatures']['probeState']=='not_queried'
assert all(f['enabled'] is None for f in offline['companyFeatures']['facts'])
live=invoke('live',['capabilities','--live','--company-file',str(COMPANY)])
snapshot=live['companyFeatures'];assert snapshot['probeState']=='succeeded'
version=snapshot['protocolVersion']
assert version in live['connection']['host']['supportedProtocolVersions']
direct=invoke('reference-result',['sdk','run','PreferencesQuery','--protocol',version,'--company-file',str(COMPANY)])
record=direct['results'][0]['data'][0]
for fact in snapshot['facts']:
    path=fact['sourcePath'].split('/');current=record
    assert current['name']==path[0]
    for part in path[1:]:
        current=next((c for c in current['children'] if c['name']==part),None)
        if current is None:break
    if current is None:
        assert fact['state']=='unknown' and fact['enabled'] is None
    else:
        raw=current['value']
        expected={'true':True,'1':True,'false':False,'0':False,'None':False,'SerialNumber':True,'LotNumber':True}.get(raw)
        assert fact['enabled']==expected
        expected_value=None if expected is None else raw if fact['id']=='serial_or_lot_tracking' else 'true' if expected else 'false'
        assert fact['value']==expected_value
assert all(o['availableInConnectedCompany'] is None for o in live['sdkCatalog']['operations'])
assert 'AutomaticLoginUserName' not in json.dumps(snapshot)
metadata={'protocol':version,'factCount':len(snapshot['facts']),
    'observedCount':sum(f['reason']=='observed' for f in snapshot['facts']),
    'everyFactComparedAgainstDirectSdkQuery':True,'offlineDidNotProbe':True,'usernamesOmittedFromFeatureSnapshot':True,
    'operationAvailabilityNotInferred':True,'scope':'Read-only feature snapshot on designated Test.QBW; not a permission or every-operation availability test.'}
(DEST/f'lifecycle-{STAMP}-feature-discovery-verification.json').write_text(json.dumps(metadata,indent=2),encoding='utf-8')
print(json.dumps(metadata),flush=True)
