"""Independently verify retained payment edits and complete queried field preservation."""
import json
import pathlib
import sys
from decimal import Decimal
ROOT=pathlib.Path(__file__).resolve().parent.parent;DEST=ROOT/'artifacts/fixtures'
STAMP=sys.argv[1];assert len(STAMP)==6 and STAMP.isdigit()
PREFIX=f'lifecycle-{STAMP}-payment-distribution';COMPANY=(ROOT/'QBData/Test.QBW').resolve()
assert COMPANY==pathlib.Path(__import__("os").environ["QBCLI_LIVE_TEST_COMPANY"]).resolve(strict=True)
manifest=json.loads((DEST/(PREFIX+'-manifest.json')).read_text(encoding='utf-8'));assert pathlib.Path(manifest['companyFile'])==COMPANY
ids=manifest['documentIds'];payments=manifest['paymentIds'];parties=manifest['partyIds']
def load(label,suffix='result'):
    return json.loads((DEST/(PREFIX+'-'+label+'-'+suffix+'.json')).read_text(encoding='utf-8'))
def scalar(node,name):
    found=[c for c in node['children'] if c['name']==name];assert len(found)<=1
    return found[0].get('value') if found else None
def child(node,name):
    found=[c for c in node['children'] if c['name']==name];assert len(found)==1;return found[0]
source=manifest['sourceFixture'];assert len(source)==6 and source.isdigit()
for label,target,field in [('base',parties,'ListID'),('seed',ids,'TxnID'),('apply',payments,'TxnID')]:
    original=json.loads((DEST/f'lifecycle-{source}-credit-discounts-{label}-result.json').read_text(encoding='utf-8'))
    assert original['success'] and pathlib.Path(original['companyFile'])==COMPANY and original['mutatesCompany']
    assert all(r['statusCode']==0 and r['outcome']=='succeeded' for r in original['results'])
    assert target=={r['requestId']:scalar(r['data'][0],field) for r in original['results']}
def canonical(node,ignore=()):
    # Queries here have no ordered form lines. Return field, application and link
    # order may change after a memo edit; compare the complete multiset instead.
    if isinstance(node,dict):
        return {k:sorted([canonical(c,ignore) for c in v if c['name'] not in ignore],key=lambda c:json.dumps(c,sort_keys=True)) if k=='children' else canonical(v,ignore) for k,v in node.items()}
    if isinstance(node,list):return [canonical(n,ignore) for n in node]
    return node
def reads(label):
    value=load(label);plan=load(label,'plan');assert value['success'] and pathlib.Path(value['companyFile'])==COMPANY
    assert not value['mutatesCompany'] and len(value['results'])==8
    result={r['requestId']:r for r in value['results']};assert len(result)==8
    for request in plan['requests']:
        key=request['requestId'];row=result[key];assert row['operation']==request['operation']
        assert row['outcome'] in ('succeeded','no_match') and row['statusCode'] in (0,1)
        if key=='payable':
            assert request['fields']=={'PayeeEntityRef':{'ListID':parties['vendor']}} and not row['data'];continue
        assert len(row['data'])==1 and scalar(row['data'][0],'TxnID')==(payments.get(key) or ids[key])
        assert request['fields']=={'TxnID':payments.get(key) or ids[key],**({'IncludeLineItems':True} if key in payments else {'IncludeLinkedTxns':True})}
        assert not any(c['name'].endswith('LineRet') for c in row['data'][0]['children'])
    return {k:r['data'][0] for k,r in result.items() if k!='payable'}
def mutation(label,before):
    value=load(label);plan=load(label,'plan');assert value['success'] and value['mutatesCompany'] and pathlib.Path(value['companyFile'])==COMPANY
    assert len(value['results'])==len(plan['requests'])==2
    journal=[json.loads(line) for line in pathlib.Path(value['journalFile']).read_text(encoding='utf-8').splitlines()]
    assert [r['requestIds'] for r in journal if r['kind']=='submitted']==[[r['requestId'] for r in plan['requests']]]
    assert any(r['kind']=='finished' for r in journal)
    for request,row in zip(plan['requests'],value['results']):
        key=request['requestId'];body=request['fields'][request['operation']]
        assert row['outcome']=='succeeded' and row['statusCode']==0 and row['operation']==request['operation'] and row['requestId']==key
        assert body['TxnID']==payments[key] and body['EditSequence']==scalar(before[key],'EditSequence')
        assert 'SetCredit' not in json.dumps(body)
    return plan
before=reads('before');memo=reads('memo-readback');after=reads('redistribute-readback')
memo_plan=mutation('memo',before);distribution_plan=mutation('redistribute',memo)
for request in memo_plan['requests']:
    key=request['requestId'];body=request['fields'][request['operation']]
    assert set(body)=={'TxnID','EditSequence','Memo'} and scalar(memo[key],'Memo')==body['Memo']=='Payment distribution control '+STAMP
for key in before:
    ignore=('TimeModified','EditSequence','Memo') if key in payments else ('TimeModified','EditSequence')
    assert canonical(before[key],ignore)==canonical(memo[key],ignore),('memo changed another field',key)
    if key not in payments:assert canonical(memo[key],ignore)==canonical(after[key],ignore),('document/credit changed',key)
