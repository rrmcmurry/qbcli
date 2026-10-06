"""Isolate supported purchase-order linking and verify classes on successful forms."""
from collections import Counter
import datetime
import json
import pathlib
import subprocess

ROOT=pathlib.Path(__file__).resolve().parent.parent;DEST=ROOT/'artifacts/fixtures'
EXE=ROOT/'artifacts/win-x64/qbcli.exe';COMPANY=(ROOT/'QBData/Test.QBW').resolve()
assert COMPANY==pathlib.Path(__import__("os").environ["QBCLI_LIVE_TEST_COMPANY"]).resolve(strict=True)
STAMP=datetime.datetime.now().strftime('%H%M%S');PREFIX='POLINK'+STAMP
def scalar(n,key):
    matches=[c for c in n['children'] if c['name']==key];assert len(matches)<=1
    return matches[0].get('value') if matches else None
def reference(n,key):
    matches=[c for c in n['children'] if c['name']==key];assert len(matches)<=1
    return scalar(matches[0],'ListID') if matches else None
def canonical(n,selected=()):
    return (n['name'],tuple(sorted(n['attributes'].items())),n.get('value'),tuple(canonical(c,selected) for c in n['children']
        if c['name'] not in ('TimeModified','EditSequence') and not(c['name']=='ClassRef' and scalar(n,'TxnLineID') in selected)))
def walk(n):
    yield n
    for c in n['children']:yield from walk(c)
def call(operation,body,key):return {'requestId':key,'operation':operation,'fields':{operation:body}}
def run(label,args,allow_rejection=False):
    proc=subprocess.run([str(EXE),*args,'--company-file',str(COMPANY),'--json'],capture_output=True,text=True,encoding='utf-8')
    (DEST/f'lifecycle-{STAMP}-po-links-{label}-result.json').write_text(proc.stdout,encoding='utf-8')
    value=json.loads(proc.stdout)
    print(json.dumps({'phase':label,'success':value.get('success'),'outcomes':[{k:r.get(k) for k in ('operation','outcome','statusCode')} for r in value.get('results',[])]}),flush=True)
    if allow_rejection:
        assert value.get('results') and all(r['outcome'] in ('succeeded','failed') or (r['outcome']=='unprocessed' and r['statusCode'] in (None,3231)) for r in value['results']), 'Uncertain result: do not replay'
    else:assert proc.returncode==0 and value['success'], 'Keep evidence and do not replay'
    return value
def batch(label,requests,apply=False,allow_rejection=False):
    path=DEST/f'lifecycle-{STAMP}-po-links-{label}-plan.json'
    path.write_text(json.dumps({'protocolVersion':'13.0','country':'US','onError':'continueOnError' if allow_rejection else 'stopOnError','requests':requests}),encoding='utf-8')
    return run(label,['sdk','batch','--input',str(path),'--batch-size','4',*(['--apply'] if apply else [])],allow_rejection)
base=batch('base',[call('AccountAdd',{'Name':PREFIX+' '+name,'AccountType':kind},name) for name,kind in
    [('Expense','Expense'),('Income','Income'),('COGS','CostOfGoodsSold'),('Asset','OtherCurrentAsset')]]+[call('ClassAdd',{'Name':PREFIX+' Target'},'Target')],True)
ids={r['requestId']:scalar(r['data'][0],'ListID') for r in base['results']}
items=batch('items',[
    call('ItemNonInventoryAdd',{'Name':PREFIX+' NI','SalesAndPurchase':{'SalesPrice':'5.00','IncomeAccountRef':{'ListID':ids['Income']},'PurchaseCost':'2.00','ExpenseAccountRef':{'ListID':ids['Expense']}}},'NonInventory'),
    call('ItemInventoryAdd',{'Name':PREFIX+' Inventory','SalesPrice':'5.00','PurchaseCost':'2.00','IncomeAccountRef':{'ListID':ids['Income']},'COGSAccountRef':{'ListID':ids['COGS']},'AssetAccountRef':{'ListID':ids['Asset']}},'Inventory')],True)
item_ids={r['requestId']:scalar(r['data'][0],'ListID') for r in items['results']}
cases={kind+'-'+item+'-'+mode:(kind,item,mode) for kind in ['Bill','ItemReceipt'] for item in ['NonInventory','Inventory'] for mode in ['Whole','Line']}
orders=batch('orders',[call('PurchaseOrderAdd',{'VendorRef':{'FullName':'SDK154955 Vendor'},'TxnDate':'2026-10-05','Memo':PREFIX+' '+key,
    'PurchaseOrderLineAdd':{'ItemRef':{'ListID':item_ids[item]},'Quantity':'2','Rate':'11.00','Amount':'22.00'}},key) for key,(kind,item,mode) in cases.items()],True)
