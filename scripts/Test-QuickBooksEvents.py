"""Own temporary data subscription, live ClassAdd notification, and cleanup.
Only Test.QBW receives accounting mutations. Subscription identity is fresh and
installation-scoped; no existing subscription is changed or deleted.
"""
import datetime
import json
import pathlib
import subprocess
import time
import uuid
ROOT=pathlib.Path(__file__).resolve().parent.parent
COMPANY=(ROOT/'QBData/Test.QBW').resolve()
assert COMPANY==pathlib.Path(__import__("os").environ["QBCLI_LIVE_TEST_COMPANY"]).resolve(strict=True)
EXE=ROOT/'artifacts/win-x64/qbcli.exe'
DEST=ROOT/'artifacts/fixtures'
STAMP=datetime.datetime.now().strftime('%H%M%S')
CLSID='{'+str(uuid.uuid4())+'}'
SUBSCRIBER='{'+str(uuid.uuid4())+'}'
UI_SUBSCRIBER='{'+str(uuid.uuid4())+'}'
MENU_SUBSCRIBER='{'+str(uuid.uuid4())+'}'
settings=DEST/f'lifecycle-{STAMP}-qb-events-settings.json'
settings.write_text(json.dumps({'callbackClsid':CLSID,'durationSeconds':55,'registerCallback':True}),encoding='utf-8')
journal=DEST/f'lifecycle-{STAMP}-qb-events.jsonl'
stdout=(DEST/f'lifecycle-{STAMP}-qb-listener-summary.json').open('w',encoding='utf-8')
stderr=(DEST/f'lifecycle-{STAMP}-qb-listener-stderr.txt').open('w',encoding='utf-8')
listener=subprocess.Popen([str(EXE),'events','listen','--input',str(settings),'--company-file',str(COMPANY),'--journal',str(journal),'--apply'],stdout=stdout,stderr=stderr,creationflags=subprocess.CREATE_NO_WINDOW)
def run(label,op,fields,subscription=False,apply=False):
    path=DEST/f'lifecycle-{STAMP}-qb-events-{label}-plan.json'
    path.write_text(json.dumps({'protocolVersion':'13.0','country':'US','onError':'stopOnError','requests':[{'requestId':label,'operation':op,'fields':fields}]}),encoding='utf-8')
    args=[str(EXE),'sdk','subscription-batch' if subscription else 'batch','--input',str(path)]
    if apply:args+=['--apply']
    if not subscription:args+=['--company-file',str(COMPANY)]
    result=subprocess.run(args,capture_output=True,text=True,encoding='utf-8')
    path.with_name(path.name.replace('-plan','-result')).write_text(result.stdout,encoding='utf-8')
    value=json.loads(result.stdout)
    print(json.dumps({'phase':label,'success':value.get('success'),'outcomes':[{k:r.get(k) for k in ('operation','outcome','statusCode','statusMessage')} for r in value.get('results',[])]}),flush=True)
    if 'error' in value:raise RuntimeError(value['error'])
    return value
