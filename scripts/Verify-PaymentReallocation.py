"""Verify reallocated cash/discount and retained credits from independent reads."""
import json
import pathlib
import runpy
import sys
from decimal import Decimal
ROOT=pathlib.Path(__file__).resolve().parent.parent;DEST=ROOT/'artifacts/fixtures'
STAMP=sys.argv[1];assert len(STAMP)==6 and STAMP.isdigit();PREFIX=f'lifecycle-{STAMP}-payment-reallocation'
COMPANY=(ROOT/'QBData/Test.QBW').resolve();assert COMPANY==pathlib.Path(__import__("os").environ["QBCLI_LIVE_TEST_COMPANY"]).resolve(strict=True)
expected=runpy.run_path(str(ROOT/'scripts/Test-DocumentReads.py'))['expected']
def scalar(n,k):
    values=[c for c in n['children'] if c['name']==k];assert len(values)<=1
    return values[0].get('value') if values else None
def child(n,k):
    values=[c for c in n['children'] if c['name']==k];assert len(values)==1;return values[0]
def load(label,suffix='result'):
    return json.loads((DEST/(PREFIX+'-'+label+'-'+suffix+'.json')).read_text(encoding='utf-8'),parse_float=Decimal)
def result(label):
    v=load(label);assert v['success'] and pathlib.Path(v['companyFile'])==COMPANY;return v
def mutation(label,count):
    v=result(label);plan=load(label,'plan');assert v['mutatesCompany'] and len(plan['requests'])==len(v['results'])==count
    for request,row in zip(plan['requests'],v['results']):assert request['requestId']==row['requestId'] and request['operation']==row['operation'] and row['statusCode']==0 and row['outcome']=='succeeded'
    journal=[json.loads(line) for line in pathlib.Path(v['journalFile']).read_text(encoding='utf-8').splitlines()]
    assert [r['requestIds'] for r in journal if r['kind']=='submitted']==[[r['requestId'] for r in plan['requests']]]
    assert any(r['kind']=='finished' for r in journal);return v,plan
base,_=mutation('base',3);parties={r['requestId']:scalar(r['data'][0],'ListID') for r in base['results']}
seed,seed_plan=mutation('seed',6);ids={r['requestId']:scalar(r['data'][0],'TxnID') for r in seed['results']}
applied,apply_plan=mutation('apply',2);payments={r['requestId']:scalar(r['data'][0],'TxnID') for r in applied['results']}
modified,mod_plan=mutation('reallocate',2)
def canon(node,ignore=()):
    if isinstance(node,dict):return {k:sorted([canon(c,ignore) for c in v if c['name'] not in ignore],key=lambda c:json.dumps(c,sort_keys=True)) if k=='children' else canon(v,ignore) for k,v in node.items()}
    if isinstance(node,list):return [canon(n,ignore) for n in node]
    return node
snapshots={};balances={}
for stage in ('before','after'):
    v=result(stage+'-sdk');plan=load(stage+'-sdk','plan');assert not v['mutatesCompany'] and len(v['results'])==len(plan['requests'])==9
    rows={r['requestId']:r['data'] for r in v['results']};assert len(rows)==9
    for req,row in zip(plan['requests'],v['results']):
        assert req['requestId']==row['requestId'] and req['operation']==row['operation'] and row['statusCode']==0 and row['outcome']=='succeeded'
        key=req['requestId']
        if key=='payable':assert req['fields']=={'PayeeEntityRef':{'ListID':parties['vendor']}};continue
        assert len(row['data'])==1 and scalar(row['data'][0],'TxnID')==(payments.get(key) or ids[key])
        assert req['fields']=={'TxnID':payments.get(key) or ids[key],'IncludeLineItems':True,**({} if key in payments else {'IncludeLinkedTxns':True})}
    payable=rows.pop('payable');assert all(n['children'][0]['name']=='BillToPay' for n in payable)
    balances[stage]={scalar(n['children'][0],'TxnID'):Decimal(scalar(n['children'][0],'AmountDue')) for n in payable}
    assert set(balances[stage])=={ids['bill-old'],ids['bill-new']}
    snapshots[stage]={k:n[0] for k,n in rows.items()}