po_records={r['requestId']:r['data'][0] for r in orders['results']}
requests=[]
for key,(kind,item,mode) in cases.items():
    po=po_records[key];line=next(n for n in po['children'] if n['name']=='PurchaseOrderLineRet')
    body={'VendorRef':{'FullName':'SDK154955 Vendor'},'APAccountRef':{'FullName':'SDK154955 AP'},'TxnDate':'2026-10-05','Memo':PREFIX+' '+key,
        'ExpenseLineAdd':{'AccountRef':{'ListID':ids['Expense']},'Amount':'7.00'}}
    if mode=='Whole':body['LinkToTxnID']=[scalar(po,'TxnID')]
    else:body['ItemLineAdd']={'Quantity':'1','Amount':'11.00','LinkToTxn':{'TxnID':scalar(po,'TxnID'),'TxnLineID':scalar(line,'TxnLineID')}}
    requests.append(call(kind+'Add',body,key))
linked=batch('link',requests,True,True)
checks=[{'transactionType':cases[r['requestId']][0],'itemKind':cases[r['requestId']][1],'linkMode':cases[r['requestId']][2],
    'outcome':r['outcome'],'statusCode':r['statusCode'],'billableFieldsRequested':False,'classModificationVerified':False} for r in linked['results']]
successful={r['requestId']:scalar(r['data'][0],'TxnID') for r in linked['results'] if r['outcome']=='succeeded'}
def evidence(label):
    requests=[]
    for key,txn in successful.items():
        requests.append({'requestId':'form-'+key,'operation':cases[key][0]+'Query','fields':{'TxnID':txn,'IncludeLineItems':True,'IncludeLinkedTxns':True}})
        requests.append({'requestId':'po-'+key,'operation':'PurchaseOrderQuery','fields':{'TxnID':scalar(po_records[key],'TxnID'),'IncludeLineItems':True,'IncludeLinkedTxns':True}})
    return {r['requestId']:r['data'][0] for r in batch(label,requests)['results']}
def postings(label):
    fields=DEST/f'lifecycle-{STAMP}-po-links-{label}-fields.json'
    fields.write_text(json.dumps({'TransactionTypeFilter':{'TxnTypeFilter':['Bill','ItemReceipt']},'TransactionDetailLevelFilter':'AllExceptSummary'}),encoding='utf-8')
    value=run(label,['sdk','run','TransactionQuery','--input',str(fields),'--all','--page-size','500']);assert value['complete']
    return [n for n in value['data'] if scalar(n,'TxnID') in successful.values()]
if successful:
    before=evidence('before');before_posts=postings('before-postings')
    for key,txn in successful.items():
        form=before['form-'+key];po=before['po-'+key]
        assert any(scalar(n,'TxnID')==scalar(po,'TxnID') for n in form['children'] if n['name']=='LinkedTxn')
        assert any(scalar(n,'TxnID')==txn for n in po['children'] if n['name']=='LinkedTxn')
    selection=DEST/f'lifecycle-{STAMP}-po-links-selection.json'
    selection.write_text(json.dumps({'accountId':ids['Expense'],'classId':ids['Target'],'transactionTypes':['Bill','ItemReceipt']}),encoding='utf-8')
    args=['bulk','reclassify','--input',str(selection),'--page-size','25','--batch-size','4']
    preview=run('bulk-preview',args)
    expected=sum(2 if cases[key][1]=='NonInventory' else 1 for key in successful)
    assert preview['counts']['transactionsToChange']==len(successful) and preview['counts']['linesToChange']==expected
    applied=run('bulk-apply',[*args,'--apply'])
    assert all(v['classVerified'] and v['otherFieldsPreserved'] for v in applied['verification'])
    assert len(applied['execution']['results'])==len(successful)
    (DEST/f'lifecycle-{STAMP}-po-links-bulk-execution-result.json').write_text(json.dumps(applied['execution'],indent=2),encoding='utf-8')
    after=evidence('after');after_posts=postings('after-postings')
    assert Counter(map(canonical,before_posts))==Counter(map(canonical,after_posts))
    for key,txn in successful.items():
        change=next(c for c in applied['changes'] if c['txnId']==txn);selected=set(change['lineIds'])
        assert canonical(before['form-'+key],selected)==canonical(after['form-'+key],selected)
        assert canonical(before['po-'+key])==canonical(after['po-'+key])
        lines={scalar(n,'TxnLineID'):n for n in walk(after['form-'+key]) if n['name'] in ('ItemLineRet','ExpenseLineRet')}
        assert all(reference(lines[line],'ClassRef')==ids['Target'] for line in selected)
        check=next(c for c in checks if (c['transactionType'],c['itemKind'],c['linkMode'])==cases[key])
        check.update(classModificationVerified=True,classesChanged=len(selected),purchaseFormFieldsAndBothLinkDirectionsPreserved=True,
            purchaseOrderFieldsAndReceivingStatePreserved=True,allReturnedPostingFieldsPreserved=True,
            itemClassChanged=cases[key][1]=='NonInventory',inventoryItemClassChanged=False)
    repeat=run('bulk-repeat-preview',args);assert repeat['counts']['transactionsToChange']==repeat['counts']['linesToChange']==0
for check in checks:
    check['scope']='Small Test.QBW Bill/ItemReceipt PO-link control without billable fields. Successful non-inventory forms use combined expense/item classes; inventory forms change expense classes only. No inventory item-class, billable linking, every link shape or other-release proof.'
(DEST/f'lifecycle-{STAMP}-po-links-verification.json').write_text(json.dumps(checks,indent=2),encoding='utf-8')
print(json.dumps(checks),flush=True)
