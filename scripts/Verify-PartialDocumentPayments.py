"""Verify saved document payment lifecycle, summaries and journals without replay."""
import json
import pathlib
import runpy
import sys
from decimal import Decimal
root=pathlib.Path(__file__).resolve().parent.parent;dest=root/'artifacts/fixtures'
stamp=sys.argv[1];assert len(stamp)==6 and stamp.isdigit()
company=(root/'QBData/Test.QBW').resolve();assert company==pathlib.Path(__import__("os").environ["QBCLI_LIVE_TEST_COMPANY"]).resolve(strict=True)
expected=runpy.run_path(str(root/'scripts/Test-DocumentReads.py'))['expected']
def scalar(n,k):return next((c.get('value') for c in n['children'] if c['name']==k),None)
def load(label):
    value=json.loads((dest/f'lifecycle-{stamp}-partial-documents-{label}-result.json').read_text(encoding='utf-8'),parse_float=Decimal)
    assert value['success'] and pathlib.Path(value['companyFile'])==company
    return value
seed=load('seed');ids={r['requestId']:scalar(r['data'][0],'TxnID') for r in seed['results']}
specs=[('invoice','Invoice','100.25','30.10'),('bill-check','Bill','101.35','31.10'),('bill-card','Bill','102.45','32.10')]
payment_ids={}
for stage in ('partial','paid'):
    result=load(stage+'-payments');assert len(result['results'])==3 and result['mutatesCompany']
    journal=[json.loads(line) for line in pathlib.Path(result['journalFile']).read_text(encoding='utf-8').splitlines()]
    assert [r['requestIds'] for r in journal if r['kind']=='submitted']==[['invoice','bill-check','bill-card']]
    assert any(r['kind']=='finished' for r in journal)
    for row,(key,kind,total,partial) in zip(result['results'],specs):
        assert row['requestId']==key and row['outcome']=='succeeded' and row['statusCode']==0
        record=row['data'][0];payment_ids[(stage,key)]=scalar(record,'TxnID')
        applied=[c for c in record['children'] if c['name']=='AppliedToTxnRet'];assert len(applied)==1
        assert scalar(applied[0],'TxnID')==ids[key]
        assert Decimal(scalar(applied[0],'Amount'))==(Decimal(partial) if stage=='partial' else Decimal(total)-Decimal(partial))
checks=[]
for stage in ('unpaid','partial','paid'):
    records={r['requestId']:r['data'][0] for r in load(stage+'-confirmed-sdk')['results']}
    payable=load(stage+'-payable');balances={scalar(row['children'][0],'TxnID'):Decimal(scalar(row['children'][0],'AmountDue')) for row in payable['results'][0]['data'] if row['children'][0]['name']=='BillToPay'}
    for key,kind,total,partial in specs:
        record=records[key];txn=ids[key];assert scalar(record,'TxnID')==txn
        remaining=Decimal(total) if stage=='unpaid' else Decimal(total)-Decimal(partial) if stage=='partial' else Decimal(0)
        assert scalar(record,'IsPaid')==('true' if stage=='paid' else 'false')
        if kind=='Bill':
            assert Decimal(scalar(record,'AmountDue'))==Decimal(total)
            assert (balances.get(txn,Decimal(0)))==remaining
            if stage=='paid':assert txn not in balances
        else:assert Decimal(scalar(record,'BalanceRemaining'))==remaining
        links={scalar(c,'TxnID'):c for c in record['children'] if c['name']=='LinkedTxn'}
        for payment_stage in ([] if stage=='unpaid' else ['partial'] if stage=='partial' else ['partial','paid']):
            assert payment_ids[(payment_stage,key)] in links
        command='invoices' if kind=='Invoice' else 'bills'
        for open_only in (False,True):
            value=load(stage+'-'+command+('-open' if open_only else '-all'));assert value['complete'] and value['openOnly']==open_only
            found=[r for r in value['results'] if r['id']==txn]
            assert len(found)==(0 if open_only and stage=='paid' else 1)
            if found:assert found[0]==expected(record,kind,balances) and found[0]['remainingAmount']==remaining
        checks.append({'documentType':kind.lower(),'stage':stage,'paymentType':'ReceivePayment' if kind=='Invoice' else 'BillPaymentCheck' if key=='bill-check' else 'BillPaymentCreditCard',
            'everySummaryFieldMatchesIndependentSdkRead':True,'paidFlagAndOpenMembershipVerified':True,'perDocumentRemainingAmountVerified':True,
            'paymentApplicationsAndLinkedTransactionIdsVerified':True,'independentRetainedEvidenceVerification':True,
            'scope':'Test.QBW QBD2024R21 US protocol13: one invoice and two bills sharing a fresh vendor; unpaid/partial/paid transitions, six explicit payment applications, journals and saved fresh reads. BillQuery OpenAmount was vendor aggregate; BillToPay AmountDue resolved actual bill balances. No credits/discount applications, multicurrency or other releases proof.'})
for stage in ('unpaid','partial'):
    records={r['requestId']:r['data'][0] for r in load(stage+'-confirmed-sdk')['results']}
    payable=load(stage+'-payable');total=sum(Decimal(scalar(r['children'][0],'AmountDue')) for r in payable['results'][0]['data'])
    assert all(Decimal(scalar(records[k],'OpenAmount'))==total for k in ('bill-check','bill-card'))
(dest/f'lifecycle-{stamp}-partial-documents-verification.json').write_text(json.dumps(checks,indent=2),encoding='utf-8')
print(json.dumps({'documentStagesVerified':len(checks),'paymentApplicationsVerified':6,'perBillBalanceSource':'BillToPayQuery','vendorAggregateOpenAmountObserved':True}))
