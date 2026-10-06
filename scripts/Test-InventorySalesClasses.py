"""Inventory/COGS sales class preservation probe, only in expendable Test.QBW.

Fixture-specific SDK recipe, not generic inventory bulk support. No backups,
restores, automatic retries or direct company-database access.
"""
import datetime
import argparse
from decimal import Decimal
import json
import pathlib
import subprocess
import sys
from collections import Counter

ROOT=pathlib.Path(__file__).resolve().parent.parent
DEST=ROOT/'artifacts/fixtures'
COMPANY=(ROOT/'QBData/Test.QBW').resolve()
assert COMPANY==pathlib.Path(__import__("os").environ["QBCLI_LIVE_TEST_COMPANY"]).resolve(strict=True)
EXE=ROOT/'artifacts/win-x64/qbcli.exe'
STAMP=datetime.datetime.now().strftime('%H%M%S')
parser=argparse.ArgumentParser(description=__doc__)
parser.add_argument('--existing-fixture',help='Continue a fixture stopped at a read before default/class writes; seed writes are not replayed.')
parser.add_argument('--class-baseline',action='store_true',help='Test class changes against the current posting baseline without modifying any item defaults; requires an existing fixture.')
parser.add_argument('--seed-only',action='store_true',help='Create fresh mixed inventory/service sales forms without modifying defaults or classes.')
options=parser.parse_args()
assert not options.class_baseline or options.existing_fixture
assert not options.seed_only or not options.existing_fixture
if options.existing_fixture:
    assert len(options.existing_fixture)==6 and options.existing_fixture.isdigit()
    assert options.class_baseline or not (DEST/f'lifecycle-{options.existing_fixture}-inventory-sales-change-defaults-result.json').exists()
    assert not (DEST/f'lifecycle-{options.existing_fixture}-inventory-sales-apply-result.json').exists()
PREFIX='INVCLASS'+STAMP
TYPES=['Invoice','SalesReceipt','CreditMemo']

def leaf(n,key):
    values=[c for c in n['children'] if c['name']==key]; assert len(values)<=1
    return values[0].get('value') if values else None
def ref(n,key):
    values=[c for c in n['children'] if c['name']==key]; assert len(values)<=1
    return leaf(values[0],'ListID') if values else None
def walk(n):
    yield n
    for c in n['children']:
        yield from walk(c)
def call(operation,body,identity):
    return {'requestId':identity,'operation':operation,'fields':{operation:body}}
def run(label,args):
    proc=subprocess.run([str(EXE),*args,'--company-file',str(COMPANY),'--json'],capture_output=True,text=True,encoding='utf-8')
    (DEST/f'lifecycle-{STAMP}-inventory-sales-{label}.json').write_text(proc.stdout,encoding='utf-8')
    value=json.loads(proc.stdout)
    print(json.dumps({'phase':label,'success':value.get('success'),'outcomes':[
        {k:r.get(k) for k in ('operation','outcome','statusCode')} for r in value.get('results',[])]}),flush=True)
    assert proc.returncode==0 and value['success'], 'SDK failure: keep journal and do not replay'
    return value
def batch(label,requests,apply=False,single=False):
    path=DEST/f'lifecycle-{STAMP}-inventory-sales-{label}-plan.json'
    path.write_text(json.dumps({'protocolVersion':'13.0','country':'US','onError':'stopOnError','requests':requests}),encoding='utf-8')
    return run(label+'-result',['sdk','batch','--input',str(path),*(['--apply'] if apply else []),*(['--file-mode','single-user'] if single else [])])

def retained(label):
    value=json.loads((DEST/f'lifecycle-{options.existing_fixture}-inventory-sales-{label}-result.json').read_text(encoding='utf-8'))
    assert value['success'] and all(r['outcome']=='succeeded' for r in value['results'])
    return value

base=retained('base') if options.existing_fixture else batch('base',[call('AccountAdd',{'Name':PREFIX+' '+name,'AccountType':kind},name) for name,kind in
    [('Income','Income'),('Override','Income'),('COGS','CostOfGoodsSold'),('NewCOGS','CostOfGoodsSold'),('Asset','OtherCurrentAsset')]]+
    [call('ClassAdd',{'Name':PREFIX+' '+name},name) for name in ['Target','Existing']],True)
