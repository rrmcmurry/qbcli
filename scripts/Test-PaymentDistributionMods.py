"""Modify confirmed synthetic payments; verify separately, never replay mutations."""
import datetime
import json
import pathlib
import subprocess
import sys
ROOT=pathlib.Path(__file__).resolve().parent.parent;DEST=ROOT/'artifacts/fixtures'
COMPANY=(ROOT/'QBData/Test.QBW').resolve();EXE=ROOT/'artifacts/win-x64/qbcli.exe'
assert COMPANY==pathlib.Path(__import__("os").environ["QBCLI_LIVE_TEST_COMPANY"]).resolve(strict=True)
SOURCE=sys.argv[1];assert len(SOURCE)==6 and SOURCE.isdigit()
STAMP=datetime.datetime.now().strftime('%H%M%S');PREFIX=f'lifecycle-{STAMP}-payment-distribution'
def scalar(n,k):
    found=[c for c in n['children'] if c['name']==k];assert len(found)<=1
    return found[0].get('value') if found else None
def source(label):
    v=json.loads((DEST/f'lifecycle-{SOURCE}-credit-discounts-{label}-result.json').read_text(encoding='utf-8'))
    assert v['success'] and pathlib.Path(v['companyFile'])==COMPANY
    assert all(r['outcome']=='succeeded' and r['statusCode']==0 for r in v['results']);return v
seed=source('seed');ids={r['requestId']:scalar(r['data'][0],'TxnID') for r in seed['results']}
base=source('base');party={r['requestId']:scalar(r['data'][0],'ListID') for r in base['results']}
applied=source('apply');payments={r['requestId']:scalar(r['data'][0],'TxnID') for r in applied['results']}
manifest={'sourceFixture':SOURCE,'companyFile':str(COMPANY),'documentIds':ids,'paymentIds':payments,'partyIds':party}
path=DEST/(PREFIX+'-manifest.json');assert not path.exists();path.write_text(json.dumps(manifest,indent=2),encoding='utf-8')
def call(op,fields,key):return {'requestId':key,'operation':op,'fields':fields}
def batch(label,requests,apply=False):
    path=DEST/(PREFIX+'-'+label+'-plan.json');assert not path.exists()
    path.write_text(json.dumps({'protocolVersion':'13.0','country':'US','requests':requests}),encoding='utf-8')
    x=subprocess.run([str(EXE),'sdk','batch','--input',str(path),*(['--apply'] if apply else []),'--company-file',str(COMPANY),'--json'],capture_output=True,text=True,encoding='utf-8')
    result=DEST/(PREFIX+'-'+label+'-result.json');assert not result.exists();result.write_text(x.stdout,encoding='utf-8')
    v=json.loads(x.stdout);print(json.dumps({'fixture':STAMP,'phase':label,'success':v.get('success'),'outcomes':[{k:r.get(k) for k in ('operation','statusCode','outcome')} for r in v.get('results',[])]}),flush=True)
    assert x.returncode==0 and v['success'] and pathlib.Path(v['companyFile'])==COMPANY,'Inspect retained result/journal; never replay'
    return v
queries=[call(kind+'Query',{'TxnID':payments[key],'IncludeLineItems':True},key) for key,kind in [('receive','ReceivePayment'),('pay-bills','BillPaymentCheck')]]
queries += [call(kind+'Query',{'TxnID':ids[key],'IncludeLinkedTxns':True},key) for key,kind in [('invoice','Invoice'),('customer-credit','CreditMemo'),('bill-a','Bill'),('bill-b','Bill'),('vendor-credit','VendorCredit')]]
queries += [call('BillToPayQuery',{'PayeeEntityRef':{'ListID':party['vendor']}},'payable')]
before=batch('before',queries)
def records(v):return {r['requestId']:r['data'][0] for r in v['results'] if r['data'] and r['requestId']!='payable'}
fresh=records(before);mods=[]
# Refuse fixtures whose current distributions no longer match the intended
# control, rather than relying on historical creation responses.
for key,documents in [('receive',['invoice']),('pay-bills',['bill-a','bill-b'])]:
    apps={scalar(c,'TxnID'):c for c in fresh[key]['children'] if c['name']=='AppliedToTxnRet'}
    assert set(apps)=={ids[k] for k in documents}
    for document in documents:
        app=apps[ids[document]]
        assert scalar(app,'Amount')=='25.00' and scalar(app,'DiscountAmount')=='5.00'
        credit=ids['customer-credit' if document=='invoice' else 'vendor-credit']
        links=[c for c in app['children'] if c['name']=='LinkedTxn' and scalar(c,'TxnID')==credit]
        assert len(links)==1
        assert scalar(fresh[document],'IsPaid')=='true'
for key,kind in [('receive','ReceivePayment'),('pay-bills','BillPaymentCheck')]:
    assert scalar(fresh[key],'TxnID')==payments[key]
    mods.append(call(kind+'Mod',{kind+'Mod':{'TxnID':payments[key],'EditSequence':scalar(fresh[key],'EditSequence'),'Memo':'Payment distribution control '+STAMP}},key))
batch('memo',mods,True);memo=batch('memo-readback',queries);fresh=records(memo)
for key in payments:
    assert scalar(fresh[key],'Memo')=='Payment distribution control '+STAMP
def app(key,cash,discount):
    return {'TxnID':ids[key],'PaymentAmount':cash,'DiscountAmount':discount,'DiscountAccountRef':{'FullName':'SDK154955 '+('Expense' if key=='invoice' else 'Income')},'DiscountClassRef':{'ListID':party['class']}}
batch('redistribute',[
    call('ReceivePaymentMod',{'ReceivePaymentMod':{'TxnID':payments['receive'],'EditSequence':scalar(fresh['receive'],'EditSequence'),'TotalAmount':'20.00','AppliedToTxnMod':app('invoice','20.00','10.00')}},'receive'),
    call('BillPaymentCheckMod',{'BillPaymentCheckMod':{'TxnID':payments['pay-bills'],'EditSequence':scalar(fresh['pay-bills'],'EditSequence'),'AppliedToTxnMod':[app('bill-a','20.00','10.00'),app('bill-b','30.00','0.00')]}},'pay-bills')],True)
batch('redistribute-readback',queries)
print(json.dumps({'fixtureStamp':STAMP,'sourceFixture':SOURCE,'scope':'Run independent retained-evidence verifier; source results are historical snapshots, not current-state proof.'}),flush=True)
