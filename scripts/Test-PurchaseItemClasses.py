"""Purchase item class preservation probe only on expendable Test.QBW."""
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
STAMP=datetime.datetime.now().strftime('%H%M%S'); PREFIX='BUYITEM'+STAMP
TYPES=['Check','Bill','CreditCardCharge','CreditCardCredit','VendorCredit','ItemReceipt']
parser=argparse.ArgumentParser(description=__doc__)
parser.add_argument('--public-bulk',action='store_true',help='Verify simultaneous item and expense classification through public bulk preview/apply/repeat.')
parser.add_argument('--linked-billable',action='store_true',help='Add customer/billable fields and linked purchase-order lines; requires public bulk.')
parser.add_argument('--billable-only',action='store_true',help='Add customer/billable fields without purchase-order linking; requires public bulk.')
parser.add_argument('--inventory',action='store_true',help='Probe inventory purchase asset-account classes with a mixed inventory/non-inventory group.')
parser.add_argument('--existing-fixture',help='Continue confirmed inventory seed identities from a read failure; never replay seed writes.')
options=parser.parse_args()
assert not options.linked_billable or options.public_bulk
assert not options.billable_only or (options.public_bulk and not options.linked_billable)
assert not options.inventory or not(options.linked_billable or options.billable_only)
assert not options.existing_fixture or (options.inventory and not options.public_bulk and len(options.existing_fixture)==6 and options.existing_fixture.isdigit())
def scalar(n,key):
    values=[c for c in n['children'] if c['name']==key]; assert len(values)<=1
    return values[0].get('value') if values else None
def ref(n,key):
    values=[c for c in n['children'] if c['name']==key]; assert len(values)<=1
    return scalar(values[0],'ListID') if values else None
def walk(n):
    yield n
    for c in n['children']: yield from walk(c)
def call(operation,fields,key): return {'requestId':key,'operation':operation,'fields':{operation:fields}}
def run(label,args):
    proc=subprocess.run([str(EXE),*args,'--company-file',str(COMPANY),'--json'],capture_output=True,text=True,encoding='utf-8')
    (DEST/f'lifecycle-{STAMP}-purchase-item-{label}-result.json').write_text(proc.stdout,encoding='utf-8')
    value=json.loads(proc.stdout)
    print(json.dumps({'phase':label,'success':value.get('success'),'outcomes':[{k:r.get(k) for k in ('operation','outcome','statusCode')} for r in value.get('results',[])]}),flush=True)
    assert proc.returncode==0 and value['success'], 'Keep evidence/journal; do not replay'
    return value
def batch(label,requests,apply=False):
    path=DEST/f'lifecycle-{STAMP}-purchase-item-{label}-plan.json'
    path.write_text(json.dumps({'protocolVersion':'13.0','country':'US','onError':'stopOnError','requests':requests}),encoding='utf-8')
    return run(label,['sdk','batch','--input',str(path),'--batch-size','3',*(['--apply'] if apply else [])])
def retained(label):
    value=json.loads((DEST/f'lifecycle-{options.existing_fixture}-purchase-item-{label}-result.json').read_text(encoding='utf-8'))
    assert value['success'] and all(r['outcome']=='succeeded' for r in value['results'])
    return value
base=retained('base') if options.existing_fixture else batch('base',[call('AccountAdd',{'Name':PREFIX+' '+name,'AccountType':'Expense'},name) for name in ['Expense','Other']]+
    ([call('AccountAdd',{'Name':PREFIX+' '+name,'AccountType':kind},name) for name,kind in [('Income','Income'),('COGS','CostOfGoodsSold'),('Asset','OtherCurrentAsset')]] if options.inventory else [])+
    [call('ClassAdd',{'Name':PREFIX+' '+name},name) for name in ['Target','Existing']],True)
ids={r['requestId']:scalar(r['data'][0],'ListID') for r in base['results']}
requests=[]
for kind,suffix in [('ItemNonInventory','NI'),('ItemOtherCharge','OC'),('ItemService','Service')]:
    fields={'Name':PREFIX+' '+suffix}
    if kind=='ItemOtherCharge':fields['SalesOrPurchase']={'Price':'3.00','AccountRef':{'ListID':ids['Expense']}}
    else:fields['SalesAndPurchase']={'SalesPrice':'5.00','IncomeAccountRef':{'FullName':'SDK154955 Income'},'PurchaseCost':'2.00','ExpenseAccountRef':{'ListID':ids['Expense']}}
    requests.append(call(kind+'Add',fields,suffix))
