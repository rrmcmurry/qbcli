"""Sales posting/line identity proof, only in expendable Test.QBW.

The SDK transaction query supplies actual posting accounts. Item settings alone
cannot capture per-line overrides or historical account mappings.
"""
import datetime
import argparse
import json
import pathlib
import subprocess
from collections import Counter

ROOT = pathlib.Path(__file__).resolve().parent.parent
COMPANY = (ROOT / 'QBData/Test.QBW').resolve()
assert COMPANY == pathlib.Path(__import__("os").environ["QBCLI_LIVE_TEST_COMPANY"]).resolve(strict=True)
EXE = ROOT / 'artifacts/win-x64/qbcli.exe'
DEST = ROOT / 'artifacts/fixtures'
STAMP = datetime.datetime.now().strftime('%H%M%S')
PREFIX = 'SALEPOST' + STAMP
TYPES = ['Invoice', 'SalesReceipt', 'CreditMemo']
parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--executable', type=pathlib.Path, default=EXE,
    help='CLI executable; use the0.8.5 build or newer for the optional file-mode test.')
parser.add_argument('--test-item-account-change', action='store_true',
    help='Also request single-user mode to test changing the item default; an already-open multi-user company cannot be switched by the SDK.')
OPTIONS = parser.parse_args()
EXE = OPTIONS.executable.resolve()

def leaf(node, key):
    return next((c['value'] for c in node['children'] if c['name'] == key), None)

def ref_id(node, key):
    value = next((c for c in node['children'] if c['name'] == key), None)
    return leaf(value, 'ListID') if value else None

def walk(node):
    yield node
    for c in node['children']:
        yield from walk(c)

def call(operation, body, identity):
    return {'requestId': identity, 'operation': operation, 'fields': {operation: body}}

def run(label, requests, apply=False, file_mode='any'):
    path = DEST / f'lifecycle-{STAMP}-sales-posting-{label}-plan.json'
    path.write_text(json.dumps({'protocolVersion': '13.0', 'country': 'US', 'onError': 'stopOnError', 'requests': requests}), encoding='utf-8')
    args = [str(EXE), 'sdk', 'batch', '--input', str(path), '--company-file', str(COMPANY), '--batch-size', '100']
    if file_mode != 'any':
        args.extend(['--file-mode', file_mode])
    if apply:
        args.append('--apply')
    proc = subprocess.run(args, capture_output=True, text=True, encoding='utf-8')
    path.with_name(path.name.replace('-plan', '-result')).write_text(proc.stdout, encoding='utf-8')
    result = json.loads(proc.stdout)
    print(json.dumps({'phase': label, 'success': result.get('success'), 'outcomes': [
        {k: r.get(k) for k in ('operation', 'outcome', 'statusCode')} for r in result.get('results', [])]}), flush=True)
    assert result['success'], result
    return result

base = run('base', [
    call('AccountAdd', {'Name': PREFIX + ' Original', 'AccountType': 'Income'}, 'original'),
    call('AccountAdd', {'Name': PREFIX + ' Override', 'AccountType': 'Income'}, 'override'),
    call('ClassAdd', {'Name': PREFIX + ' Target'}, 'class'),
    call('ClassAdd', {'Name': PREFIX + ' Existing'}, 'existing'),
    call('ItemServiceAdd', {'Name': PREFIX + ' Service', 'SalesOrPurchase': {'Price': '3.00',
        'AccountRef': {'FullName': PREFIX + ' Original'}}}, 'item'),
], True)
ids = {r['requestId']: leaf(r['data'][0], 'ListID') for r in base['results']}
group = run('group', [call('ItemGroupAdd', {'Name': PREFIX + ' Group', 'IsPrintItemsInGroup': True,
    'ItemGroupLine': [{'ItemRef': {'ListID': ids['item']}, 'Quantity': '1'},
        {'ItemRef': {'FullName': 'SDK154955 Service'}, 'Quantity': '1'}]}, 'group')], True)
group_id = leaf(group['results'][0]['data'][0], 'ListID')
requests = []
for kind in TYPES:
    lines = [
        {'ItemRef': {'ListID': ids['item']}, 'Amount': '11.00', 'Desc': 'default account blank class'},
        {'ItemRef': {'ListID': ids['item']}, 'Amount': '12.00', 'Desc': 'override account blank class', 'OverrideItemAccountRef': {'ListID': ids['override']}},
        {'ItemRef': {'ListID': ids['item']}, 'Amount': '13.00', 'Desc': 'default account existing class', 'ClassRef': {'ListID': ids['existing']}},
        {'ItemRef': {'ListID': ids['item']}, 'Amount': '14.00', 'Desc': 'override account other line', 'OverrideItemAccountRef': {'ListID': ids['override']}},
    ]
    body = {'CustomerRef': {'FullName': 'SDK154955 Customer'}, 'TxnDate': '2026-10-05', 'Memo': PREFIX + ' ' + kind,
        kind + 'LineAdd': lines, kind + 'LineGroupAdd': {'ItemGroupRef': {'ListID': group_id}, 'Quantity': '1'}}
    if kind == 'SalesReceipt':
        body['DepositToAccountRef'] = {'FullName': 'SDK154955 Bank'}
    else:
        body['ARAccountRef'] = {'FullName': 'SDK154955 AR'}
    requests.append(call(kind + 'Add', body, kind))
