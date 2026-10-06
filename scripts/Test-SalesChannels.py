"""Verify documented SDK16 sales channel fields in the designated synthetic company."""
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
def leaf(record,key):return next((c['value'] for c in record['children'] if c['name']==key),None)
def run(label,calls,apply=False):
    path=DEST/f'lifecycle-{STAMP}-sales-channel-{label}-plan.json'
    path.write_text(json.dumps({'protocolVersion':'16.0','country':'US','onError':'stopOnError','requests':calls}),encoding='utf-8')
    args=[str(EXE),'sdk','batch','--input',str(path),'--company-file',str(COMPANY)]
    if apply:args+=['--apply']
    result=subprocess.run(args,capture_output=True,text=True,encoding='utf-8')
    path.with_name(path.name.replace('-plan','-result')).write_text(result.stdout,encoding='utf-8')
    value=json.loads(result.stdout)
    print(json.dumps({'phase':label,'success':value.get('success'),'outcomes':[{k:r.get(k) for k in ('operation','outcome','statusCode','statusMessage')} for r in value.get('results',[])]}),flush=True)
    assert value.get('success'),value.get('error') or value['results']
    return value['results'][0]['data'][0]
record=run('add',[{'requestId':'add','operation':'SalesOrderAdd','fields':{'SalesOrderAdd':{
    'CustomerRef':{'FullName':'SDK154955 Customer'},'TxnDate':'2026-10-05','Memo':'SDK sales channel '+STAMP,
    'SalesOrderLineAdd':[{'ItemRef':{'FullName':'SDK154955 Service'},'Quantity':'1','Rate':'6.00'}],
    'SOChannel':'Ecommerce','StoreName':'Synthetic '+STAMP,'StoreType':'Shopify'}}}],True)
txn=leaf(record,'TxnID')
query=[{'requestId':'query','operation':'SalesOrderQuery','fields':{'TxnID':[txn],'IncludeLineItems':True}}]
record=run('query',query)
assert leaf(record,'SOChannel')=='Ecommerce'
assert leaf(record,'StoreName')=='Synthetic '+STAMP
record=run('modify',[{'requestId':'mod','operation':'SalesOrderMod','fields':{'SalesOrderMod':{
    'TxnID':txn,'EditSequence':leaf(record,'EditSequence'),'StoreName':'Synthetic Updated '+STAMP,'StoreType':'Synthetic'}}}],True)
record=run('verify',query)
assert leaf(record,'StoreName')=='Synthetic Updated '+STAMP
assert leaf(record,'StoreType')=='Synthetic'
assert leaf(record,'SOChannel')=='Ecommerce'
assert leaf(record,'TotalAmount')=='6.00'
print(json.dumps({'verifiedSalesChannelAddAndMod':True,'amountPreserved':True}),flush=True)
