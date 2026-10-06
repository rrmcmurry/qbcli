"""Verify SDK deletion acknowledgements, exact absent IDs and remaining documents."""
import json
import pathlib
import runpy
import sys
from decimal import Decimal
ROOT=pathlib.Path(__file__).resolve().parent.parent;DEST=ROOT/'artifacts/fixtures'
STAMP=sys.argv[1];assert len(STAMP)==6 and STAMP.isdigit();PREFIX=f'lifecycle-{STAMP}-applied-deletion'
manifest=json.loads((DEST/(PREFIX+'-manifest.json')).read_text(encoding='utf-8'));SOURCE=manifest['sourceFixture'];assert len(SOURCE)==6 and SOURCE.isdigit()
args=sys.argv;sys.argv=[args[0],SOURCE]
try:source=runpy.run_path(str(ROOT/'scripts/Verify-AdditionalCreditMods.py'))
finally:sys.argv=args
company=source['company'];ids=source['ids'];extra=source['extra_ids'];payments=source['payments'];parties=source['parties']
scalar=source['scalar'];canon=source['canon'];expected=source['expected']
assert manifest=={'sourceFixture':SOURCE,'companyFile':str(company),'documentIds':ids,'extraCreditIds':extra,'paymentIds':payments,'partyIds':parties}
def load(label,suffix='result'):return json.loads((DEST/(PREFIX+'-'+label+'-'+suffix+'.json')).read_text(encoding='utf-8'),parse_float=Decimal)
credit_types={'customer-credit':'CreditMemo','extra-customer-credit':'CreditMemo','vendor-credit':'VendorCredit','extra-vendor-credit':'VendorCredit'}
for label,selected in [('delete-payments',{'receive':'ReceivePayment','pay-bills':'BillPaymentCheck'}),('delete-credits',credit_types)]:
    v=load(label);plan=load(label,'plan');assert v['success'] and v['mutatesCompany'] and pathlib.Path(v['companyFile'])==company
    assert len(v['results'])==len(plan['requests'])==len(selected)
    for request,row in zip(plan['requests'],v['results']):
        key=request['requestId'];txn=payments.get(key) or (ids|extra)[key]
        assert request['operation']==row['operation']=='TxnDel' and row['requestId']==key and request['fields']=={'TxnDelType':selected[key],'TxnID':txn}
        assert row['statusCode']==0 and row['outcome']=='succeeded'
        acknowledgement={'children':row['data']};names={n['name'] for n in row['data']}
        assert {'TxnDelType','TxnID','TimeDeleted'}<=names<={'TxnDelType','TxnID','TimeDeleted','RefNumber'} and len(names)==len(row['data'])
        assert scalar(acknowledgement,'TxnID')==txn and scalar(acknowledgement,'TxnDelType')==selected[key] and scalar(acknowledgement,'TimeDeleted')
    journal=[json.loads(line) for line in pathlib.Path(v['journalFile']).read_text(encoding='utf-8').splitlines()]
    assert [r['requestIds'] for r in journal if r['kind']=='submitted']==[[r['requestId'] for r in plan['requests']]] and any(r['kind']=='finished' for r in journal)
payment_stage='after-payments-confirmed' if (DEST/(PREFIX+'-after-payments-confirmed-sdk-result.json')).exists() else 'after-payments'
snapshots={};balances={}
for stage in ('before',payment_stage,'after-credits'):
    v=load(stage+'-sdk');plan=load(stage+'-sdk','plan');assert pathlib.Path(v['companyFile'])==company and not v['mutatesCompany'] and len(v['results'])==len(plan['requests'])==11
    assert v['success']==(stage=='before')
    deleted=set() if stage=='before' else set(payments)| (set(credit_types) if stage=='after-credits' else set())
    rows={}
    for request,row in zip(plan['requests'],v['results']):
        key=request['requestId'];assert key==row['requestId'] and request['operation']==row['operation']
        if key=='payable':assert request['fields']=={'PayeeEntityRef':{'ListID':parties['vendor']}}
        else:
            txn=payments.get(key) or (ids|extra)[key]
            assert request['fields']=={'TxnID':txn,'IncludeLineItems':True,**({} if key in payments else {'IncludeLinkedTxns':True})}
        if key in deleted:
            assert row['statusCode']==500 and row['outcome']=='warning' and not row['data']
            assert ('"'+txn+'"') in row['statusMessage'] and 'could not be found' in row['statusMessage']
        else:assert row['statusCode']==0 and row['outcome']=='succeeded' and row['data']
        rows[key]=row['data']
    payable=rows.pop('payable');assert all(n['children'][0]['name']=='BillToPay' for n in payable)
    balances[stage]={scalar(n['children'][0],'TxnID'):Decimal(scalar(n['children'][0],'AmountDue')) for n in payable};assert set(balances[stage])=={ids['bill-old'],ids['bill-new']}
    snapshots[stage]={k:n[0] for k,n in rows.items() if n}
