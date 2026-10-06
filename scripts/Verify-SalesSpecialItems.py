"""Independently verify retained special-item public bulk evidence, without replay."""
from collections import Counter
from decimal import Decimal, ROUND_HALF_UP
import json
import pathlib
import sys
root=pathlib.Path(__file__).resolve().parent.parent;dest=root/'artifacts/fixtures'
stamp=sys.argv[1];assert len(stamp)==6 and stamp.isdigit()
def load(label):
    v=json.loads((dest/f'lifecycle-{stamp}-special-sales-{label}-result.json').read_text(encoding='utf-8'));assert v['success'];return v
def scalar(n,k):return next((c.get('value') for c in n['children'] if c['name']==k),None)
def ref(n,k):return next((scalar(c,'ListID') for c in n['children'] if c['name']==k),None)
def canonical(n,selected=()):return (n['name'],tuple(sorted(n['attributes'].items())),n.get('value'),tuple(canonical(c,selected) for c in n['children']
    if c['name'] not in ('TimeModified','EditSequence') and not(c['name']=='ClassRef' and scalar(n,'TxnLineID') in selected)))
old={r['requestId']:r['data'][0] for r in load('before-forms')['results']};new={r['requestId']:r['data'][0] for r in load('after-forms')['results']}
before=load('before-postings');after=load('after-postings');assert before['complete'] and after['complete']
base={r['requestId']:scalar(r['data'][0],'ListID') for r in load('base')['results']}
discount_account=base.get('discount-income',base['income'])
discount_only='discount-income' in base
selected_count=1 if discount_only else 3
selection=json.loads((dest/f'lifecycle-{stamp}-special-sales-selection.json').read_text(encoding='utf-8'))
assert selection['accountId']==(discount_account if discount_only else base['income']) and selection['classId']==base['class']
items={r['requestId']:scalar(r['data'][0],'ListID') for r in load('items')['results']}
itemplan=json.loads((dest/f'lifecycle-{stamp}-special-sales-items-plan.json').read_text(encoding='utf-8'))
discount_input=next(r['fields']['ItemDiscountAdd'] for r in itemplan['requests'] if r['operation']=='ItemDiscountAdd')
percentage='DiscountRatePercent' in discount_input
mode='percentage' if percentage else 'fixed'
if percentage:
    assert Decimal(discount_input['DiscountRatePercent'])==Decimal('12.12345')
    item_definition=next(r['data'][0] for r in load('items')['results'] if r['requestId']=='discount')
    assert Decimal(scalar(item_definition,'DiscountRatePercent'))==Decimal('12.12345')
applied=load('bulk-apply');preview=load('bulk-preview');repeat=load('bulk-repeat-preview')
assert preview['execution'] is None and applied['counts']['transactionsToChange']==3 and applied['counts']['linesToChange']==3*selected_count
assert repeat['counts']['transactionsToChange']==repeat['counts']['linesToChange']==0
assert len(applied['changes'])==len(applied['execution']['results'])==3 and all(r['outcome']=='succeeded' for r in applied['execution']['results'])
assert all(v['classVerified'] and v['otherFieldsPreserved'] and v['postingAccountsAndAmountsPreserved'] for v in applied['verification'])
journal=[json.loads(line) for line in pathlib.Path(applied['execution']['journalFile']).read_text(encoding='utf-8').splitlines()]
assert [len(r['requestIds']) for r in journal if r['kind']=='submitted']==[2,1]
checks=[]
for change,request in zip(applied['changes'],applied['plan']['requests']):
    kind=change['transactionType'];txn=change['txnId'];selected=set(change['lineIds']);assert len(selected)==selected_count
    body=request['fields'][kind+'Mod'];assert body['TxnID']==txn and body['EditSequence']==change['editSequence'] and len(body['$choices'])==4
    lines=[n for n in old[kind]['children'] if n['name']==kind+'LineRet'];assert len(lines)==4
    if percentage:
        assert ref(lines[2],'ItemRef')==items['subtotal'] and ref(lines[3],'ItemRef')==items['discount']
        subtotal=sum(Decimal(scalar(n,'Amount')) for n in lines[:2])
        assert subtotal==Decimal(scalar(lines[2],'Amount'))==Decimal('21.00')
        percent=Decimal(scalar(lines[3],'RatePercent'));assert percent==Decimal('-12.12345')
        expected=(subtotal*percent/Decimal('100')).quantize(Decimal('.01'),rounding=ROUND_HALF_UP)
        assert Decimal(scalar(lines[3],'Amount'))==expected==Decimal('-2.55')
        seedplan=json.loads((dest/f'lifecycle-{stamp}-special-sales-seed-plan.json').read_text(encoding='utf-8'))
        seedbody=next(r['fields'][kind+'Add'] for r in seedplan['requests'] if r['requestId']==kind)
        assert Decimal(seedbody[kind+'LineAdd'][3]['RatePercent'])==abs(percent)
    for line,choice in zip(lines,body['$choices']):
        mod=choice[kind+'LineMod'];assert mod['TxnLineID']==scalar(line,'TxnLineID')
        if ref(line,'ItemRef')==items['subtotal']:assert set(mod)=={'TxnLineID'} and mod['TxnLineID'] not in selected
        else:
            is_discount=ref(line,'ItemRef')==items['discount']
            assert mod['OverrideItemAccountRef']['ListID']==(discount_account if is_discount else base['income'])
            chosen=is_discount or not discount_only
            assert (mod['TxnLineID'] in selected)==chosen
            if chosen:assert mod['ClassRef']['ListID']==base['class']
            else:assert 'ClassRef' not in mod and ref(line,'ClassRef') is None
    assert canonical(old[kind],selected)==canonical(new[kind],selected)==canonical(change['original'],selected)
    returned={scalar(n,'TxnLineID'):n for n in new[kind]['children'] if n['name']==kind+'LineRet'}
    assert all(ref(returned[x],'ClassRef')==base['class'] for x in selected)
    oldrows=[n for n in before['data'] if scalar(n,'TxnType')==kind and scalar(n,'TxnID')==txn];newrows=[n for n in after['data'] if scalar(n,'TxnType')==kind and scalar(n,'TxnID')==txn]
    assert oldrows and Counter(map(canonical,oldrows))==Counter(map(canonical,newrows))==Counter(map(canonical,change['originalPostings']))
    checks.append({'transactionType':kind,'classesChanged':selected_count,'retainedLeaves':4,'subtotalPreserved':True,'discountClassVerified':True,'discountMode':mode,'discountAccountOnlySelection':discount_only,
        'fiveDecimalDiscountPercentPreserved':True if percentage else None,'subtotalDiscountCalculationPreserved':True if percentage else None,
        'allOtherReturnedFormFieldsPreserved':True,'allReturnedPostingFieldsPreserved':True,'publicBulkVerified':True,
        'repeatPreviewHasNoResidualChanges':True,'journalMutationBatchSizes':[2,1],'independentRetainedEvidenceVerification':True,
        'scope':f'Public bulk flat service/subtotal/{mode}-discount recipe on Test.QBW QBD2024R21 US protocol13. Percentage control verifies signed five-decimal RatePercent and rounded subtotal discount when present. No payment/salestax items, groups or other releases proof.'})
(dest/f'lifecycle-{stamp}-special-sales-bulk-verification.json').write_text(json.dumps(checks,indent=2),encoding='utf-8')
print(json.dumps({'independentlyVerifiedSalesTypes':len(checks),'classesChanged':3*selected_count,'discountAccountOnlySelection':discount_only}))