before=snapshots['before'];after=snapshots['after'];checks=[]
for key,kind,old,new,credit in [('receive','ReceivePayment','invoice-old','invoice-new','customer-credit'),('pay-bills','BillPaymentCheck','bill-old','bill-new','vendor-credit')]:
    initial=next(r for r in apply_plan['requests'] if r['requestId']==key)['fields'][kind+'Add']
    app=initial['AppliedToTxnAdd'];assert app['TxnID']==ids[old] and app['PaymentAmount']=='30.00' and app['DiscountAmount']=='5.00'
    assert app['SetCredit']=={'CreditTxnID':ids[credit],'AppliedAmount':'20.00'}
    request=next(r for r in mod_plan['requests'] if r['requestId']==key);body=request['fields'][kind+'Mod']
    assert set(body)=={'TxnID','EditSequence','AppliedToTxnMod'} and body['TxnID']==payments[key] and body['EditSequence']==scalar(before[key],'EditSequence')
    target=body['AppliedToTxnMod'];assert set(target)=={'TxnID','PaymentAmount','DiscountAmount','DiscountAccountRef','DiscountClassRef'}
    assert target['TxnID']==ids[new] and target['PaymentAmount']=='30.00' and target['DiscountAmount']=='10.00'
    assert target['DiscountClassRef']=={'ListID':parties['class']}
    assert target['DiscountAccountRef']=={'FullName':'SDK154955 '+('Expense' if key=='receive' else 'Income')}
    assert canon(before[key],('TimeModified','EditSequence','AppliedToTxnRet'))==canon(after[key],('TimeModified','EditSequence','AppliedToTxnRet'))
    for stage,document,discount in [('before',old,5),('after',new,10)]:
        payment=snapshots[stage][key];applications=[c for c in payment['children'] if c['name']=='AppliedToTxnRet'];assert len(applications)==1
        ret=applications[0];assert scalar(ret,'TxnID')==ids[document] and Decimal(scalar(ret,'Amount'))==30 and Decimal(scalar(ret,'DiscountAmount'))==discount
        assert scalar(child(ret,'DiscountClassRef'),'ListID')==parties['class'] and scalar(child(ret,'DiscountAccountRef'),'FullName')==target['DiscountAccountRef']['FullName']
        assert Decimal(scalar(payment,'TotalAmount' if key=='receive' else 'Amount'))==30
        if stage=='after':assert not any(c['name']=='LinkedTxn' and scalar(c,'TxnID')==ids[credit] for c in ret['children'])
    for document in (old,new):
        # Every original form line and unrelated header field must remain.
        assert canon(before[document],('TimeModified','EditSequence','BalanceRemaining','AppliedAmount','OpenAmount','LinkedTxn'))==canon(after[document],('TimeModified','EditSequence','BalanceRemaining','AppliedAmount','OpenAmount','LinkedTxn'))
        for stage in ('before','after'):
            record=snapshots[stage][document];amount=(45 if document==old else 100) if stage=='before' else (80 if document==old else 60)
            actual=Decimal(scalar(record,'BalanceRemaining')) if key=='receive' else balances[stage][ids[document]]
            assert actual==amount and scalar(record,'IsPaid')=='false'
            if key=='receive':assert Decimal(scalar(record,'AppliedAmount'))==-(100-amount)
            else:
                assert Decimal(scalar(record,'AmountDue'))==100
                assert Decimal(scalar(record,'OpenAmount'))==sum(balances[stage].values())
            links={scalar(c,'TxnID'):c for c in record['children'] if c['name']=='LinkedTxn'}
            expect_credit=document==old;expect_payment=(document==old if stage=='before' else document==new)
            assert set(links)==({ids[credit]} if expect_credit else set())|({payments[key]} if expect_payment else set())
            if expect_credit:assert abs(Decimal(scalar(links[ids[credit]],'Amount')))==20
            if expect_payment:assert abs(Decimal(scalar(links[payments[key]],'Amount')))==(35 if stage=='before' else 40)
            for open_only in (False,True):
                normalized=result(stage+'-'+('invoices' if key=='receive' else 'bills')+('-open' if open_only else '-all'))
                assert normalized['complete'] and normalized['openOnly']==open_only
                matches=[r for r in normalized['results'] if r['id']==ids[document]];assert len(matches)==1 and matches[0]==expected(record,'Invoice' if key=='receive' else 'Bill',balances[stage])
            checks.append({'operation':kind+'Mod','documentRole':'original' if document==old else 'replacement','stage':stage,
                'cashDiscountReallocationVerified':stage=='after','retainedOriginalCreditVerified':document==old,'normalizedSummaryAndOpenMembershipVerified':True,
                'completeUnrelatedDocumentAndPaymentFieldsPreservedVerified':True,'freshEditAndJournalVerified':True,'independentRetainedEvidenceVerification':True,
                'scope':'Test.QBW QBD2024R21 US protocol13: same-party fresh invoice/bill pairs100, original cash30/discount5/credit20 ->45 remaining. Single new-document AppliedToTxnMod without SetCredit moved cash30/discount10 to replacement ->60; original retained credit20 ->80. Old cash/discount links removed, bidirectional credits preserved. Fresh payment queries returned only replacement applications and did not expose retained original credit. Full queried line/header fields, normalized all/open lists and journals verified. No cross-party, cross-account, credit unapply, additional credit, deposited/reconciled payment, multicurrency, card modifier or other releases proof.'})
    assert canon(before[credit],('TimeModified','EditSequence','OpenAmount'))==canon(after[credit],('TimeModified','EditSequence','OpenAmount'))
    if key=='pay-bills':
        for stage in ('before','after'):assert Decimal(scalar(snapshots[stage][credit],'OpenAmount'))==sum(balances[stage].values())
    reverse=[c for c in after[credit]['children'] if c['name']=='LinkedTxn'];assert len(reverse)==1 and scalar(reverse[0],'TxnID')==ids[old] and abs(Decimal(scalar(reverse[0],'Amount')))==20
    if key=='receive':assert Decimal(scalar(after[credit],'CreditRemaining'))==0
(DEST/(PREFIX+'-verification.json')).write_text(json.dumps(checks,indent=2),encoding='utf-8')
print(json.dumps({'documentStatesVerified':len(checks),'paymentReallocationsVerified':2,'retainedCreditLinksVerified':2,'replacementOnlyPaymentReturnsObserved':True}))