created = run('seed', requests, True)
txn_ids = {r['requestId']: leaf(r['data'][0], 'TxnID') for r in created['results']}

def postings(label):
    path = DEST / f'lifecycle-{STAMP}-sales-posting-{label}-fields.json'
    path.write_text(json.dumps({'TransactionAccountFilter': {'ListID': [ids['original'], ids['override']]},
        'TransactionTypeFilter': {'TxnTypeFilter': TYPES}, 'TransactionDetailLevelFilter': 'AllExceptSummary'}), encoding='utf-8')
    proc = subprocess.run([str(EXE), 'sdk', 'run', 'TransactionQuery', '--input', str(path), '--all', '--page-size', '1',
        '--company-file', str(COMPANY)], capture_output=True, text=True, encoding='utf-8')
    (DEST / f'lifecycle-{STAMP}-sales-posting-{label}-query.json').write_text(proc.stdout, encoding='utf-8')
    result = json.loads(proc.stdout)
    assert result['success'] and result['complete'], result
    print(json.dumps({'phase': label, 'records': len(result['data']), 'pages': result['pages']}), flush=True)
    return result['data']

before = postings('before-item-change')
read = run('forms', [{'requestId': kind, 'operation': kind + 'Query', 'fields': {
    'TxnID': txn_ids[kind], 'IncludeLineItems': True}} for kind in TYPES])
checks = []
for result in read['results']:
    kind = result['requestId']
    record = result['data'][0]
    lines = {leaf(n, 'TxnLineID'): n for n in walk(record) if n['name'] == kind + 'LineRet'}
    rows = [r for r in before if leaf(r, 'TxnID') == txn_ids[kind]]
    assert len(rows) == 5, [(leaf(r, 'TxnLineID'), ref_id(r, 'AccountRef')) for r in rows]
    assert all(leaf(r, 'TxnLineID') in lines for r in rows)
    assert Counter(ref_id(r, 'AccountRef') for r in rows) == Counter({ids['original']: 3, ids['override']: 2})
    for row in rows:
        line = lines[leaf(row, 'TxnLineID')]
        assert leaf(row, 'Amount').lstrip('-') == leaf(line, 'Amount').lstrip('-')
    checks.append({'transactionType': kind, 'postingLinesMatched': 5, 'flatAndGroupChildIdentitiesMatched': True,
        'actualOverrideAccountsVerified': True, 'postingAmountsMatchFormLines': True})

for check in checks:
    check['historicalPostingsPreservedAfterItemDefaultChange'] = None
    check['scope'] = 'Service-item sales fixture with per-line overrides and a group in Test.QBW. Inventory/COGS and other item types remain unverified.'
verification_path = DEST / f'lifecycle-{STAMP}-sales-posting-verification.json'
verification_path.write_text(json.dumps(checks, indent=2), encoding='utf-8')
if not OPTIONS.test_item_account_change:
    print(json.dumps(checks), flush=True)
    raise SystemExit(0)

item = base['results'][-1]['data'][0]
run('change-item-default', [call('ItemServiceMod', {'ListID': ids['item'], 'EditSequence': leaf(item, 'EditSequence'),
    'SalesOrPurchaseMod': {'AccountRef': {'ListID': ids['override']}, 'ApplyAccountRefToExistingTxns': False}}, 'item')], True, 'single-user')
after = postings('after-item-change')
canonical = lambda rows: Counter((leaf(r, 'TxnType'), leaf(r, 'TxnID'), leaf(r, 'TxnLineID'), ref_id(r, 'AccountRef'), leaf(r, 'Amount')) for r in rows)
assert canonical(before) == canonical(after)
for check in checks:
    check['historicalPostingsPreservedAfterItemDefaultChange'] = True
    check['scope'] = 'Service-item sales fixture with per-line overrides and a group in Test.QBW. Inventory/COGS and other item types remain unverified.'
(DEST / f'lifecycle-{STAMP}-sales-posting-verification.json').write_text(json.dumps(checks, indent=2), encoding='utf-8')
print(json.dumps(checks), flush=True)