ids={r['requestId']:leaf(r['data'][0],'ListID') for r in base['results']}
item_result=retained('item') if options.existing_fixture else batch('item',[call('ItemInventoryAdd',{'Name':PREFIX+' Item','SalesPrice':'5.00','PurchaseCost':'2.00',
    'IncomeAccountRef':{'ListID':ids['Income']},'COGSAccountRef':{'ListID':ids['COGS']},'AssetAccountRef':{'ListID':ids['Asset']},
    'QuantityOnHand':'20','TotalValue':'40.00','InventoryDate':'2026-10-01'},'inventory')],True)
inventory=leaf(item_result['results'][0]['data'][0],'ListID')
if options.existing_fixture and not options.class_baseline:
    for prior in DEST.glob('lifecycle-*-inventory-sales-change-defaults-plan.json'):
        prior_plan=json.loads(prior.read_text(encoding='utf-8'))
        if any(r.get('fields',{}).get('ItemInventoryMod',{}).get('ListID')==inventory for r in prior_plan['requests']):
            raise AssertionError('Default-account changes already planned for this item; inspect that run instead of replaying them')
group_result=retained('group') if options.existing_fixture else batch('group',[call('ItemGroupAdd',{'Name':PREFIX+' Group','IsPrintItemsInGroup':True,'ItemGroupLine':[
    {'ItemRef':{'ListID':inventory},'Quantity':'1'},{'ItemRef':{'FullName':'SDK154955 Service'},'Quantity':'1'}]},'group')],True)
group=leaf(group_result['results'][0]['data'][0],'ListID')
requests=[]
for kind in TYPES:
    body={'CustomerRef':{'FullName':'SDK154955 Customer'},'TxnDate':'2026-10-05','Memo':PREFIX+' '+kind,
        kind+'LineAdd':[{'ItemRef':{'ListID':inventory},'Quantity':'1','Amount':'11.00'},
            {'ItemRef':{'ListID':inventory},'Quantity':'1','Amount':'12.00','ClassRef':{'ListID':ids['Existing']},'OverrideItemAccountRef':{'ListID':ids['Override']}}],
        kind+'LineGroupAdd':{'ItemGroupRef':{'ListID':group},'Quantity':'1'}}
    body['DepositToAccountRef' if kind=='SalesReceipt' else 'ARAccountRef']={'FullName':'SDK154955 Bank' if kind=='SalesReceipt' else 'SDK154955 AR'}
    requests.append(call(kind+'Add',body,kind))
seed=retained('seed') if options.existing_fixture else batch('seed',requests,True)
if options.seed_only:
    (DEST/f'lifecycle-{STAMP}-inventory-sales-seed-verification.json').write_text(json.dumps({'fixtureStamp':STAMP,'seedOnly':True}),encoding='utf-8')
    sys.exit(0)
txns={r['requestId']:leaf(r['data'][0],'TxnID') for r in seed['results']}
def forms(label):
    value=batch(label,[{'requestId':kind,'operation':kind+'Query','fields':{'TxnID':txns[kind],'IncludeLineItems':True,
        **({'IncludeLinkedTxns':True} if kind!='SalesReceipt' else {})}} for kind in TYPES])
    return {r['requestId']:r['data'][0] for r in value['results']}
def postings(label):
    path=DEST/f'lifecycle-{STAMP}-inventory-sales-{label}-fields.json'
    path.write_text(json.dumps({'TransactionTypeFilter':{'TxnTypeFilter':TYPES},'TransactionDetailLevelFilter':'AllExceptSummary'}),encoding='utf-8')
    value=run(label,['sdk','run','TransactionQuery','--input',str(path),'--all','--page-size','500'])
    assert value['complete']
    return [n for n in value['data'] if leaf(n,'TxnID') in txns.values()]
before=forms('before-forms')
original_posts=postings('original-postings')
fresh_item=batch('fresh-item',[{'requestId':'item','operation':'ItemInventoryQuery','fields':{'ListID':inventory}}])['results'][0]['data'][0]
if not options.class_baseline:
    batch('change-defaults',[call('ItemInventoryMod',{'ListID':inventory,'EditSequence':leaf(fresh_item,'EditSequence'),
        'IncomeAccountRef':{'ListID':ids['Override']},'ApplyIncomeAccountRefToExistingTxns':False,
        'COGSAccountRef':{'ListID':ids['NewCOGS']},'ApplyCOGSAccountRefToExistingTxns':False},'item')],True,True)
    before_posts=postings('changed-default-postings')
else:
    before_posts=original_posts
def canonical(n,selected=()):
    line=leaf(n,'TxnLineID')
    return (n['name'],tuple(sorted(n['attributes'].items())),n.get('value'),tuple(canonical(c,selected) for c in n['children']
        if c['name'] not in ('TimeModified','EditSequence') and not(c['name']=='ClassRef' and line in selected)))
