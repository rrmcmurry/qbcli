"""Independently verify additional modifier credits and retained distributions."""
import json
import pathlib
import runpy
import sys
from decimal import Decimal
ROOT=pathlib.Path(__file__).resolve().parent.parent;DEST=ROOT/'artifacts/fixtures'
STAMP=sys.argv[1];assert len(STAMP)==6 and STAMP.isdigit();PREFIX=f'lifecycle-{STAMP}-payment-reallocation'
# Verify the initial controls from retained plans/independent SDK reads first.
initial=runpy.run_path(str(ROOT/'scripts/Verify-PaymentReallocation.py'))
result=initial['result'];load=initial['load'];mutation=initial['mutation'];scalar=initial['scalar'];child=initial['child'];canon=initial['canon'];expected=initial['expected']
ids=initial['ids'];payments=initial['payments'];parties=initial['parties'];company=initial['COMPANY']
seed,seed_plan=mutation('extra-seed',2);extra_ids={r['requestId']:scalar(r['data'][0],'TxnID') for r in seed['results']}
assert set(extra_ids)=={'extra-customer-credit','extra-vendor-credit'}
for r in seed_plan['requests']:
    body=r['fields'][r['operation']]
    if r['requestId']=='extra-customer-credit':assert body['CustomerRef']=={'ListID':parties['customer']} and Decimal(body['CreditMemoLineAdd']['Rate'])==10 and Decimal(body['CreditMemoLineAdd']['Quantity'])==1
    else:assert body['VendorRef']=={'ListID':parties['vendor']} and Decimal(body['ExpenseLineAdd']['Amount'])==10
snapshots={};balances={};payable={}
for stage in ('extra-before','extra-after'):
    v=result(stage+'-sdk');plan=load(stage+'-sdk','plan');assert not v['mutatesCompany'] and len(v['results'])==len(plan['requests'])==11
    rows={r['requestId']:r['data'] for r in v['results']};assert len(rows)==11
    for request,row in zip(plan['requests'],v['results']):
        key=request['requestId'];assert key==row['requestId'] and request['operation']==row['operation'] and row['statusCode']==0 and row['outcome']=='succeeded'
        if key=='payable':assert request['fields']=={'PayeeEntityRef':{'ListID':parties['vendor']}};continue
        txn=payments.get(key) or ids.get(key) or extra_ids[key]
        assert len(row['data'])==1 and scalar(row['data'][0],'TxnID')==txn
        assert request['fields']=={'TxnID':txn,'IncludeLineItems':True,**({} if key in payments else {'IncludeLinkedTxns':True})}
    payable[stage]=rows.pop('payable');balances[stage]={scalar(n['children'][0],'TxnID'):Decimal(scalar(n['children'][0],'AmountDue')) for n in payable[stage] if n['children'][0]['name']=='BillToPay'}
    assert set(balances[stage])=={ids['bill-old'],ids['bill-new']}
    snapshots[stage]={k:n[0] for k,n in rows.items()}
before=snapshots['extra-before'];after=snapshots['extra-after'];applied,plan=mutation('extra-apply',2);checks=[]
def vendor_net(stage):
    available=sum(Decimal(scalar(n['children'][0],'CreditRemaining')) for n in payable[stage] if n['children'][0]['name']=='CreditToApply')
    assert available==(10 if stage=='extra-before' else 0)
    return sum(balances[stage].values())-available