before=snapshots['before'];checks=[]
for document,kind in [('invoice-old','Invoice'),('invoice-new','Invoice'),('bill-old','Bill'),('bill-new','Bill')]:
    for stage in ('before',payment_stage,'after-credits'):
        record=snapshots[stage][document];old=document.endswith('old');customer=kind=='Invoice'
        remaining=(70 if old else 60) if stage=='before' else (70 if old and stage==payment_stage else 100)
        actual=Decimal(scalar(record,'BalanceRemaining')) if customer else balances[stage][ids[document]]
        assert actual==remaining and scalar(record,'IsPaid')=='false'
        if customer:assert Decimal(scalar(record,'AppliedAmount'))==-(100-remaining)
        else:assert Decimal(scalar(record,'AmountDue'))==100 and Decimal(scalar(record,'OpenAmount'))==sum(balances[stage].values())
        assert canon(before[document],('TimeModified','EditSequence','BalanceRemaining','AppliedAmount','OpenAmount','LinkedTxn'))==canon(record,('TimeModified','EditSequence','BalanceRemaining','AppliedAmount','OpenAmount','LinkedTxn'))
        credit='customer-credit' if customer else 'vendor-credit';added='extra-'+credit;payment='receive' if customer else 'pay-bills'
        links={scalar(c,'TxnID'):c for c in record['children'] if c['name']=='LinkedTxn'}
        wanted=({ids[credit],extra[added]} if old else {payments[payment]}) if stage=='before' else ({ids[credit],extra[added]} if old and stage==payment_stage else set())
        assert set(links)==wanted
        for txn,amount in [(ids[credit],20),(extra[added],10),(payments[payment],40)]:
            if txn in links:assert abs(Decimal(scalar(links[txn],'Amount')))==amount
        for open_only in (False,True):
            normalized=load(stage+'-'+('invoices' if customer else 'bills')+('-open' if open_only else '-all'));assert normalized['success'] and pathlib.Path(normalized['companyFile'])==company and normalized['complete'] and normalized['openOnly']==open_only
            found=[r for r in normalized['results'] if r['id']==ids[document]];assert len(found)==1 and found[0]==expected(record,kind,balances[stage])
        checks.append({'operation':'TxnDel','documentType':kind.lower(),'documentRole':'original-credit-target' if old else 'cash-discount-target','stage':stage,
            'remainingDocumentBalanceAndLinksVerified':True,'unrelatedQueriedFieldsPreservedVerified':True,'normalizedSummaryAndOpenMembershipVerified':True,
            'exactDeletedIdsWarning500Verified':stage!='before','sdkDeletionAcknowledgementsAndJournalsVerified':True,'independentRetainedEvidenceVerification':True,
            'scope':'Test.QBW QBD2024R21 US protocol13: two synthetic payments with cash30/discount10 deleted, releasing replacement documents60->100 while original documents70 retained credit20+10. Four applied credits then deleted, releasing originals70->100 and removing all retained links. Exact deleted-ID queries returned empty warning500, retained as warning/overall batch success=false, not converted to no_match. All twelve normalized document states, queried lines/unrelated headers, credit reverse links, SDK delete IDs/types and journals verified. Initial post-delete harness halted on expected warnings; confirmed deletions were not replayed. No voiding, deleting original invoices/bills, deposited/reconciled payments, multicurrency or other releases proof.'})
for key in credit_types:
    original=before[key];after=snapshots[payment_stage][key]
    assert canon(original,('TimeModified','EditSequence','OpenAmount'))==canon(after,('TimeModified','EditSequence','OpenAmount'))
    if key.endswith('customer-credit'):assert Decimal(scalar(after,'CreditRemaining'))==0
    else:assert Decimal(scalar(after,'OpenAmount'))==sum(balances[payment_stage].values())
(DEST/(PREFIX+'-verification.json')).write_text(json.dumps(checks,indent=2),encoding='utf-8')
print(json.dumps({'documentStatesVerified':len(checks),'paymentsDeleted':2,'appliedCreditsDeleted':4,'paymentDeletionRetainedCreditLinks':True,'creditDeletionReleasedOriginalDocuments':True,'deletedIdWarningsPreserved':True}))