for stage in (before,memo,after):
    assert scalar(stage['invoice'],'IsPaid')=='true' and Decimal(scalar(stage['invoice'],'BalanceRemaining'))==0
    assert all(scalar(stage[k],'IsPaid')=='true' for k in ('bill-a','bill-b'))
    assert Decimal(scalar(stage['customer-credit'],'CreditRemaining'))==0
    for key,credit,amount in [('invoice','customer-credit',20),('bill-a','vendor-credit',10),('bill-b','vendor-credit',10)]:
        links=[c for c in stage[key]['children'] if c['name']=='LinkedTxn' and scalar(c,'TxnID')==ids[credit]]
        reverse=[c for c in stage[credit]['children'] if c['name']=='LinkedTxn' and scalar(c,'TxnID')==ids[key]]
        assert len(links)==len(reverse)==1 and abs(Decimal(scalar(links[0],'Amount')))==abs(Decimal(scalar(reverse[0],'Amount')))==amount
checks=[]
for key,operation,expected in [('receive','ReceivePaymentMod',{'invoice':(20,10)}),('pay-bills','BillPaymentCheckMod',{'bill-a':(20,10),'bill-b':(30,0)})]:
    request=next(r for r in distribution_plan['requests'] if r['requestId']==key);body=request['fields'][operation]
    requested=body['AppliedToTxnMod'];requested=requested if isinstance(requested,list) else [requested]
    assert set(body)==({'TxnID','EditSequence','TotalAmount','AppliedToTxnMod'} if key=='receive' else {'TxnID','EditSequence','AppliedToTxnMod'})
    if key=='receive':assert Decimal(body['TotalAmount'])==20
    old={scalar(c,'TxnID'):c for c in memo[key]['children'] if c['name']=='AppliedToTxnRet'}
    new={scalar(c,'TxnID'):c for c in after[key]['children'] if c['name']=='AppliedToTxnRet'}
    assert set(old)==set(new)=={ids[k] for k in expected}=={a['TxnID'] for a in requested}
    for document,(cash,discount) in expected.items():
        old_app=old[ids[document]];new_app=new[ids[document]];app=next(a for a in requested if a['TxnID']==ids[document])
        assert Decimal(scalar(old_app,'Amount'))==25 and Decimal(scalar(old_app,'DiscountAmount'))==5
        assert Decimal(app['PaymentAmount'])==Decimal(scalar(new_app,'Amount'))==cash and Decimal(app['DiscountAmount'])==discount
        assert app['DiscountClassRef']=={'ListID':parties['class']}
        assert app['DiscountAccountRef']=={'FullName':'SDK154955 '+('Expense' if document=='invoice' else 'Income')}
        if discount:
            assert Decimal(scalar(new_app,'DiscountAmount'))==discount
            assert scalar(child(new_app,'DiscountAccountRef'),'FullName')==app['DiscountAccountRef']['FullName']
            assert scalar(child(new_app,'DiscountClassRef'),'ListID')==parties['class']
        else:
            assert not any(c['name'] in ('DiscountAmount','DiscountAccountRef','DiscountClassRef') for c in new_app['children'])
        assert canonical(old_app,('Amount','DiscountAmount','DiscountAccountRef','DiscountClassRef'))==canonical(new_app,('Amount','DiscountAmount','DiscountAccountRef','DiscountClassRef'))
    ignore=('TimeModified','EditSequence','AppliedToTxnRet','TotalAmount') if key=='receive' else ('TimeModified','EditSequence','AppliedToTxnRet')
    assert canonical(memo[key],ignore)==canonical(after[key],ignore)
    assert Decimal(scalar(after[key],'TotalAmount' if key=='receive' else 'Amount'))==sum(x[0] for x in expected.values())
    for stage in ('memo','redistribute'):
        checks.append({'operation':operation,'stage':stage,'memoOnlyPreservesApplicationsVerified':stage=='memo','cashDiscountRedistributionVerified':stage=='redistribute',
            'existingCreditLinksPreservedVerified':True,'settledDocumentsRemainPaidVerified':True,'completeQueriedFieldComparisonVerified':True,'freshEditAndJournalVerified':True,
            'zeroDiscountFieldsOmittedObserved':key=='pay-bills' and stage=='redistribute','independentRetainedEvidenceVerification':True,
            'scope':'Test.QBW QBD2024R21 US protocol13: existing synthetic paid invoice and two bills with fully consumed credits; memo-only edits retain applications. Receive cash25/discount5 ->20/10; two bill applications25/25 cash and5/5 discounts ->20/30 cash and10/0 discounts. Complete queried payment/document/credit fields, bidirectional credit links and empty unbounded BillToPay query preserved, ignoring timestamps/edit sequences and unordered links. AppliedToTxnMod supplied every original application without SetCredit. No applied-credit modification/unapply, partial redistribution, different-document reallocation, multicurrency, card modifier or other releases proof.'})
(DEST/(PREFIX+'-verification.json')).write_text(json.dumps(checks,indent=2),encoding='utf-8')
print(json.dumps({'paymentModifierChecks':len(checks),'paymentOperations':2,'cashDiscountApplications':3,'existingCreditsPreserved':True,'settledDocumentBalancesPreserved':True}))