items=retained('items') if options.existing_fixture else batch('items',requests,True)
item_ids={r['requestId']:scalar(r['data'][0],'ListID') for r in items['results']}
if options.inventory:
    item=retained('inventory') if options.existing_fixture else batch('inventory',[call('ItemInventoryAdd',{'Name':PREFIX+' Inventory','SalesPrice':'5.00','PurchaseCost':'2.00',
        'IncomeAccountRef':{'ListID':ids['Income']},'COGSAccountRef':{'ListID':ids['COGS']},'AssetAccountRef':{'ListID':ids['Asset']},
        'QuantityOnHand':'20','TotalValue':'40.00','InventoryDate':'2026-10-01'},'inventory')],True)
    item_ids['Inventory']=scalar(item['results'][0]['data'][0],'ListID')
selected_account=ids['Asset' if options.inventory else 'Expense']
group=retained('group') if options.existing_fixture else batch('group',[call('ItemGroupAdd',{'Name':PREFIX+' Group','IsPrintItemsInGroup':True,'ItemGroupLine':[
    {'ItemRef':{'ListID':item_ids['Inventory' if options.inventory else 'NI']},'Quantity':'1'},
    {'ItemRef':{'ListID':item_ids['NI' if options.inventory else 'OC']},'Quantity':'1'}]},'group')],True)
group_id=scalar(group['results'][0]['data'][0],'ListID')
orders={}
if options.linked_billable:
    po_result=batch('purchase-orders',[call('PurchaseOrderAdd',{'VendorRef':{'FullName':'SDK154955 Vendor'},'TxnDate':'2026-10-05',
        'Memo':PREFIX+' PO '+kind,'PurchaseOrderLineAdd':{'ItemRef':{'ListID':item_ids['NI']},'Quantity':'2','Rate':'11.00','Amount':'22.00'}},kind) for kind in ['Bill','ItemReceipt']],True)
    for outcome in po_result['results']:
        record=outcome['data'][0];line=next(n for n in record['children'] if n['name']=='PurchaseOrderLineRet')
        orders[outcome['requestId']]={'TxnID':scalar(record,'TxnID'),'TxnLineID':scalar(line,'TxnLineID')}
requests=[]
for kind in TYPES:
    body={'TxnDate':'2026-10-05','Memo':PREFIX+' '+kind,'ExpenseLineAdd':[
        {'AccountRef':{'ListID':ids['Expense']},'Amount':'7.00'},
        {'AccountRef':{'ListID':ids['Other']},'Amount':'8.00','ClassRef':{'ListID':ids['Existing']}}],
        'ItemLineAdd':[
            {'ItemRef':{'ListID':item_ids['Inventory' if options.inventory else 'NI']},'Quantity':'1','Amount':'11.00'},
            {'ItemRef':{'ListID':item_ids['Inventory' if options.inventory else 'NI']},'Quantity':'1','Amount':'12.00','ClassRef':{'ListID':ids['Existing']}},
            {'ItemRef':{'ListID':item_ids['OC']},'Quantity':'1','Amount':'13.00','OverrideItemAccountRef':{'ListID':ids['Other']}},
            {'ItemRef':{'ListID':item_ids['Service']},'Quantity':'1','Amount':'14.00'}],
        'ItemGroupLineAdd':{'ItemGroupRef':{'ListID':group_id},'Quantity':'1'}}
    if kind in ('Check','CreditCardCharge','CreditCardCredit'):
        body.update(AccountRef={'FullName':'SDK154955 '+('Bank' if kind=='Check' else 'Card')},PayeeEntityRef={'FullName':'SDK154955 Vendor'})
    else:body.update(VendorRef={'FullName':'SDK154955 Vendor'},APAccountRef={'FullName':'SDK154955 AP'})
    if options.linked_billable or options.billable_only:
        for line in body['ExpenseLineAdd']+body['ItemLineAdd']:
            line.update(CustomerRef={'FullName':'SDK154955 Customer'},BillableStatus='Billable')
        if kind in orders:
            body['ItemLineAdd'][0].pop('ItemRef')
            body['ItemLineAdd'][0]['LinkToTxn']=orders[kind]
    requests.append(call(kind+'Add',body,kind))
seed=retained('seed') if options.existing_fixture else batch('seed',requests,True)
txns={r['requestId']:scalar(r['data'][0],'TxnID') for r in seed['results']}
if options.existing_fixture:
    for prior in DEST.glob('lifecycle-*-purchase-item-apply-plan.json'):
        plan=json.loads(prior.read_text(encoding='utf-8'))
        assert not any(r.get('fields',{}).get(r['operation'],{}).get('TxnID') in txns.values() for r in plan['requests']), 'Class modification already planned for this fixture; inspect that run rather than replay'
