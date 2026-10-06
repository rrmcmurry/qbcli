"""Saved-evidence verification for credit-only lean results and bill stubs; no replay."""
import json
import pathlib
import runpy
import sys
from decimal import Decimal
root=pathlib.Path(__file__).resolve().parent.parent;dest=root/'artifacts/fixtures'
stamp=sys.argv[1];assert len(stamp)==6 and stamp.isdigit()
company=(root/'QBData/Test.QBW').resolve();assert company==pathlib.Path(__import__("os").environ["QBCLI_LIVE_TEST_COMPANY"]).resolve(strict=True)
expected=runpy.run_path(str(root/'scripts/Test-DocumentReads.py'))['expected']
def scalar(n,k):
    values=[c for c in n['children'] if c['name']==k];assert len(values)<=1
    return values[0].get('value') if values else None
def load(label):
    value=json.loads((dest/f'lifecycle-{stamp}-credit-only-{label}-result.json').read_text(encoding='utf-8'),parse_float=Decimal)
    assert value['success'] and pathlib.Path(value['companyFile'])==company;return value
def plan(label):return json.loads((dest/f'lifecycle-{stamp}-credit-only-{label}-plan.json').read_text(encoding='utf-8'))
seed=load('seed');ids={r['requestId']:scalar(r['data'][0],'TxnID') for r in seed['results']}
before=load('payments-before-confirmed');after=load('payments-after');applied=load('apply')
assert len(before['results'])==len(after['results'])==len(applied['results'])==3
assert all(r['outcome']=='no_match' and r['statusCode']==1 and not r['data'] for r in before['results'])
assert after['results'][0]['outcome']=='no_match' and not after['results'][0]['data']
journal=[json.loads(line) for line in pathlib.Path(applied['journalFile']).read_text(encoding='utf-8').splitlines()]
assert applied['mutatesCompany'] and [r['requestIds'] for r in journal if r['kind']=='submitted']==[['receive','check','card']]
assert any(r['kind']=='finished' for r in journal)
fresh={r['requestId']:r['data'] for r in load('verify')['results']}
balances={scalar(n['children'][0],'TxnID'):Decimal(scalar(n['children'][0],'AmountDue')) for n in fresh['payable'] if n['children'][0]['name']=='BillToPay'}
assert Decimal(scalar(fresh['customer-credit'][0],'CreditRemaining'))==0
assert not any(n['children'][0]['name']=='CreditToApply' and scalar(n['children'][0],'TxnID') in (ids['check-credit'],ids['card-credit']) for n in fresh['payable'])
stubs=load('stub-transactions')['results'][0]['data'];assert len(stubs)==2
checks=[]
for i,(key,kind,credit,operation) in enumerate([('invoice','Invoice','customer-credit','ReceivePaymentAdd'),('bill-check','Bill','check-credit','BillPaymentCheckAdd'),('bill-card','Bill','card-credit','BillPaymentCreditCardAdd')]):
    request=plan('apply')['requests'][i];assert request['operation']==operation
    body=request['fields'][operation];app=body['AppliedToTxnAdd']
    assert app=={'TxnID':ids[key],'SetCredit':{'CreditTxnID':ids[credit],'AppliedAmount':'40.00'}}
    assert 'PaymentAmount' not in app and 'DiscountAmount' not in app
    if i==0:assert Decimal(body['TotalAmount'])==0
    response=applied['results'][i];assert response['outcome']=='succeeded' and response['statusCode']==0 and len(response['data'])==1
    ret=response['data'][0];lean=[c for c in ret['children'] if c['name']=='AppliedToTxnRet'];assert len(lean)==1
    assert {c['name'] for c in lean[0]['children']}=={'TxnID','TxnType'} and scalar(lean[0],'TxnID')==ids[key]
    generated=scalar(ret,'TxnID')
    if i==0:assert generated is None
    else:
        assert generated and len(after['results'][i]['data'])==1
        queried=after['results'][i]['data'][0];assert scalar(queried,'TxnID')==generated
        assert scalar(queried,'Memo')=='QuickBooks generated zero amount transaction for bill payment stub'
        transaction=[n for n in stubs if scalar(n,'TxnID')==generated];assert len(transaction)==1
        assert scalar(transaction[0],'TxnType')==operation.removesuffix('Add') and scalar(transaction[0],'Amount') is None
    record=fresh[key][0];assert scalar(record,'IsPaid')=='false'
    balance=Decimal(scalar(record,'BalanceRemaining')) if kind=='Invoice' else balances[ids[key]];assert balance==60
    linked=[n for n in record['children'] if n['name']=='LinkedTxn' and scalar(n,'TxnID')==ids[credit]];assert len(linked)==1 and abs(Decimal(scalar(linked[0],'Amount')))==40
    reverse=[n for n in fresh[credit][0]['children'] if n['name']=='LinkedTxn' and scalar(n,'TxnID')==ids[key]];assert len(reverse)==1 and abs(Decimal(scalar(reverse[0],'Amount')))==40
    for open_only in (False,True):
        normalized=load(('invoices' if kind=='Invoice' else 'bills')+('-open' if open_only else '-all'));assert normalized['complete'] and normalized['openOnly']==open_only
        summaries=[r for r in normalized['results'] if r['id']==ids[key]];assert len(summaries)==1 and summaries[0]==expected(record,kind,balances)
    checks.append({'operation':operation,'sdkAcceptedCreditOnly':True,'leanAppliedReturnWithoutAmountVerified':True,
        'generatedPaymentTxnIdReturned':generated is not None,'generatedZeroAmountStubMemoObserved':i>0,'numericStubAmountReturned':False if i>0 else None,
        'freshPaymentQueryRecordCount':len(after['results'][i]['data']),'creditConsumedAndDocumentBalanceVerified':True,
        'bidirectionalCreditLinksVerified':True,'normalizedSummaryAndOpenMembershipVerified':True,'independentRetainedEvidenceVerification':True,
        'scope':'Test.QBW QBD2024R21 US protocol13: three credit-only40 applications to invoice/two bills100. ReceivePayment returned no payment ID/record; check/card routes generated stubs labelled zero amount by QuickBooks, with numeric Amount omitted. Fresh entity-filtered unbounded payment queries, stub TransactionQuery, credit links/consumption, remaining60, all/open normalized fields and journal verified. Initial read connection0x80040408 recovered via company query; successful seed reused and applications never replayed. No universal absence-of-payment-transaction, numeric cash-impact, full-credit settlement, modify/unapply or other releases proof.'})
(dest/f'lifecycle-{stamp}-credit-only-verification.json').write_text(json.dumps(checks,indent=2),encoding='utf-8')
print(json.dumps({'creditOnlyApplicationsVerified':3,'leanReceivePaymentWithoutCreatedId':True,'generatedBillPaymentStubs':2,'documentBalancesAndLinksVerified':True}))
