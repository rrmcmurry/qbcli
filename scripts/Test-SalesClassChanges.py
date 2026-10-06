"""Sales class-modification preservation proof on designated expendable Test.QBW.

Uses the newest successful service-item sales fixture whose item default was
changed. Full retained line/group identities and actual posting-account overrides
are submitted; no generic item-account inference or accounting restoration.
"""
import datetime
import json
import pathlib
import subprocess
from collections import Counter

ROOT=pathlib.Path(__file__).resolve().parent.parent
COMPANY=(ROOT/'QBData/Test.QBW').resolve()
assert COMPANY==pathlib.Path(__import__("os").environ["QBCLI_LIVE_TEST_COMPANY"]).resolve(strict=True)
EXE=ROOT/'artifacts/win-x64/qbcli.exe'
DEST=ROOT/'artifacts/fixtures'
STAMP=datetime.datetime.now().strftime('%H%M%S')
TYPES=['Invoice','SalesReceipt','CreditMemo']
source=max(DEST.glob('lifecycle-*-sales-posting-verification.json'),key=lambda p:p.stat().st_mtime)
proof=json.loads(source.read_text(encoding='utf-8'))
assert len(proof)==3 and all(c['historicalPostingsPreservedAfterItemDefaultChange'] is True for c in proof)
SOURCE_STAMP=source.name.split('-')[1]
base=json.loads((DEST/f'lifecycle-{SOURCE_STAMP}-sales-posting-base-result.json').read_text(encoding='utf-8'))
seed=json.loads((DEST/f'lifecycle-{SOURCE_STAMP}-sales-posting-seed-result.json').read_text(encoding='utf-8'))

def child(node,key):
    values=[c for c in node['children'] if c['name']==key]
    assert len(values)<=1
    return values[0] if values else None

def leaf(node,key):
    c=child(node,key)
    return c.get('value') if c else None

def ref(node,key):
    c=child(node,key)
    return leaf(c,'ListID') if c else None

def walk(node):
    yield node
    for c in node['children']:
        yield from walk(c)

ids={r['requestId']:leaf(r['data'][0],'ListID') for r in base['results']}
txns={r['requestId']:leaf(r['data'][0],'TxnID') for r in seed['results']}

def run(label,args):
    proc=subprocess.run([str(EXE),*args,'--company-file',str(COMPANY),'--json'],capture_output=True,text=True,encoding='utf-8')
    (DEST/f'lifecycle-{STAMP}-sales-class-{label}.json').write_text(proc.stdout,encoding='utf-8')
    result=json.loads(proc.stdout)
    assert proc.returncode==0 and result['success'], 'SDK request failed; retain journal, do not replay'
    assert pathlib.Path(result['companyFile']).resolve()==COMPANY
    return result

def batch(label,requests,apply=False):
    path=DEST/f'lifecycle-{STAMP}-sales-class-{label}-plan.json'
    path.write_text(json.dumps({'protocolVersion':'13.0','country':'US','onError':'stopOnError','requests':requests}),encoding='utf-8')
    return run(label+'-result',['sdk','batch','--input',str(path),*(['--apply'] if apply else [])])

def forms(label):
    result=batch(label,[{'requestId':kind,'operation':kind+'Query','fields':{'TxnID':txns[kind],'IncludeLineItems':True,
        **({'IncludeLinkedTxns':True} if kind!='SalesReceipt' else {})}} for kind in TYPES])
    return {r['requestId']:r['data'][0] for r in result['results']}

def postings(label):
    path=DEST/f'lifecycle-{STAMP}-sales-class-{label}-fields.json'
    path.write_text(json.dumps({'TransactionTypeFilter':{'TxnTypeFilter':TYPES},'TransactionDetailLevelFilter':'AllExceptSummary'}),encoding='utf-8')
    result=run(label,['sdk','run','TransactionQuery','--input',str(path),'--all','--page-size','500'])
    assert result['complete']
    return [r for r in result['data'] if leaf(r,'TxnID') in txns.values()]