assert Counter(map(canonical,original_posts))==Counter(map(canonical,before_posts)), 'Changing defaults rewrote historical postings'
refreshed=forms('fresh-forms-after-defaults')
assert all(canonical(before[kind])==canonical(refreshed[kind]) for kind in TYPES)
before=refreshed
before_class_item=batch('before-class-item',[{'requestId':'item','operation':'ItemInventoryQuery','fields':{'ListID':inventory}}])['results'][0]['data'][0]
changes=[]; selected={}; join_counts={}
for kind in TYPES:
    record=before[kind]; rows=[r for r in before_posts if leaf(r,'TxnID')==txns[kind]]
    selected[kind]=set(); join_counts[kind]=[]
    def mod(line):
        line_id=leaf(line,'TxnLineID')
        matched=[r for r in rows if leaf(r,'TxnLineID')==line_id]
        join_counts[kind].append(len(matched))
        revenue=[r for r in matched if ref(r,'AccountRef') in (ids['Income'],ids['Override'])] if ref(line,'ItemRef')==inventory else matched
        assert len(revenue)==1, 'Fixture cannot uniquely identify retained revenue posting'
        assert abs(Decimal(leaf(revenue[0],'Amount')))==abs(Decimal(leaf(line,'Amount')))
        body={'TxnLineID':line_id,'OverrideItemAccountRef':{'ListID':ref(revenue[0],'AccountRef')}}
        cls=ref(line,'ClassRef')
        if cls is None and ref(revenue[0],'AccountRef')==ids['Income']:
            cls=ids['Target']; selected[kind].add(line_id)
        if cls:
            body['ClassRef']={'ListID':cls}
        return body
    choices=[]
    for n in record['children']:
        if n['name']==kind+'LineRet':
            choices.append({kind+'LineMod':mod(n)})
        elif n['name']==kind+'LineGroupRet':
            choices.append({kind+'LineGroupMod':{'TxnLineID':leaf(n,'TxnLineID'),kind+'LineMod':[mod(c) for c in n['children'] if c['name']==kind+'LineRet']}})
    assert len(selected[kind])==2
    changes.append(call(kind+'Mod',{'TxnID':txns[kind],'EditSequence':leaf(record,'EditSequence'),'$choices':choices},kind))
preview=batch('preview',changes)
assert not preview['submitted']
batch('apply',changes,True)
after=forms('after-forms')
after_posts=postings('after-postings')
after_class_item=batch('after-class-item',[{'requestId':'item','operation':'ItemInventoryQuery','fields':{'ListID':inventory}}])['results'][0]['data'][0]
assert canonical(before_class_item)==canonical(after_class_item), 'Item quantities, costs or definitions changed during class-only modification'
checks=[]
for kind in TYPES:
    assert canonical(before[kind],selected[kind])==canonical(after[kind],selected[kind]), 'Unexpected form change'
    old=[r for r in before_posts if leaf(r,'TxnID')==txns[kind]]
    new=[r for r in after_posts if leaf(r,'TxnID')==txns[kind]]
    assert Counter(map(canonical,old))==Counter(map(canonical,new)), 'Income/inventory/COGS postings changed'
    lines={leaf(n,'TxnLineID'):n for n in walk(after[kind]) if n['name']==kind+'LineRet'}
    assert all(ref(lines[line],'ClassRef')==ids['Target'] for line in selected[kind])
    checks.append({'transactionType':kind,'classesChanged':len(selected[kind]),'retainedLeafLines':len(lines),
        'postingRows':len(old),'maxPostingRowsSharingLeafIdentity':max(join_counts[kind]),
        'changedIncomeAndCogsDefaultsPreservedHistoricalPostings':None if options.class_baseline else True,
        'defaultAccountChangesPerformedThisRun':not options.class_baseline,'itemQuantitiesCostsAndDefinitionsPreserved':True,'incomeInventoryAndCogsPostingsPreserved':True,
        'flatAndGroupChildClassesVerified':True,'unselectedReturnedFormFieldsPreserved':True,
        'scope':'Fixture-specific inventory-item revenue-account selection against fresh current posting baseline, with quantity/cost, overrides and mixed service/inventory group. No generic inventory bulk planner, pre-default-change COGS preservation or COGS-account selection proof.'})
(DEST/f'lifecycle-{STAMP}-inventory-sales-verification.json').write_text(json.dumps(checks,indent=2),encoding='utf-8')
print(json.dumps(checks),flush=True)
