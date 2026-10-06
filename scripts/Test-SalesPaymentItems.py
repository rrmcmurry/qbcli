"""Payment-item preservation recipe only on the authorized disposable company."""
from collections import Counter
from decimal import Decimal
import datetime
import argparse
import json
import pathlib
import subprocess

ROOT=pathlib.Path(__file__).resolve().parent.parent; DEST=ROOT/'artifacts/fixtures'
EXE=ROOT/'artifacts/win-x64/qbcli.exe'; COMPANY=(ROOT/'QBData/Test.QBW').resolve()
assert COMPANY==pathlib.Path(__import__("os").environ["QBCLI_LIVE_TEST_COMPANY"]).resolve(strict=True)
STAMP=datetime.datetime.now().strftime('%H%M%S'); PREFIX='PAYITEM'+STAMP
TYPES=['Invoice','SalesReceipt','CreditMemo']
parser=argparse.ArgumentParser(description=__doc__);parser.add_argument('--public-bulk',action='store_true');options=parser.parse_args()
def scalar(n,k):
    values=[c for c in n['children'] if c['name']==k];assert len(values)<=1
    return values[0].get('value') if values else None
def ref(n,k):return next((scalar(c,'ListID') for c in n['children'] if c['name']==k),None)
def call(op,body,key):return {'requestId':key,'operation':op,'fields':{op:body}}
def run(label,args):
    proc=subprocess.run([str(EXE),*args,'--company-file',str(COMPANY),'--json'],capture_output=True,text=True,encoding='utf-8')
    (DEST/f'lifecycle-{STAMP}-payment-sales-{label}-result.json').write_text(proc.stdout,encoding='utf-8')
    value=json.loads(proc.stdout)
    print(json.dumps({'phase':label,'success':value.get('success'),'outcomes':[{k:r.get(k) for k in ['operation','outcome','statusCode']} for r in value.get('results',[])]}),flush=True)
    assert proc.returncode==0 and value['success'],'Inspect evidence; never replay'
    return value
def batch(label,requests,apply=False):
    path=DEST/f'lifecycle-{STAMP}-payment-sales-{label}-plan.json'
    path.write_text(json.dumps({'protocolVersion':'13.0','country':'US','onError':'stopOnError','requests':requests}),encoding='utf-8')
    return run(label,['sdk','batch','--input',str(path),*(['--apply'] if apply else [])])
base=batch('base',[call('AccountAdd',{'Name':PREFIX+' Income','AccountType':'Income'},'income'),call('ClassAdd',{'Name':PREFIX+' Target'},'class'),call('AccountAdd',{'Name':PREFIX+' Bank','AccountType':'Bank'},'bank')],True)
ids={r['requestId']:scalar(r['data'][0],'ListID') for r in base['results']}
items=batch('items',[call('ItemServiceAdd',{'Name':PREFIX+' Service','SalesOrPurchase':{'Price':'10.00','AccountRef':{'ListID':ids['income']}}},'service'),
    call('ItemPaymentAdd',{'Name':PREFIX+' Payment','DepositToAccountRef':{'ListID':ids['bank']}},'payment')],True)
itemids={r['requestId']:scalar(r['data'][0],'ListID') for r in items['results']}
requests=[]
for kind in TYPES:
    body={'CustomerRef':{'FullName':'SDK154955 Customer'},'TxnDate':'2026-10-05','Memo':PREFIX+' '+kind,
        kind+'LineAdd':[{'ItemRef':{'ListID':itemids['service']},'Quantity':'1','Amount':'10.00'},
            {'ItemRef':{'ListID':itemids['service']},'Quantity':'1','Amount':'11.00'},
            {'ItemRef':{'ListID':itemids['payment']},'Amount':'2.00'}]}
    body['DepositToAccountRef' if kind=='SalesReceipt' else 'ARAccountRef']={'FullName':'SDK154955 Bank' if kind=='SalesReceipt' else 'SDK154955 AR'}
    requests.append(call(kind+'Add',body,kind))
seed=batch('seed',requests,True);txns={r['requestId']:scalar(r['data'][0],'TxnID') for r in seed['results']}
def forms(label):
    value=batch(label,[{'requestId':k,'operation':k+'Query','fields':{'TxnID':v,'IncludeLineItems':True,**({'IncludeLinkedTxns':True} if k!='SalesReceipt' else {})}} for k,v in txns.items()])
    return {r['requestId']:r['data'][0] for r in value['results']}
def postings(label):
    path=DEST/f'lifecycle-{STAMP}-payment-sales-{label}-fields.json'
    path.write_text(json.dumps({'TransactionTypeFilter':{'TxnTypeFilter':TYPES},'TransactionDetailLevelFilter':'AllExceptSummary'}),encoding='utf-8')
    value=run(label,['sdk','run','TransactionQuery','--input',str(path),'--all','--page-size','500']);assert value['complete']
    return [n for n in value['data'] if scalar(n,'TxnID') in txns.values()]