def forms(label):
    value=batch(label,[{'requestId':kind,'operation':kind+'Query','fields':{'TxnID':txn,'IncludeLineItems':True,
        **({'IncludeLinkedTxns':True} if kind in ['Check','Bill','VendorCredit','ItemReceipt'] else {})}} for kind,txn in txns.items()])
    return {r['requestId']:r['data'][0] for r in value['results']}
def postings(label):
    fields=DEST/f'lifecycle-{STAMP}-purchase-item-{label}-fields.json'
    fields.write_text(json.dumps({'TransactionTypeFilter':{'TxnTypeFilter':TYPES},'TransactionDetailLevelFilter':'AllExceptSummary'}),encoding='utf-8')
    value=run(label,['sdk','run','TransactionQuery','--input',str(fields),'--all','--page-size','500'])
    assert value['complete']
    return [n for n in value['data'] if scalar(n,'TxnID') in txns.values()]
before=forms('before-forms'); old_posts=postings('before-postings')
def inventory_state(label):
    return batch(label,[{'requestId':'inventory','operation':'ItemInventoryQuery','fields':{'ListID':item_ids['Inventory']}}])['results'][0]['data'][0]
before_inventory=inventory_state('before-inventory') if options.inventory else None
def po_forms(label):
    value=batch(label,[{'requestId':kind,'operation':'PurchaseOrderQuery','fields':{'TxnID':identity['TxnID'],'IncludeLineItems':True,'IncludeLinkedTxns':True}} for kind,identity in orders.items()])
    return {r['requestId']:r['data'][0] for r in value['results']}
before_orders=po_forms('before-orders') if orders else {}
if options.linked_billable or options.billable_only:
    for kind,record in before.items():
        flat=[n for n in record['children'] if n['name'] in ('ItemLineRet','ExpenseLineRet')]
        assert len(flat)==6 and all(ref(n,'CustomerRef') is not None and scalar(n,'BillableStatus')=='Billable' for n in flat)
        if kind in orders:
            assert any(scalar(n,'TxnID')==orders[kind]['TxnID'] for n in record['children'] if n['name']=='LinkedTxn')
selected={}; requests=[]
for kind,record in before.items():
    rows=[r for r in old_posts if scalar(r,'TxnID')==txns[kind] and scalar(r,'TxnType')==kind]
    selected[kind]=set()
    def mod(line):
        line_id=scalar(line,'TxnLineID'); matches=[r for r in rows if scalar(r,'TxnLineID')==line_id]
        if options.inventory and ref(line,'ItemRef')==item_ids['Inventory']:
            matches=[r for r in matches if ref(r,'AccountRef')==ids['Asset'] and abs(Decimal(scalar(r,'Amount')))==abs(Decimal(scalar(line,'Amount')))]
        assert len(matches)==1 and ref(matches[0],'AccountRef') is not None, 'Unverified purchase posting join'
        assert abs(Decimal(scalar(matches[0],'Amount')))==abs(Decimal(scalar(line,'Amount')))
        cls=ref(line,'ClassRef'); account=ref(matches[0],'AccountRef')
        body={'TxnLineID':line_id,'OverrideItemAccountRef':{'ListID':account}}
        if cls is None and account==selected_account: cls=ids['Target']; selected[kind].add(line_id)
        if cls: body['ClassRef']={'ListID':cls}
        return body
    choices=[]
    for node in record['children']:
        if node['name']=='ItemLineRet': choices.append({'ItemLineMod':mod(node)})
        elif node['name']=='ItemGroupLineRet': choices.append({'ItemGroupLineMod':{'TxnLineID':scalar(node,'TxnLineID'),'ItemLineMod':[mod(c) for c in node['children'] if c['name']=='ItemLineRet']}})
    assert len(selected[kind])==(2 if options.inventory else 4)
    requests.append(call(kind+'Mod',{'TxnID':txns[kind],'EditSequence':scalar(record,'EditSequence'),'$choices':choices},kind))
if options.public_bulk:
    selection=DEST/f'lifecycle-{STAMP}-purchase-item-bulk-selection.json'
    selection.write_text(json.dumps({'accountId':selected_account,'classId':ids['Target'],'transactionTypes':TYPES}),encoding='utf-8')
    args=['bulk','reclassify','--input',str(selection),'--page-size','25','--batch-size','3']
    preview=run('bulk-preview',args)
    assert preview['execution'] is None and preview['counts']['transactionsToChange']==6 and preview['counts']['linesToChange']==(12 if options.inventory else 30)
    applied=run('bulk-apply',[*args,'--apply'])
    assert len(applied['verification'])==6 and all(v['classVerified'] and v['otherFieldsPreserved'] and v['postingAccountsAndAmountsPreserved'] for v in applied['verification'])
    assert len(applied['execution']['results'])==6
    (DEST/f'lifecycle-{STAMP}-purchase-item-bulk-execution-result.json').write_text(json.dumps(applied['execution'],indent=2),encoding='utf-8')
    for kind,record in before.items():
        for line in record['children']:
            if line['name']=='ExpenseLineRet' and ref(line,'AccountRef')==selected_account and ref(line,'ClassRef') is None:
                selected[kind].add(scalar(line,'TxnLineID'))
        assert len(selected[kind])==(2 if options.inventory else 5)