metadata={}
try:
    deadline=time.monotonic()+8
    while time.monotonic()<deadline:
        if listener.poll() is not None:raise RuntimeError('Listener ended before registration')
        if journal.exists() and any(json.loads(line).get('kind')=='listener_ready' for line in journal.read_text().splitlines()):break
        time.sleep(.05)
    else:raise RuntimeError('Listener not ready')
    add=run('subscribe','DataEventSubscriptionAdd',{'DataEventSubscriptionAdd':{'SubscriberID':SUBSCRIBER,
        'COMCallbackInfo':{'AppName':'McMurry Software QuickBooks CLI','CLSID':CLSID},'DeliveryPolicy':'DeliverOnlyIfRunning',
        'TrackLostEvents':'None','DeliverOwnEvents':True,'ListEventSubscription':[{'ListEventType':'Class','ListEventOperation':['Add']}]}},True,True)
    metadata['subscriptionAccepted']=add['success']
    if add['success']:
        query=run('query','DataEventSubscriptionQuery',{'SubscriberID':SUBSCRIBER},True)
        assert query['success'] and len(query['results'][0]['data'])==1
        mutation=run('class','ClassAdd',{'ClassAdd':{'Name':'EVT'+STAMP}},False,True)
        assert mutation['success']
        entity=mutation['results'][0]['data'][0]
        list_id=next(c['value'] for c in entity['children'] if c['name']=='ListID')
        deadline=time.monotonic()+5
        while time.monotonic()<deadline:
            events=[json.loads(line) for line in journal.read_text().splitlines() if json.loads(line).get('kind')=='event']
            if events:break
            time.sleep(.1)
        metadata['quickBooksEventReceived']=any(any(c['name']=='ListEvent' and any(f['name']=='ListID' and f['value']==list_id for f in c['children']) for c in e['notification']['data']['children']) for e in events)
        run('recovery-info','DataEventRecoveryInfoQuery',{'SubscriberID':SUBSCRIBER})
        callback={'AppName':'McMurry Software QuickBooks CLI','CLSID':CLSID}
        ui=run('ui-subscribe','UIEventSubscriptionAdd',{'UIEventSubscriptionAdd':{'SubscriberID':UI_SUBSCRIBER,'COMCallbackInfo':callback,'DeliveryPolicy':'DeliverOnlyIfRunning',
            'CompanyFileEventSubscription':{'CompanyFileEventOperation':['Open','Close']}}},True,True)
        metadata['uiSubscriptionAccepted']=ui['success']
        if ui['success']:run('ui-query','UIEventSubscriptionQuery',{'SubscriberID':UI_SUBSCRIBER},True)
        menu=run('menu-subscribe','UIExtensionSubscriptionAdd',{'UIExtensionSubscriptionAdd':{'SubscriberID':MENU_SUBSCRIBER,'COMCallbackInfo':callback,
            'MenuExtensionSubscription':{'AddToMenu':'Company','MenuItem':{'MenuText':'McMurry SDK Test '+STAMP,'EventTag':'qbcli-test-'+STAMP}}}},True,True)
        metadata['menuSubscriptionAccepted']=menu['success']
        if menu['success']:run('menu-query','UIExtensionSubscriptionQuery',{'SubscriberID':MENU_SUBSCRIBER},True)
finally:
    # A fresh subscriber ID belongs to this test even if its add response was uncertain.
    cleanup=run('unsubscribe','SubscriptionDel',{'SubscriberID':SUBSCRIBER,'SubscriptionType':'Data'},True,True)
    metadata['ownedSubscriptionRemoved']=cleanup['success']
    run('verify-removed','DataEventSubscriptionQuery',{'SubscriberID':SUBSCRIBER},True)
    for kind,subscriber in [('UI',UI_SUBSCRIBER),('UIExtension',MENU_SUBSCRIBER)]:
        try:
            result=run(kind+'-unsubscribe','SubscriptionDel',{'SubscriberID':subscriber,'SubscriptionType':kind},True,True)
            query_op='UIEventSubscriptionQuery' if kind=='UI' else 'UIExtensionSubscriptionQuery'
            read=run(kind+'-verify-removed',query_op,{'SubscriberID':subscriber},True)
            metadata[kind+'SubscriptionAbsentAfterCleanup']=read['success'] and read['results'][0]['outcome']=='no_match'
        except Exception as ex:metadata[kind+'CleanupError']=type(ex).__name__
    listener.wait(timeout=60)
    stdout.close();stderr.close()
    metadata['listenerEnded']=listener.returncode==0
    import winreg
    try:
        with winreg.OpenKey(winreg.HKEY_CURRENT_USER,'Software\\Classes\\CLSID\\'+CLSID):pass
        metadata['temporaryRegistrationRemoved']=False
    except FileNotFoundError:metadata['temporaryRegistrationRemoved']=True
    (DEST/f'lifecycle-{STAMP}-qb-events-verification.json').write_text(json.dumps(metadata,indent=2),encoding='utf-8')
print(json.dumps(metadata),flush=True)