before=forms('before-forms');oldposts=postings('before-postings');selected={};requests=[]
for kind,record in before.items():
    selected[kind]=set();choices=[]
    for line in record['children']:
        if line['name']!=kind+'LineRet':continue
        identity=scalar(line,'TxnLineID');joined=[p for p in oldposts if scalar(p,'TxnID')==txns[kind] and scalar(p,'TxnType')==kind and scalar(p,'TxnLineID')==identity]
        body={'TxnLineID':identity}
        account=ids['bank'] if ref(line,'ItemRef')==itemids['payment'] else ids['income']
        assert len(joined)==1 and ref(joined[0],'AccountRef')==account
        assert abs(Decimal(scalar(joined[0],'Amount')))==abs(Decimal(scalar(line,'Amount')))
        body.update(OverrideItemAccountRef={'ListID':account})
        if not options.public_bulk or ref(line,'ItemRef')==itemids['payment']:
            body['ClassRef']={'ListID':ids['class']};selected[kind].add(identity)
        choices.append({kind+'LineMod':body})
    assert len(choices)==3 and len(selected[kind])==(1 if options.public_bulk else 3)
    requests.append(call(kind+'Mod',{'TxnID':txns[kind],'EditSequence':scalar(record,'EditSequence'),'$choices':choices},kind))
if options.public_bulk:
    selection=DEST/f'lifecycle-{STAMP}-payment-sales-selection.json'
    selection.write_text(json.dumps({'accountId':ids['bank'],'classId':ids['class'],'transactionTypes':TYPES}),encoding='utf-8')
    args=['bulk','reclassify','--input',str(selection),'--batch-size','2','--page-size','25']
    preview=run('bulk-preview',args);assert preview['execution'] is None and preview['counts']['transactionsToChange']==3 and preview['counts']['linesToChange']==3
    applied=run('bulk-apply',[*args,'--apply'])
    assert len(applied['verification'])==3 and all(v['classVerified'] and v['otherFieldsPreserved'] and v['postingAccountsAndAmountsPreserved'] for v in applied['verification'])
    (DEST/f'lifecycle-{STAMP}-payment-sales-bulk-execution-result.json').write_text(json.dumps(applied['execution'],indent=2),encoding='utf-8')
else:
    preview=batch('preview',requests);assert not preview['submitted']
    batch('apply',requests,True)
after=forms('after-forms');newposts=postings('after-postings')
def canonical(n,selected=()):return (n['name'],tuple(sorted(n['attributes'].items())),n.get('value'),tuple(canonical(c,selected) for c in n['children']
    if c['name'] not in ('TimeModified','EditSequence') and not(c['name']=='ClassRef' and scalar(n,'TxnLineID') in selected)))
assert Counter(map(canonical,oldposts))==Counter(map(canonical,newposts))
checks=[]
for kind in TYPES:
    assert canonical(before[kind],selected[kind])==canonical(after[kind],selected[kind])
    lines={scalar(n,'TxnLineID'):n for n in after[kind]['children'] if n['name']==kind+'LineRet'}
    assert all(ref(lines[x],'ClassRef')==ids['class'] for x in selected[kind])
    checks.append({'transactionType':kind,'classesChanged':len(selected[kind]),'retainedLeaves':3,'paymentClassVerified':True,
        'allOtherReturnedFormFieldsPreserved':True,'allReturnedPostingFieldsPreserved':True,
        'scope':'Explicit SDK flat service/payment recipe on Test.QBW QBD2024R21 US protocol13. No public bulk, salestax items, groups or other releases proof.'})
if options.public_bulk:
    repeat=run('bulk-repeat-preview',args);assert repeat['counts']['transactionsToChange']==repeat['counts']['linesToChange']==0
    records=[json.loads(line) for line in pathlib.Path(applied['execution']['journalFile']).read_text(encoding='utf-8').splitlines()]
    assert [len(r['requestIds']) for r in records if r['kind']=='submitted']==[2,1]
    for check in checks:check.update(publicBulkVerified=True,repeatPreviewHasNoResidualChanges=True,journalMutationBatchSizes=[2,1],
        scope='Public bulk flat service/payment recipe on Test.QBW QBD2024R21 US protocol13. No salestax items, groups or other releases proof.')
suffix='-bulk' if options.public_bulk else ''
(DEST/f'lifecycle-{STAMP}-payment-sales{suffix}-verification.json').write_text(json.dumps(checks,indent=2),encoding='utf-8')
print(json.dumps(checks),flush=True)
