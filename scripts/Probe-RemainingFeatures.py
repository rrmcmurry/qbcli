"""Documented feature/enum probes on the designated expendable company only."""
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
schemas=json.loads((ROOT/'src/QbCli.QuickBooks/Resources/sdk-reference.json').read_text())['operations']
special=next(o for o in schemas if o['name']=='SpecialAccountAdd')['request']['children'][0]['children'][0]['enumValues']
calls=[{'requestId':'special-'+str(i),'operation':'SpecialAccountAdd','fields':{'SpecialAccountAdd':{'SpecialAccountType':value}}} for i,value in enumerate(special)]
for op,body in [
    ('Form1099CategoryAccountMappingMod',{'IsFiling1099Misc':True,'FormType':'NEC','CategoryAccountMappingMod':[{'MappingCategory':'BOX1','MappingAccount':[{'AccountRef':{'FullName':'SDK154955 Expense'},'ForceMapping':True}],'Threshold':'600.00'}]}),
    ('CurrencyAdd',{'Name':'Synthetic Test '+STAMP,'CurrencyCode':'XTS'}),
    ('WorkersCompCodeAdd',{'Name':'W'+STAMP,'Desc':'SDK synthetic test','RateEntry':[{'Rate':'1.00','EffectiveDate':'2026-10-05'}]}),
    ('InventorySiteAdd',{'Name':'Synthetic Site '+STAMP}),
    ('LeadAdd',{'FullName':'Synthetic Lead '+STAMP,'Status':'Warm'}),
    ('ItemAssembliesCanBuildQuery',{'ItemInventoryAssemblyRef':{'FullName':'EXT162827 Assembly'},'TxnDate':'2026-10-05'})]:
    calls.append({'requestId':op,'operation':op,'fields':body if op.endswith('Query') else {op:body}})
plan=DEST/f'lifecycle-{STAMP}-remaining-features-plan.json'
plan.write_text(json.dumps({'protocolVersion':'16.0','country':'US','onError':'continueOnError','requests':calls}),encoding='utf-8')
result=subprocess.run([str(EXE),'sdk','batch','--input',str(plan),'--company-file',str(COMPANY),'--apply','--batch-size','100'],capture_output=True,text=True,encoding='utf-8')
plan.with_name(plan.name.replace('-plan','-result')).write_text(result.stdout,encoding='utf-8')
value=json.loads(result.stdout)
if 'error' in value:raise RuntimeError(value['error'])
print(json.dumps([{k:r.get(k) for k in ('requestId','operation','outcome','statusCode','statusMessage')} for r in value['results']]),flush=True)
special_checks=[{'specialAccountType':special[int(r['requestId'].split('-')[1])],'outcome':r['outcome'],'statusCode':r['statusCode']} for r in value['results'] if r['requestId'].startswith('special-')]
(DEST/f'lifecycle-{STAMP}-special-account-coverage.json').write_text(json.dumps(special_checks,indent=2),encoding='utf-8')
if any(r['operation']=='Form1099CategoryAccountMappingMod' and r['outcome']=='succeeded' for r in value['results']):
    query=DEST/f'lifecycle-{STAMP}-mapping-verify-plan.json'
    query.write_text(json.dumps({'protocolVersion':'16.0','requests':[{'operation':'Form1099CategoryAccountMappingQuery','fields':{'FormType':'NEC'}}]}),encoding='utf-8')
    result=subprocess.run([str(EXE),'sdk','batch','--input',str(query),'--company-file',str(COMPANY)],capture_output=True,text=True,encoding='utf-8')
    query.with_name(query.name.replace('-plan','-result')).write_text(result.stdout,encoding='utf-8')
    read=json.loads(result.stdout)
    assert read['success']
    leaf=lambda node,key:next((c['value'] for c in node['children'] if c['name']==key),None)
    record=read['results'][0]['data'][0]
    category=next(c for c in record['children'] if c['name']=='CategoryAccountMapping' and leaf(c,'MappingCategory')=='BOX1')
    assert leaf(category,'Threshold')=='600.00'
    account=next(c for c in category['children'] if c['name']=='AccountRef')
    assert leaf(account,'FullName')=='SDK154955 Expense'
    print(json.dumps({'form1099MappingReadBack':True}),flush=True)
