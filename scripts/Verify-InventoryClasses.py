"""Independently verify retained inventory class evidence without SDK calls."""
import argparse
from collections import Counter
from decimal import Decimal
import json
import pathlib

ROOT = pathlib.Path(__file__).resolve().parent.parent
DEST = ROOT / 'artifacts/fixtures'
parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('fixture_stamp')
parser.add_argument('probe_stamp')
options = parser.parse_args()
assert all(len(s) == 6 and s.isdigit() for s in (options.fixture_stamp, options.probe_stamp))

def load(stamp, label):
    return json.loads((DEST / f'lifecycle-{stamp}-inventory-sales-{label}.json').read_text(encoding='utf-8'))

def scalar(node, name):
    matches = [c for c in node['children'] if c['name'] == name]
    assert len(matches) <= 1
    return matches[0].get('value') if matches else None

def reference(node, name):
    matches = [c for c in node['children'] if c['name'] == name]
    assert len(matches) <= 1
    return scalar(matches[0], 'ListID') if matches else None

def canonical(node, selected=()):
    return (node['name'], tuple(sorted(node['attributes'].items())), node.get('value'),
            tuple(canonical(c, selected) for c in node['children']
                  if c['name'] not in ('TimeModified', 'EditSequence')
                  and not (c['name'] == 'ClassRef' and scalar(node, 'TxnLineID') in selected)))

def successful(value):
    assert value['success']
    assert all(r['outcome'] == 'succeeded' for r in value.get('results', []))
    return value

base = successful(load(options.fixture_stamp, 'base-result'))
ids = {r['requestId']: scalar(r['data'][0], 'ListID') for r in base['results']}
before = successful(load(options.probe_stamp, 'fresh-forms-after-defaults-result'))
after = successful(load(options.probe_stamp, 'after-forms-result'))
before_forms = {r['requestId']: r['data'][0] for r in before['results']}
after_forms = {r['requestId']: r['data'][0] for r in after['results']}
old_posts = successful(load(options.probe_stamp, 'original-postings'))
new_posts = successful(load(options.probe_stamp, 'after-postings'))
assert old_posts['complete'] and new_posts['complete']
plan = load(options.probe_stamp, 'apply-plan')
applied = successful(load(options.probe_stamp, 'apply-result'))
assert len(plan['requests']) == len(applied['results']) == 3
# This verifier intentionally accepts only a class-only run, never the default-change run.
assert not (DEST / f'lifecycle-{options.probe_stamp}-inventory-sales-change-defaults-plan.json').exists()
old_item = successful(load(options.probe_stamp, 'before-class-item-result'))['results'][0]['data'][0]
new_item = successful(load(options.probe_stamp, 'after-class-item-result'))['results'][0]['data'][0]
assert canonical(old_item) == canonical(new_item)
checks = []
for request in plan['requests']:
    kind = request['requestId']
    assert kind in ('Invoice', 'SalesReceipt', 'CreditMemo') and request['operation'] == kind + 'Mod'
    body = request['fields'][kind + 'Mod']
    original, current = before_forms[kind], after_forms[kind]
    txn = scalar(original, 'TxnID')
    assert body['TxnID'] == txn == scalar(current, 'TxnID')
    assert body['EditSequence'] == scalar(original, 'EditSequence')
    assert set(body) == {'TxnID', 'EditSequence', '$choices'}
    leaves = {}; actual_choices = []
    for node in original['children']:
        if node['name'] == kind + 'LineRet':
            line = scalar(node, 'TxnLineID'); leaves[line] = node
            actual_choices.append(('leaf', line))
        elif node['name'] == kind + 'LineGroupRet':
            children = [c for c in node['children'] if c['name'] == kind + 'LineRet']
            child_ids = [scalar(c, 'TxnLineID') for c in children]
            leaves.update(zip(child_ids, children))
            actual_choices.append(('group', scalar(node, 'TxnLineID'), tuple(child_ids)))
    mods = {}; planned_choices = []
    for choice in body['$choices']:
        if kind + 'LineMod' in choice:
            mod = choice[kind + 'LineMod']; mods[mod['TxnLineID']] = mod
            planned_choices.append(('leaf', mod['TxnLineID']))
        else:
            group = choice[kind + 'LineGroupMod']; children = group[kind + 'LineMod']
            mods.update((c['TxnLineID'], c) for c in children)
            planned_choices.append(('group', group['TxnLineID'], tuple(c['TxnLineID'] for c in children)))
    assert planned_choices == actual_choices and set(mods) == set(leaves) and len(leaves) == 4
    old = [p for p in old_posts['data'] if scalar(p, 'TxnID') == txn and scalar(p, 'TxnType') == kind]
    new = [p for p in new_posts['data'] if scalar(p, 'TxnID') == txn and scalar(p, 'TxnType') == kind]
    assert len(old) == len(new) == 12 and Counter(map(canonical, old)) == Counter(map(canonical, new))
    selected = set()
    for line, mod in mods.items():
        assert set(mod) <= {'TxnLineID', 'OverrideItemAccountRef', 'ClassRef'}
        account = mod['OverrideItemAccountRef']['ListID']
        matched = [p for p in old if scalar(p, 'TxnLineID') == line and reference(p, 'AccountRef') == account]
        assert len(matched) == 1
        assert abs(Decimal(scalar(matched[0], 'Amount'))) == abs(Decimal(scalar(leaves[line], 'Amount')))
        prior_class = reference(leaves[line], 'ClassRef')
        planned_class = mod.get('ClassRef', {}).get('ListID')
        if prior_class != planned_class:
            assert prior_class is None and planned_class == ids['Target'] and account == ids['Income']
            selected.add(line)
    assert len(selected) == 2 and canonical(original, selected) == canonical(current, selected)
    def walk(node):
        yield node
        for child in node['children']: yield from walk(child)
    returned = {scalar(n, 'TxnLineID'): n for n in walk(current) if n['name'] == kind + 'LineRet'}
    assert all(reference(returned[line], 'ClassRef') == ids['Target'] for line in selected)
    checks.append({'transactionType': kind, 'classesChanged': 2, 'retainedLeafLines': 4, 'postingRows': 12,
                   'orderedFlatAndGroupIdentitiesPreserved': True, 'actualRevenueAccountOverridesVerified': True,
                   'allReturnedPostingFieldsPreserved': True, 'unselectedReturnedFormFieldsPreserved': True,
                   'itemQuantitiesCostsAndDefinitionsPreserved': True, 'defaultAccountChangesPerformedThisRun': False,
                   'scope': 'Independent retained-evidence verification of inventory revenue-account class selection on Test.QBW current baseline. No pre-default-change COGS preservation, generic inventory bulk planner, COGS-account class-selection or independent ledger-report proof.'})
(DEST / f'lifecycle-{options.probe_stamp}-inventory-class-verification.json').write_text(json.dumps(checks, indent=2), encoding='utf-8')
print(json.dumps(checks))
