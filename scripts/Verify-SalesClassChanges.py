"""Independent retained-evidence verifier; no SDK calls or accounting changes."""
import argparse
import json
import pathlib
from collections import Counter

ROOT=pathlib.Path(__file__).resolve().parent.parent
DEST=ROOT/'artifacts/fixtures'
parser=argparse.ArgumentParser(description=__doc__)
parser.add_argument('stamp')
options=parser.parse_args()
assert options.stamp.isdigit() and len(options.stamp)==6
prefix=f'lifecycle-{options.stamp}-sales-class-'

def load(suffix):
    return json.loads((DEST/(prefix+suffix+'.json')).read_text(encoding='utf-8'))

def scalar(node,key):
    matches=[c for c in node['children'] if c['name']==key]
    assert len(matches)<=1
    return matches[0].get('value') if matches else None

def reference(node,key):
    matches=[c for c in node['children'] if c['name']==key]
    assert len(matches)<=1
    return scalar(matches[0],'ListID') if matches else None

def walk(node):
    yield node
    for c in node['children']:
        yield from walk(c)

def canonical(node,selected):
    line=scalar(node,'TxnLineID')
    return (node['name'],tuple(sorted(node['attributes'].items())),node.get('value'),tuple(
        canonical(c,selected) for c in node['children'] if c['name'] not in ('TimeModified','EditSequence')
        and not(c['name']=='ClassRef' and line in selected)))

before_result=load('before-forms-result')
after_result=load('after-forms-result')
applied=load('apply-result')
plan=load('apply-plan')
before_postings=load('before-postings')
after_postings=load('after-postings')
assert all(value['success'] for value in [before_result,after_result,applied,before_postings,after_postings])
assert len(plan['requests'])==3 and len(applied['results'])==3
assert all(r['outcome']=='succeeded' for r in applied['results'])
assert before_postings['complete'] and after_postings['complete']
checks=[]
for request in plan['requests']:
    kind=request['requestId']; body=request['fields'][kind+'Mod']
    before=next(r['data'][0] for r in before_result['results'] if r['requestId']==kind)
    after=next(r['data'][0] for r in after_result['results'] if r['requestId']==kind)
    assert body['TxnID']==scalar(before,'TxnID')==scalar(after,'TxnID')
    assert body['EditSequence']==scalar(before,'EditSequence')
    old_lines={scalar(n,'TxnLineID'):n for n in walk(before) if n['name']==kind+'LineRet'}
    new_lines={scalar(n,'TxnLineID'):n for n in walk(after) if n['name']==kind+'LineRet'}
    assert len(old_lines)==6 and old_lines.keys()==new_lines.keys()
    selected={line for line,n in old_lines.items() if reference(n,'ClassRef') is None and reference(new_lines[line],'ClassRef') is not None}
    assert len(selected)==2
    targets={reference(new_lines[line],'ClassRef') for line in selected}
    assert len(targets)==1
    target=targets.pop()
    assert canonical(before,selected)==canonical(after,selected)
    old_posts=[r for r in before_postings['data'] if scalar(r,'TxnID')==body['TxnID']]
    new_posts=[r for r in after_postings['data'] if scalar(r,'TxnID')==body['TxnID']]
    assert Counter(canonical(r,set()) for r in old_posts)==Counter(canonical(r,set()) for r in new_posts)
    def mods(value):
        if isinstance(value,dict):
            if 'TxnLineID' in value and 'OverrideItemAccountRef' in value:
                yield value
            for nested in value.values():
                yield from mods(nested)
        elif isinstance(value,list):
            for nested in value:
                yield from mods(nested)
    retained=list(mods(body))
    assert len(retained)==6 and {m['TxnLineID'] for m in retained}==old_lines.keys()
    posts_by_line={scalar(r,'TxnLineID'):r for r in old_posts}
    assert all(m['OverrideItemAccountRef']['ListID']==reference(posts_by_line[m['TxnLineID']],'AccountRef') for m in retained)
    assert all(m.get('ClassRef',{}).get('ListID')==target for m in retained if m['TxnLineID'] in selected)
    checks.append({'transactionType':kind,'changedClasses':len(selected),'retainedLeafLines':6,
        'actualPostingAccountsAndAmountsPreserved':True,'allUnselectedFormFieldsPreserved':True,
        'flatAndGroupChildClassesVerified':True,'freshEditSequenceUsed':True,
        'verificationSource':'Independent retained SDK form and posting reads after successful mutation; no replay.',
        'scope':'Service-item fixture with changed item default and explicit actual posting-account overrides for retained leaf lines. Not inventory/COGS or generic sales bulk support.'})
(DEST/(prefix+'verification.json')).write_text(json.dumps(checks,indent=2),encoding='utf-8')
print(json.dumps(checks),flush=True)
