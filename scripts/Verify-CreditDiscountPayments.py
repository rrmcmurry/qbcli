"""Independently verify saved credit/discount applications and normalized balances."""
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
def reference(n,k):return next((c for c in n['children'] if c['name']==k),None)
def load(label):
    v=json.loads((dest/f'lifecycle-{stamp}-credit-discounts-{label}-result.json').read_text(encoding='utf-8'),parse_float=Decimal)
    assert v['success'] and pathlib.Path(v['companyFile'])==company;return v
def plan(label):return json.loads((dest/f'lifecycle-{stamp}-credit-discounts-{label}-plan.json').read_text(encoding='utf-8'))
def mutation(label):
    value=load(label);assert value['mutatesCompany'] and all(r['outcome']=='succeeded' and r['statusCode']==0 for r in value['results'])
    journal=[json.loads(line) for line in pathlib.Path(value['journalFile']).read_text(encoding='utf-8').splitlines()]
    assert [r['requestIds'] for r in journal if r['kind']=='submitted']==[[r['requestId'] for r in plan(label)['requests']]]
    assert any(r['kind']=='finished' for r in journal);return value
base=mutation('base');class_id=next(scalar(r['data'][0],'ListID') for r in base['results'] if r['requestId']=='class')
seed=mutation('seed');ids={r['requestId']:scalar(r['data'][0],'TxnID') for r in seed['results']}
applied=mutation('apply');finished=mutation('finish');fresh={r['requestId']:r['data'][0] for r in load('payment-readback')['results']}
payments={}
for phase,result in [('apply',applied),('finish',finished)]:
    assert len(result['results'])==2
    for request,row in zip(plan(phase)['requests'],result['results']):
        kind='ReceivePayment' if row['requestId']=='receive' else 'BillPaymentCheck'
        assert row['operation']==kind+'Add';txn=scalar(row['data'][0],'TxnID');payments[(phase,row['requestId'])]=txn
        record=fresh[phase+'-'+row['requestId']];assert scalar(record,'TxnID')==txn
        body=request['fields'][kind+'Add'];applications=body['AppliedToTxnAdd'];applications=applications if isinstance(applications,list) else [applications]
        returns={scalar(c,'TxnID'):c for c in record['children'] if c['name']=='AppliedToTxnRet'}
        assert set(returns)=={a['TxnID'] for a in applications}
        for app in applications:
            ret=returns[app['TxnID']];assert Decimal(scalar(ret,'Amount'))==Decimal(app['PaymentAmount'])
            if phase=='apply':
                assert Decimal(scalar(ret,'DiscountAmount'))==Decimal(app['DiscountAmount'])==5
                assert scalar(reference(ret,'DiscountClassRef'),'ListID')==class_id
                assert scalar(reference(ret,'DiscountAccountRef'),'FullName')==app['DiscountAccountRef']['FullName']
                credit=app['SetCredit'];linked=[n for n in ret['children'] if n['name']=='LinkedTxn' and scalar(n,'TxnID')==credit['CreditTxnID']];assert len(linked)==1
                assert abs(Decimal(scalar(linked[0],'Amount')))==Decimal(credit['AppliedAmount'])
        assert Decimal(scalar(record,'TotalAmount' if kind=='ReceivePayment' else 'Amount'))==sum(Decimal(a['PaymentAmount']) for a in applications)
checks=[]
for stage in ('partial','paid'):
    rows={r['requestId']:r['data'] for r in load(stage+'-sdk')['results']}
    balances={scalar(n['children'][0],'TxnID'):Decimal(scalar(n['children'][0],'AmountDue')) for n in rows['payable'] if n['children'][0]['name']=='BillToPay'}
    assert Decimal(scalar(rows['customer-credit'][0],'CreditRemaining'))==0
    assert not any(n['children'][0]['name']=='CreditToApply' and scalar(n['children'][0],'TxnID')==ids['vendor-credit'] for n in rows['payable'])
    for key,kind,total,remaining,credit_amount in [('invoice','Invoice',100,50,20),('bill-a','Bill',100,60,10),('bill-b','Bill',80,40,10)]:
        record=rows[key][0];txn=ids[key];assert scalar(record,'TxnID')==txn and scalar(record,'IsPaid')==('true' if stage=='paid' else 'false')
        balance=Decimal(scalar(record,'BalanceRemaining')) if kind=='Invoice' else balances.get(txn,Decimal(0))
        assert balance==remaining if stage=='partial' else balance==0
        if kind=='Bill':assert Decimal(scalar(record,'AmountDue'))==total
        links={scalar(c,'TxnID'):c for c in record['children'] if c['name']=='LinkedTxn'}
        credit_id=ids['customer-credit' if kind=='Invoice' else 'vendor-credit']
        assert abs(Decimal(scalar(links[credit_id],'Amount')))==credit_amount
        for phase in (['apply'] if stage=='partial' else ['apply','finish']):assert payments[(phase,'receive' if kind=='Invoice' else 'pay-bills')] in links
        command='invoices' if kind=='Invoice' else 'bills'
        for open_only in (False,True):
            normalized=load(stage+'-'+command+('-open' if open_only else '-all'));assert normalized['complete'] and normalized['openOnly']==open_only
            found=[r for r in normalized['results'] if r['id']==txn];assert len(found)==(0 if open_only and stage=='paid' else 1)
            if found:assert found[0]==expected(record,kind,balances) and found[0]['remainingAmount']==balance
        checks.append({'documentType':kind.lower(),'stage':stage,'creditApplicationVerified':True,'discountAmountAccountAndClassFreshReadVerified':True,
            'normalizedSummaryAndOpenMembershipVerified':True,'paymentAndCreditLinksVerified':True,'creditFullyConsumedVerified':True,
            'sharedVendorCreditSplitAcrossTwoBillsVerified':kind=='Bill','independentRetainedEvidenceVerification':True,
            'scope':'Test.QBW QBD2024R21 US protocol13: invoice payment25/credit20/discount5, two bills sharing vendor credit10 each plus payment25/discount5 each. Explicit DiscountClassRef and accounts, partial balances50/60/40, remaining cash completion, fresh payment/credit/bill/invoice reads, all/open normalized fields and mutation journals. No credit-only lean returns, overpayment, applied-credit modification/unapply, multicurrency or other releases proof.'})
(dest/f'lifecycle-{stamp}-credit-discounts-verification.json').write_text(json.dumps(checks,indent=2),encoding='utf-8')
print(json.dumps({'documentStatesVerified':len(checks),'creditApplicationsVerified':3,'discountClassesVerified':3,'sharedVendorCreditSplitVerified':True}))