before=forms('before-forms')
posted=postings('before-postings')
selected={}
requests=[]
for kind in TYPES:
    record=before[kind]
    by_line={}
    for row in posted:
        if leaf(row,'TxnID')==txns[kind]:
            line_id=leaf(row,'TxnLineID')
            assert line_id not in by_line, 'Multiple postings for one line are not supported by this service fixture recipe'
            by_line[line_id]=row
    selected[kind]=set()
    def line_mod(line):
        line_id=leaf(line,'TxnLineID')
        assert line_id in by_line
        posting=by_line[line_id]
        assert ref(posting,'AccountRef')
        body={'TxnLineID':line_id,'OverrideItemAccountRef':{'ListID':ref(posting,'AccountRef')}}
        existing=ref(line,'ClassRef')
        if ref(posting,'AccountRef')==ids['original'] and existing is None:
            selected[kind].add(line_id)
            body['ClassRef']={'ListID':ids['class']}
        elif existing:
            body['ClassRef']={'ListID':existing}
        return body
    choices=[]
    for line in record['children']:
        if line['name']==kind+'LineRet':
            choices.append({kind+'LineMod':line_mod(line)})
        elif line['name']==kind+'LineGroupRet':
            choices.append({kind+'LineGroupMod':{'TxnLineID':leaf(line,'TxnLineID'),kind+'LineMod':[
                line_mod(c) for c in line['children'] if c['name']==kind+'LineRet']}})
    assert len(selected[kind])==2, 'Fixture already changed or no longer has the intended blank classes'
    body={'TxnID':txns[kind],'EditSequence':leaf(record,'EditSequence'),'$choices':choices}
    requests.append({'requestId':kind,'operation':kind+'Mod','fields':{kind+'Mod':body}})

# Validate without a connection first; apply creates a new durable write journal.
preview=batch('preview',requests)
assert not preview['submitted']
changed=batch('apply',requests,True)
assert len(changed['results'])==3
after=forms('after-forms')
after_postings=postings('after-postings')

def canonical(node,ignored_classes):
    own=leaf(node,'TxnLineID')
    return (node['name'],tuple(sorted(node['attributes'].items())),node.get('value'),tuple(
        canonical(c,ignored_classes) for c in node['children'] if c['name'] not in ('EditSequence','TimeModified')
        and not(c['name']=='ClassRef' and own in ignored_classes)))

checks=[]
for kind in TYPES:
    assert canonical(before[kind],selected[kind])==canonical(after[kind],selected[kind]), 'Unexpected form change beyond selected classes'
    old_rows=[r for r in posted if leaf(r,'TxnID')==txns[kind]]
    new_rows=[r for r in after_postings if leaf(r,'TxnID')==txns[kind]]
    assert Counter(canonical(r,selected[kind]) for r in old_rows)==Counter(canonical(r,selected[kind]) for r in new_rows), 'Unexpected accounting posting change'
    # TransactionQuery posting rows return accounts/amounts, not ClassRef.
    by_id={leaf(r,'TxnLineID'):r for r in walk(after[kind]) if r['name']==kind+'LineRet'}
    assert all(ref(by_id[line_id],'ClassRef')==ids['class'] for line_id in selected[kind])
    checks.append({'transactionType':kind,'changedClasses':len(selected[kind]),'actualPostingAccountsAndAmountsPreserved':True,
        'allUnselectedFormFieldsPreserved':True,'flatAndGroupChildClassesVerified':True,'freshEditSequenceUsed':True,
        'scope':'Service-item fixture with changed item default and explicit actual posting-account overrides for retained leaf lines. Not inventory/COGS or generic sales bulk support.'})
(DEST/f'lifecycle-{STAMP}-sales-class-verification.json').write_text(json.dumps(checks,indent=2),encoding='utf-8')
print(json.dumps(checks),flush=True)
