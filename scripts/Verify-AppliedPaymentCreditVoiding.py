"""Independently verify voided identities, amounts, surviving bills and links."""
import json
import pathlib
import runpy
import sys
from decimal import Decimal
ROOT=pathlib.Path(__file__).resolve().parent.parent;DEST=ROOT/'artifacts/fixtures';STAMP=sys.argv[1]
assert len(STAMP)==6 and STAMP.isdigit();PREFIX=f'lifecycle-{STAMP}-applied-void'
COMPANY=(ROOT/'QBData/Test.QBW').resolve();assert COMPANY==pathlib.Path(__import__("os").environ["QBCLI_LIVE_TEST_COMPANY"]).resolve(strict=True)
expected=runpy.run_path(str(ROOT/'scripts/Test-DocumentReads.py'))['expected']
def scalar(n,k):
    matches=[c for c in n['children'] if c['name']==k];assert len(matches)<=1
    return matches[0].get('value') if matches else None
def canonical(n,ignore=()):
    if isinstance(n,dict):return {k:sorted([canonical(c,ignore) for c in v if c['name'] not in ignore],key=lambda c:json.dumps(c,sort_keys=True)) if k=='children' else canonical(v,ignore) for k,v in n.items()}
    if isinstance(n,list):return [canonical(c,ignore) for c in n]
    return n
def load(label,suffix='result'):return json.loads((DEST/(PREFIX+'-'+label+'-'+suffix+'.json')).read_text(encoding='utf-8'),parse_float=Decimal)
def result(label):
    v=load(label);assert v['success'] and pathlib.Path(v['companyFile'])==COMPANY;return v
def mutation(label,count):
    v=result(label);plan=load(label,'plan');assert v['mutatesCompany'] and len(v['results'])==len(plan['requests'])==count
    for request,row in zip(plan['requests'],v['results']):assert request['requestId']==row['requestId'] and request['operation']==row['operation'] and row['outcome']=='succeeded' and row['statusCode']==0
    journal=[json.loads(line) for line in pathlib.Path(v['journalFile']).read_text(encoding='utf-8').splitlines()]
    assert [r['requestIds'] for r in journal if r['kind']=='submitted']==[[r['requestId'] for r in plan['requests']]] and any(r['kind']=='finished' for r in journal)
    return v,plan
base,_=mutation('base',2);parties={r['requestId']:scalar(r['data'][0],'ListID') for r in base['results']}
seed,_=mutation('seed',4);ids={r['requestId']:scalar(r['data'][0],'TxnID') for r in seed['results']}
applied,apply_plan=mutation('apply',2);payments={r['requestId']:scalar(r['data'][0],'TxnID') for r in applied['results']}
for label,targets in [('void-payments',{'check':'BillPaymentCheck','card':'BillPaymentCreditCard'}),('void-credits',{'check-credit':'VendorCredit','card-credit':'VendorCredit'})]:
    v,plan=mutation(label,2)
    for req,row in zip(plan['requests'],v['results']):
        key=req['requestId'];txn=payments.get(key) or ids[key]
        assert req['fields']=={'TxnVoidType':targets[key],'TxnID':txn}
        ack={'children':row['data']};names={c['name'] for c in row['data']}
        assert {'TxnVoidType','TxnID','TimeCreated','TimeModified'}<=names<={'TxnVoidType','TxnID','TimeCreated','TimeModified','RefNumber'}
        assert scalar(ack,'TxnID')==txn and scalar(ack,'TxnVoidType')==targets[key]
snapshots={};balances={}
for stage in ('before','after-payments','after-credits'):
    v=result(stage+'-sdk');plan=load(stage+'-sdk','plan');assert not v['mutatesCompany'] and len(v['results'])==len(plan['requests'])==7
    rows={}
    for request,row in zip(plan['requests'],v['results']):
        key=request['requestId'];assert key==row['requestId'] and request['operation']==row['operation'] and row['outcome']=='succeeded' and row['statusCode']==0
        if key=='payable':assert request['fields']=={'PayeeEntityRef':{'ListID':parties['vendor']}};continue
        txn=payments.get(key) or ids[key];assert len(row['data'])==1 and scalar(row['data'][0],'TxnID')==txn
        assert request['fields']=={'TxnID':txn,'IncludeLineItems':True,**({} if key in payments else {'IncludeLinkedTxns':True})};rows[key]=row['data'][0]
    payable=v['results'][-1]['data'];assert len(payable)==2 and all(n['children'][0]['name']=='BillToPay' for n in payable)
    balances[stage]={scalar(n['children'][0],'TxnID'):Decimal(scalar(n['children'][0],'AmountDue')) for n in payable};assert set(balances[stage])=={ids['check-bill'],ids['card-bill']}
    snapshots[stage]=rows
