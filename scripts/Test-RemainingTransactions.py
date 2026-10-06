"""Additional supported SDK records; hard-coded expendable Test.QBW only."""
import datetime,json,pathlib,subprocess
ROOT=pathlib.Path(__file__).resolve().parent.parent
DEST=ROOT/'artifacts/fixtures'
EXE=ROOT/'artifacts/win-x64/qbcli.exe'
COMPANY=ROOT/'QBData/Test.QBW'
stamp=datetime.datetime.now().strftime('%H%M%S')
name='MORE'+stamp
def ref(n):return {'FullName':'SDK154955 '+n}
def call(op,f):return (op,{op:f})
def run(label,calls):
    path=DEST/f'lifecycle-{stamp}-{label}-plan.json'
    path.write_text(json.dumps({'protocolVersion':'13.0','country':'US','onError':'continueOnError','requests':[{'requestId':str(i+1),'operation':op,'fields':f} for i,(op,f) in enumerate(calls)]}),encoding='utf-8')
    proc=subprocess.run([str(EXE),'sdk','batch','--input',str(path),'--company-file',str(COMPANY),'--apply','--batch-size','100'],capture_output=True,text=True,encoding='utf-8')
    path.with_name(path.name.replace('-plan','-result')).write_text(proc.stdout,encoding='utf-8')
    result=json.loads(proc.stdout)
    if 'error' in result:raise RuntimeError(result['error'])
    print(json.dumps({'phase':label,'outcomes':[{k:r.get(k) for k in ('operation','outcome','statusCode','statusMessage')} for r in result['results']]}),flush=True)
    return result['results']
def leaf(n,k):return next((c['value'] for c in n['children'] if c['name']==k),None)
base=json.loads((DEST/'SDK154955-transactions-result.json').read_text(encoding='utf-8'))
check=next(r['data'][0] for r in base['results'] if r['operation']=='CheckAdd')
credit=next(r['data'][0] for r in base['results'] if r['operation']=='CreditMemoAdd')
calls=[call('ItemSalesTaxGroupAdd',{'Name':name+' TaxGroup','ItemSalesTaxRef':{'FullName':'EXT162827 Tax'}}),
       call('EstimateAdd',{'CustomerRef':ref('Customer'),'EstimateLineAdd':{'ItemRef':ref('Service'),'Quantity':'1','Rate':'5.00'}}),
       call('ClearedStatusMod',{'TxnID':leaf(check,'TxnID'),'ClearedStatus':'Cleared'}),
       call('PaymentMethodAdd',{'Name':name+' Card','PaymentMethodType':'OtherCreditCard'}),
       call('ARRefundCreditCardAdd',{'CustomerRef':ref('Customer'),'RefundFromAccountRef':ref('Bank'),'ARAccountRef':ref('AR'),'PaymentMethodRef':{'FullName':name+' Card'},'TxnDate':'2026-10-05','RefundAppliedToTxnAdd':{'TxnID':leaf(credit,'TxnID'),'RefundAmount':'1.00'}}),
       call('SalesTaxPaymentCheckAdd',{'PayeeEntityRef':ref('Vendor'),'TxnDate':'2026-10-05','BankAccountRef':ref('Bank'),'IsToBePrinted':False,'SalesTaxPaymentCheckLineAdd':{'ItemSalesTaxRef':{'FullName':'EXT162827 Tax'},'Amount':'1.00'}})]
for kind in ('UndepositedFunds','RetainedEarnings','OpeningBalanceEquity','SalesTaxPayable'):
    calls.append(call('SpecialAccountAdd',{'SpecialAccountType':kind}))
for kind in ('FinanceCharge','ReimbursableExpenseGroup','ReimbursableExpenseSubtotal'):
    calls.append(call('SpecialItemAdd',{'SpecialItemType':kind}))
result=run('remaining-transactions',calls)
mods=[]
for r in result:
    if r['outcome']!='succeeded' or not r['data']:continue
    obj=r['data'][0]
    if r['operation']=='EstimateAdd':mods.append(call('EstimateMod',{'TxnID':leaf(obj,'TxnID'),'EditSequence':leaf(obj,'EditSequence'),'Memo':'Verified additional estimate modifier'}))
    if r['operation']=='ItemSalesTaxGroupAdd':mods.append(call('ItemSalesTaxGroupMod',{'ListID':leaf(obj,'ListID'),'EditSequence':leaf(obj,'EditSequence'),'ItemDesc':'Verified tax group modifier'}))
    if r['operation']=='SalesTaxPaymentCheckAdd':mods.append(call('SalesTaxPaymentCheckMod',{'TxnID':leaf(obj,'TxnID'),'EditSequence':leaf(obj,'EditSequence'),'Memo':'Verified tax payment modifier'}))
if mods:run('remaining-modifiers',mods)