else:
    preview=batch('preview',requests); assert not preview['submitted']
    batch('apply',requests,True)
after=forms('after-forms'); new_posts=postings('after-postings')
def canonical(n,selected=()): return (n['name'],tuple(sorted(n['attributes'].items())),n.get('value'),tuple(canonical(c,selected) for c in n['children'] if c['name'] not in ('TimeModified','EditSequence') and not(c['name']=='ClassRef' and scalar(n,'TxnLineID') in selected)))
assert Counter(map(canonical,old_posts))==Counter(map(canonical,new_posts)), 'Purchase postings changed'
checks=[]
if options.inventory:
    after_inventory=inventory_state('after-inventory')
    assert canonical(before_inventory)==canonical(after_inventory), 'Inventory quantity/cost/definition changed'
if orders:
    after_orders=po_forms('after-orders')
    assert all(canonical(before_orders[kind])==canonical(after_orders[kind]) for kind in orders), 'Purchase order links or received/unbilled state changed'
for kind in TYPES:
    assert canonical(before[kind],selected[kind])==canonical(after[kind],selected[kind]), 'Other form fields changed'
    item_lines={scalar(n,'TxnLineID'):n for n in walk(after[kind]) if n['name']=='ItemLineRet'}
    lines={scalar(n,'TxnLineID'):n for n in walk(after[kind]) if n['name'] in ('ItemLineRet','ExpenseLineRet')}
    assert len(item_lines)==6 and all(ref(lines[line],'ClassRef')==ids['Target'] for line in selected[kind])
    checks.append({'transactionType':kind,'classesChanged':len(selected[kind]),'retainedItemLeaves':6,'flatAndGroupClassesVerified':True,
        'unselectedReturnedFormFieldsPreserved':True,'allReturnedPostingFieldsPreserved':True,'expenseLinesUntouched':True,
        'scope':'Explicit typed SDK purchase-item recipe on Test.QBW service/non-inventory/other-charge flat/group fixtures with actual posting overrides. Not generic purchase bulk support, inventory, PO links, billable/customer allocation or other SDK configurations.'})
if options.public_bulk:
    repeat=run('bulk-repeat-preview',args)
    assert repeat['counts']['transactionsToChange']==0 and repeat['counts']['linesToChange']==0
    for check in checks:
        check.update(expenseLinesUntouched=False,simultaneousExpenseAndItemClassesVerified=True,oneModifierPerTransaction=True,repeatPreviewHasNoResidualChanges=True,
            scope='Public combined item/expense bulk recipe on fresh Test.QBW service/non-inventory/other-charge purchase fixtures. No inventory, PO links, billable/customer allocation or other SDK configurations.')
if options.linked_billable or options.billable_only:
    for check in checks:
        check.update(customerAndBillableFieldsPreserved=True,purchaseOrderLinked=check['transactionType'] in orders,
            purchaseOrderReturnedFieldsAndReceivingStatePreserved=True if check['transactionType'] in orders else None,
            scope='Public combined purchase item/expense fixture with Billable customer allocations on flat items/expense lines. If present, both directions of returned PO links and receiving state preserved. No already-billed, billable group-child, inventory, every PO link shape or other-release proof.')
if options.inventory:
    for check in checks:
        check.update(expenseLinesUntouched=True,simultaneousExpenseAndItemClassesVerified=False,itemQuantitiesCostsAndDefinitionsPreserved=True,
            scope='Inventory purchase asset-account class fixture with flat inventory lines and mixed inventory/non-inventory groups on Test.QBW. Actual asset postings, all returned form/posting fields and item quantities/costs/definitions preserved. No purchase-order link, assembly, changed item defaults or other-release proof.')
suffix=('-inventory-bulk' if options.public_bulk else '-inventory') if options.inventory else '-linked-billable' if options.linked_billable else '-billable' if options.billable_only else '-bulk' if options.public_bulk else ''
(DEST/f'lifecycle-{STAMP}-purchase-item{suffix}-verification.json').write_text(json.dumps(checks,indent=2),encoding='utf-8')
print(json.dumps(checks),flush=True)