before=snapshots['before'];checks=[]
for key,kind in [('check','BillPaymentCheck'),('card','BillPaymentCreditCard')]:
    requested=next(r for r in apply_plan['requests'] if r['requestId']==key)['fields'][kind+'Add']['AppliedToTxnAdd']
    assert requested['TxnID']==ids[key+'-bill'] and requested['PaymentAmount']=='30.00' and requested['DiscountAmount']=='5.00' and requested['SetCredit']=={'CreditTxnID':ids[key+'-credit'],'AppliedAmount':'20.00'}
    for stage in ('before','after-payments','after-credits'):
        rows=snapshots[stage];payment=rows[key];bill=rows[key+'-bill'];credit=rows[key+'-credit'];remaining={'before':45,'after-payments':80,'after-credits':100}[stage]
        assert Decimal(scalar(payment,'Amount'))==(30 if stage=='before' else 0)
        assert Decimal(scalar(credit,'CreditAmount'))==(0 if stage=='after-credits' else 20)
        assert Decimal(scalar(bill,'AmountDue'))==100 and scalar(bill,'IsPaid')=='false' and balances[stage][ids[key+'-bill']]==remaining
        assert Decimal(scalar(bill,'OpenAmount'))==Decimal(scalar(credit,'OpenAmount'))==sum(balances[stage].values())
        assert canonical(before[key+'-bill'],('TimeModified','EditSequence','OpenAmount','LinkedTxn'))==canonical(bill,('TimeModified','EditSequence','OpenAmount','LinkedTxn'))
        apps=[c for c in payment['children'] if c['name']=='AppliedToTxnRet']
        if stage=='before':
            assert len(apps)==1 and scalar(apps[0],'TxnID')==ids[key+'-bill'] and Decimal(scalar(apps[0],'Amount'))==30 and Decimal(scalar(apps[0],'DiscountAmount'))==5
        else:
            assert not apps and scalar(payment,'Memo')=='VOID:'
            assert canonical(before[key],('TimeModified','EditSequence','Amount','Memo','AppliedToTxnRet'))==canonical(payment,('TimeModified','EditSequence','Amount','Memo','AppliedToTxnRet'))
        links={scalar(c,'TxnID'):c for c in bill['children'] if c['name']=='LinkedTxn'}
        wanted={payments[key],ids[key+'-credit']} if stage=='before' else ({ids[key+'-credit']} if stage=='after-payments' else set());assert set(links)==wanted
        if payments[key] in links:assert abs(Decimal(scalar(links[payments[key]],'Amount')))==35
        reverse=[c for c in credit['children'] if c['name']=='LinkedTxn']
        if stage!='after-credits':
            assert abs(Decimal(scalar(links[ids[key+'-credit']],'Amount')))==20
            assert len(reverse)==1 and scalar(reverse[0],'TxnID')==ids[key+'-bill'] and abs(Decimal(scalar(reverse[0],'Amount')))==20
            assert canonical(before[key+'-credit'],('TimeModified','EditSequence','OpenAmount'))==canonical(credit,('TimeModified','EditSequence','OpenAmount'))
        else:
            assert not reverse and scalar(credit,'Memo')=='VOID: '+scalar(before[key+'-credit'],'Memo')
            assert canonical(before[key+'-credit'],('TimeModified','EditSequence','OpenAmount','Memo','CreditAmount','LinkedTxn','ExpenseLineRet'))==canonical(credit,('TimeModified','EditSequence','OpenAmount','Memo','CreditAmount','LinkedTxn','ExpenseLineRet'))
            old_lines=[c for c in before[key+'-credit']['children'] if c['name']=='ExpenseLineRet'];new_lines=[c for c in credit['children'] if c['name']=='ExpenseLineRet'];assert len(old_lines)==len(new_lines)==1
            assert Decimal(scalar(new_lines[0],'Amount'))==0 and canonical(old_lines[0],('Amount',))==canonical(new_lines[0],('Amount',))
        for open_only in (False,True):
            normalized=result(stage+'-bills'+('-open' if open_only else '-all'));assert normalized['complete'] and normalized['openOnly']==open_only
            found=[r for r in normalized['results'] if r['id']==ids[key+'-bill']];assert len(found)==1 and found[0]==expected(bill,'Bill',balances[stage])
        checks.append({'operation':'TxnVoid','paymentType':kind,'stage':stage,'voidedTransactionIdentitiesRetainedVerified':stage!='before',
            'paymentAmountZeroAndCashDiscountLinksRemovedVerified':stage!='before','paymentVoidPreservesIndependentCreditVerified':stage=='after-payments',
            'creditVoidZerosRecordAndReleasesLinkVerified':stage=='after-credits','normalizedBillBalanceAndOpenMembershipVerified':True,
            'completeUnrelatedQueriedFieldsPreservedVerified':True,'voidAcknowledgementsAndJournalsVerified':True,'independentRetainedEvidenceVerification':True,
            'scope':'Test.QBW QBD2024R21 US protocol13: fresh check/card payments cash30/discount5/credit20 on two bills100. Payment void retained same IDs with Amount0, VOID memo and no applications, leaving independent credit20 and bill80. Vendor-credit void retained same IDs with CreditAmount/expense line Amount0 and VOID memo, removed credit links and restored bills100. Six normalized states, complete unrelated queried fields, link amounts/reverse links, void IDs/types/timestamps and journals independently verified. ReceivePayment is absent from documented TxnVoidType; no receive-payment void is implemented or inferred. No other void types, deposited/reconciled payments, multicurrency or other releases proof.'})
(DEST/(PREFIX+'-verification.json')).write_text(json.dumps(checks,indent=2),encoding='utf-8')
print(json.dumps({'billStatesVerified':len(checks),'paymentsVoided':2,'creditsVoided':2,'voidedIdentitiesRetained':True,'independentCreditLinksVerified':True}))