for key,kind,old,new,credit,extra in [('receive','ReceivePayment','invoice-old','invoice-new','customer-credit','extra-customer-credit'),('pay-bills','BillPaymentCheck','bill-old','bill-new','vendor-credit','extra-vendor-credit')]:
    request=next(r for r in plan['requests'] if r['requestId']==key);body=request['fields'][kind+'Mod']
    assert set(body)=={'TxnID','EditSequence','AppliedToTxnMod'} and body['TxnID']==payments[key] and body['EditSequence']==scalar(before[key],'EditSequence')
    applications=body['AppliedToTxnMod'];assert len(applications)==2
    retained,additional=applications
    assert retained=={'TxnID':ids[new],'PaymentAmount':'30.00','DiscountAmount':'10.00','DiscountAccountRef':{'FullName':'SDK154955 '+('Expense' if key=='receive' else 'Income')},'DiscountClassRef':{'ListID':parties['class']}}
    assert additional=={'TxnID':ids[old],'SetCredit':{'CreditTxnID':extra_ids[extra],'AppliedAmount':'10.00'}}
    assert canon(before[key],('TimeModified','EditSequence','AppliedToTxnRet','UnusedCredits'))==canon(after[key],('TimeModified','EditSequence','AppliedToTxnRet','UnusedCredits'))
    if key=='receive':assert Decimal(scalar(before[key],'UnusedCredits'))==10 and Decimal(scalar(after[key],'UnusedCredits'))==0
    original_app=child(before[key],'AppliedToTxnRet')
    returned=[c for c in after[key]['children'] if c['name']=='AppliedToTxnRet' and scalar(c,'TxnID')==ids[new]];assert len(returned)==1 and canon(original_app)==canon(returned[0])
    assert len([c for c in after[key]['children'] if c['name']=='AppliedToTxnRet'])==1
    for document in (old,new):
        assert canon(before[document],('TimeModified','EditSequence','BalanceRemaining','AppliedAmount','OpenAmount','LinkedTxn'))==canon(after[document],('TimeModified','EditSequence','BalanceRemaining','AppliedAmount','OpenAmount','LinkedTxn'))
        for stage in ('extra-before','extra-after'):
            record=snapshots[stage][document];remaining=(80 if stage=='extra-before' else 70) if document==old else 60
            amount=Decimal(scalar(record,'BalanceRemaining')) if key=='receive' else balances[stage][ids[document]]
            assert amount==remaining and scalar(record,'IsPaid')=='false'
            if key=='receive':assert Decimal(scalar(record,'AppliedAmount'))==-(100-remaining)
            else:assert Decimal(scalar(record,'AmountDue'))==100 and Decimal(scalar(record,'OpenAmount'))==vendor_net(stage)
            links={scalar(c,'TxnID'):c for c in record['children'] if c['name']=='LinkedTxn'}
            wanted=({ids[credit]}|({extra_ids[extra]} if stage=='extra-after' else set())) if document==old else {payments[key]}
            assert set(links)==wanted
            for txn,amount in [(ids[credit],20),(extra_ids[extra],10),(payments[key],40)]:
                if txn in links:assert abs(Decimal(scalar(links[txn],'Amount')))==amount
            for open_only in (False,True):
                normalized=result(stage+'-'+('invoices' if key=='receive' else 'bills')+('-open' if open_only else '-all'));assert normalized['complete'] and normalized['openOnly']==open_only
                found=[r for r in normalized['results'] if r['id']==ids[document]];assert len(found)==1 and found[0]==expected(record,'Invoice' if key=='receive' else 'Bill',balances[stage])
            checks.append({'operation':kind+'Mod','stage':stage,'documentRole':'original-credit-target' if document==old else 'retained-cash-target',
                'additionalCreditAppliedVerified':stage=='extra-after','originalCreditAndCashDiscountPreservedVerified':True,
                'completeUnrelatedQueriedFieldsPreservedVerified':True,'normalizedSummaryAndOpenMembershipVerified':True,'freshEditAndJournalVerified':True,
                'creditOnlyApplicationAbsentFromFreshPaymentReturnObserved':True,'receiveUnusedCreditsTracksNewCreditConsumptionVerified':key=='receive',
                'vendorOpenAmountNetOfAvailableCreditObserved':key=='pay-bills',
                'independentRetainedEvidenceVerification':True,'scope':'Test.QBW QBD2024R21 US protocol13: same-party synthetic pairs; payment modifier retained replacement cash30/discount10 and added new credit10 to original document using a second SetCredit-only application. Original credit20 not resubmitted and remained consumed/linked; original remaining80->70, replacement60 unchanged. Full fresh queried fields, bidirectional credit links, consumed extra credits, normalized all/open states and journals independently verified. No standalone SetCredit-only modifier with omitted existing cash applications, credit amount modification/unapply, cross-party/account, card modifier, reconciled/deposited payments, multicurrency or other releases proof.'})
    assert canon(before[credit],('TimeModified','EditSequence','OpenAmount'))==canon(after[credit],('TimeModified','EditSequence','OpenAmount'))
    for stage in ('extra-before','extra-after'):
        credit_record=snapshots[stage][credit];reverse=[c for c in credit_record['children'] if c['name']=='LinkedTxn'];assert len(reverse)==1 and scalar(reverse[0],'TxnID')==ids[old] and abs(Decimal(scalar(reverse[0],'Amount')))==20
    assert canon(before[extra],('TimeModified','EditSequence','CreditRemaining','OpenAmount','LinkedTxn'))==canon(after[extra],('TimeModified','EditSequence','CreditRemaining','OpenAmount','LinkedTxn'))
    assert not any(c['name']=='LinkedTxn' for c in before[extra]['children'])
    reverse=[c for c in after[extra]['children'] if c['name']=='LinkedTxn'];assert len(reverse)==1 and scalar(reverse[0],'TxnID')==ids[old] and abs(Decimal(scalar(reverse[0],'Amount')))==10
    if key=='receive':assert Decimal(scalar(before[extra],'CreditRemaining'))==10 and Decimal(scalar(after[extra],'CreditRemaining'))==0
    else:
        assert any(n['children'][0]['name']=='CreditToApply' and scalar(n['children'][0],'TxnID')==extra_ids[extra] for n in payable['extra-before'])
        assert not any(n['children'][0]['name']=='CreditToApply' for n in payable['extra-after'])
        for stage in ('extra-before','extra-after'):
            for name in (credit,extra):assert Decimal(scalar(snapshots[stage][name],'OpenAmount'))==vendor_net(stage)
(DEST/f'lifecycle-{STAMP}-additional-credit-mods-verification.json').write_text(json.dumps(checks,indent=2),encoding='utf-8')
print(json.dumps({'additionalCreditModifierOperations':2,'documentStatesVerified':len(checks),'originalCreditsPreserved':True,'replacementCashDiscountPreserved':True,'newCreditsConsumed':True}))
