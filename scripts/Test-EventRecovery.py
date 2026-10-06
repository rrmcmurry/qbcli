"""Owned subscription recovery-state operations, only in expendable Test.QBW.

An empty recovery state tests request acceptance/cleanup, not lost-event delivery.
Every subscriber/CLSID is freshly owned; existing subscriptions are untouched.
"""
import datetime
import json
import pathlib
import subprocess
import time
import uuid
import winreg

ROOT = pathlib.Path(__file__).resolve().parent.parent
COMPANY = (ROOT / 'QBData/Test.QBW').resolve()
assert COMPANY == pathlib.Path(__import__("os").environ["QBCLI_LIVE_TEST_COMPANY"]).resolve(strict=True)
EXE = ROOT / 'artifacts/win-x64/qbcli.exe'
DEST = ROOT / 'artifacts/fixtures'
STAMP = datetime.datetime.now().strftime('%H%M%S')
CLSID = '{' + str(uuid.uuid4()) + '}'
SUBSCRIBER = '{' + str(uuid.uuid4()) + '}'

def run(label, operation, fields, subscription=False, apply=False, expected_error=None):
    path = DEST / f'lifecycle-{STAMP}-event-recovery-{label}-plan.json'
    path.write_text(json.dumps({'protocolVersion': '13.0', 'country': 'US', 'onError': 'stopOnError',
        'requests': [{'requestId': label, 'operation': operation, 'fields': fields}]}), encoding='utf-8')
    args = [str(EXE), 'sdk', 'subscription-batch' if subscription else 'batch', '--input', str(path)]
    if not subscription:
        args += ['--company-file', str(COMPANY)]
    if apply:
        args.append('--apply')
    proc = subprocess.run(args, capture_output=True, text=True, encoding='utf-8')
    path.with_name(path.name.replace('-plan', '-result')).write_text(proc.stdout, encoding='utf-8')
    value = json.loads(proc.stdout)
    print(json.dumps({'phase': label, 'success': value.get('success'), 'outcomes': [
        {k: r.get(k) for k in ('operation', 'outcome', 'statusCode')} for r in value.get('results', [])]}), flush=True)
    if expected_error is None:
        assert value['success'], value
    else:
        assert not value['success'] and len(value['results']) == 1
        assert value['results'][0]['outcome'] == 'failed' and value['results'][0]['statusCode'] == expected_error
    return value

settings = DEST / f'lifecycle-{STAMP}-event-recovery-settings.json'
settings.write_text(json.dumps({'callbackClsid': CLSID, 'durationSeconds': 25, 'registerCallback': True}), encoding='utf-8')
journal = DEST / f'lifecycle-{STAMP}-event-recovery-listener.jsonl'
metadata = {'scope': 'Empty recovery state for a fresh owned subscriber in Test.QBW; actual lost-event recovery is unverified.'}
with (DEST / f'lifecycle-{STAMP}-event-recovery-listener-summary.json').open('w', encoding='utf-8') as stdout, \
        (DEST / f'lifecycle-{STAMP}-event-recovery-listener-stderr.txt').open('w', encoding='utf-8') as stderr:
    listener = subprocess.Popen([str(EXE), 'events', 'listen', '--input', str(settings), '--company-file', str(COMPANY),
        '--journal', str(journal), '--apply'], stdout=stdout, stderr=stderr, creationflags=subprocess.CREATE_NO_WINDOW)
    try:
        deadline = time.monotonic() + 8
        while time.monotonic() < deadline:
            assert listener.poll() is None, 'Listener ended before registration'
            if journal.exists() and any(json.loads(line).get('kind') == 'listener_ready' for line in journal.read_text().splitlines()):
                break
            time.sleep(.05)
        else:
            raise RuntimeError('Listener not ready')
        run('subscribe', 'DataEventSubscriptionAdd', {'DataEventSubscriptionAdd': {
            'SubscriberID': SUBSCRIBER, 'COMCallbackInfo': {'AppName': 'McMurry Software QuickBooks CLI', 'CLSID': CLSID},
            'DeliveryPolicy': 'DeliverOnlyIfRunning', 'TrackLostEvents': 'All', 'DeliverOwnEvents': True,
            'ListEventSubscription': [{'ListEventType': 'Class', 'ListEventOperation': ['Add', 'Modify', 'Delete']}]}}, True, True)
        metadata['ownedSubscriptionAccepted'] = True
        before = run('before', 'DataEventRecoveryInfoQuery', {'SubscriberID': SUBSCRIBER})
        assert before['results'][0]['outcome'] == 'no_match'
        deleted = run('delete', 'DataEventRecoveryInfoDel', {'SubscriberID': SUBSCRIBER}, apply=True, expected_error=3120)
        assert not deleted['mutatesCompany'] and deleted['mutatesRecoveryState'] and not deleted['affectsUserInterface']
        header = json.loads(pathlib.Path(deleted['journalFile']).read_text(encoding='utf-8').splitlines()[0])
        assert header['mutatesRecoveryState'] and not header['mutatesCompany'] and not header['affectsUserInterface']
        after = run('after', 'DataEventRecoveryInfoQuery', {'SubscriberID': SUBSCRIBER})
        metadata.update(beforeOutcome=before['results'][0]['outcome'], deleteOutcome=deleted['results'][0]['outcome'],
            afterOutcome=after['results'][0]['outcome'], recoveryStateAbsentAfterDelete=after['results'][0]['outcome'] == 'no_match',
            absentRecoveryDeleteReturnsNotFound=deleted['results'][0]['statusCode'] == 3120, recoveryRecordDeletionVerified=False,
            recoveryEffectMetadataVerified=True)
        assert metadata['recoveryStateAbsentAfterDelete']
        live_subscription = run('subscription-retained', 'DataEventSubscriptionQuery', {'SubscriberID': SUBSCRIBER}, True)
        assert len(live_subscription['results'][0]['data']) == 1
        metadata['subscriptionPreservedByRecoveryDelete'] = True
    finally:
        # The fresh identity is ours even if an add response became uncertain.
        try:
            run('unsubscribe', 'SubscriptionDel', {'SubscriberID': SUBSCRIBER, 'SubscriptionType': 'Data'}, True, True)
            absent = run('verify-removed', 'DataEventSubscriptionQuery', {'SubscriberID': SUBSCRIBER}, True)
            metadata['ownedSubscriptionAbsentAfterCleanup'] = absent['results'][0]['outcome'] == 'no_match'
            assert metadata['ownedSubscriptionAbsentAfterCleanup']
        finally:
            listener.wait(timeout=35)
            metadata['listenerEnded'] = listener.returncode == 0
            try:
                with winreg.OpenKey(winreg.HKEY_CURRENT_USER, 'Software\\Classes\\CLSID\\' + CLSID):
                    pass
                metadata['temporaryRegistrationRemoved'] = False
            except FileNotFoundError:
                metadata['temporaryRegistrationRemoved'] = True
            (DEST / f'lifecycle-{STAMP}-event-recovery-verification.json').write_text(json.dumps(metadata, indent=2), encoding='utf-8')
print(json.dumps(metadata), flush=True)
