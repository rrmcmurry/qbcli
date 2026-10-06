"""Read-only installation-scoped subscription probes; never creates callbacks."""
import json
import pathlib
import subprocess
ROOT=pathlib.Path(__file__).resolve().parent.parent
dest=ROOT/'artifacts/fixtures'
calls=[{'requestId':str(i+1),'operation':name,'fields':{'SubscriberID':'{F6A6DC28-4823-4CEE-B497-8B20CE70F34B}'}}
       for i,name in enumerate(['DataEventSubscriptionQuery','UIEventSubscriptionQuery','UIExtensionSubscriptionQuery'])]
plan=dest/'subscriptions-read-plan.json'
plan.write_text(json.dumps({'protocolVersion':'13.0','country':'US','onError':'continueOnError','requests':calls}),encoding='utf-8')
run=subprocess.run([str(ROOT/'artifacts/win-x64/qbcli.exe'),'sdk','subscription-batch','--input',str(plan)],capture_output=True,text=True,encoding='utf-8')
(dest/'subscriptions-read-result.json').write_text(run.stdout,encoding='utf-8')
result=json.loads(run.stdout)
if 'error' in result:raise RuntimeError(result['error'])
print(json.dumps({'scope':result['executionScope'],'success':result['success'],'results':[{k:r[k] for k in ('operation','outcome','statusCode')} for r in result['results']]}))
