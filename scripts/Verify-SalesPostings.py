"""Verify retained sales fixtures offline; never connects to QuickBooks.

Usage: python Verify-SalesPostings.py 191618
Only metadata is exported. The optional later item-account-change phase is not
inferred from an earlier successful posting query.
"""
import argparse
import json
import pathlib
from collections import Counter

ROOT = pathlib.Path(__file__).resolve().parent.parent
DEST = ROOT / 'artifacts/fixtures'

def leaf(node, key):
    return next((c.get('value') for c in node['children'] if c['name'] == key), None)

def ref_id(node, key):
    value = next((c for c in node['children'] if c['name'] == key), None)
    return leaf(value, 'ListID') if value else None

def walk(node):
    yield node
    for child in node['children']:
        yield from walk(child)

def verify(stamp):
    def read(suffix):
        value = json.loads((DEST / f'lifecycle-{stamp}-sales-posting-{suffix}.json').read_text(encoding='utf-8-sig'))
        assert value['success'], 'Fixture request did not succeed'
        return value
    base = read('base-result')
    ids = {r['requestId']: leaf(r['data'][0], 'ListID') for r in base['results']}
    created = read('seed-result')
    txn_ids = {r['requestId']: leaf(r['data'][0], 'TxnID') for r in created['results']}
    before = read('before-item-change-query')
    assert before['complete'] and len(before['data']) == 15 and before['pages'] == 15
    forms = read('forms-result')
    checks = []
    for result in forms['results']:
        kind = result['requestId']
        assert kind in ('Invoice', 'SalesReceipt', 'CreditMemo')
        lines = {leaf(n, 'TxnLineID'): n for n in walk(result['data'][0]) if n['name'] == kind + 'LineRet'}
        rows = [r for r in before['data'] if leaf(r, 'TxnID') == txn_ids[kind]]
        assert len(rows) == 5 and all(leaf(r, 'TxnType') == kind for r in rows)
        assert all(leaf(r, 'TxnLineID') in lines for r in rows)
        assert Counter(ref_id(r, 'AccountRef') for r in rows) == Counter({ids['original']: 3, ids['override']: 2})
        for row in rows:
            assert leaf(row, 'Amount').lstrip('-') == leaf(lines[leaf(row, 'TxnLineID')], 'Amount').lstrip('-')
        checks.append({'transactionType': kind, 'postingLinesMatched': 5, 'flatAndGroupChildIdentitiesMatched': True,
            'actualOverrideAccountsVerified': True, 'postingAmountsMatchFormLines': True,
            'historicalPostingsPreservedAfterItemDefaultChange': None,
            'scope': 'Service-item sales fixture with per-line overrides and a group in Test.QBW. Inventory/COGS and other item types remain unverified.'})
    assert len(checks) == 3 and len({c['transactionType'] for c in checks}) == 3
    # Later phase must have its own successful mutation result and fresh query.
    changed = DEST / f'lifecycle-{stamp}-sales-posting-change-item-default-result.json'
    after_path = DEST / f'lifecycle-{stamp}-sales-posting-after-item-change-query.json'
    if changed.exists() and after_path.exists():
        change_result = json.loads(changed.read_text(encoding='utf-8-sig'))
        if change_result.get('success'):
            after = read('after-item-change-query')
            assert after['complete']
            def canonical(rows):
                return Counter((leaf(r, 'TxnType'), leaf(r, 'TxnID'), leaf(r, 'TxnLineID'), ref_id(r, 'AccountRef'), leaf(r, 'Amount')) for r in rows)
            assert canonical(before['data']) == canonical(after['data'])
            for check in checks:
                check['historicalPostingsPreservedAfterItemDefaultChange'] = True
    output = DEST / f'lifecycle-{stamp}-sales-posting-verification.json'
    output.write_text(json.dumps(checks, indent=2), encoding='utf-8')
    return checks

if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('stamp')
    args = parser.parse_args()
    if len(args.stamp) != 6 or not args.stamp.isascii() or not args.stamp.isdigit():
        parser.error('stamp must contain exactly six ASCII digits')
    print(json.dumps(verify(args.stamp)))
