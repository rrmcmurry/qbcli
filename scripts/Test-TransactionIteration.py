"""Read-only TransactionQuery composite identity and projection checks in Test.QBW.

Uses the isolated two-bank deposit fixture created by Test-LineMacros.py.
"""
import datetime
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

def leaf(record, key):
    return next((c['value'] for c in record['children'] if c['name'] == key), None)

base = json.loads(sorted(DEST.glob('lifecycle-*-line-macro-same_message-base-result.json'))[-1].read_text(encoding='utf-8'))
account_ids = [leaf(r['data'][0], 'ListID') for r in base['results']]
fields = {'TransactionAccountFilter': {'ListID': account_ids},
    'TransactionTypeFilter': {'TxnTypeFilter': 'Deposit'}, 'TransactionDetailLevelFilter': 'All'}

def run(label, input_fields, all_records=False):
    path = DEST / f'lifecycle-{STAMP}-transaction-iteration-{label}-fields.json'
    path.write_text(json.dumps(input_fields), encoding='utf-8')
    args = [str(EXE), 'sdk', 'run', 'TransactionQuery', '--input', str(path), '--company-file', str(COMPANY)]
    if all_records:
        args += ['--all', '--page-size', '1']
    proc = subprocess.run(args, capture_output=True, text=True, encoding='utf-8')
    value = json.loads(proc.stdout)
    (DEST / f'lifecycle-{STAMP}-transaction-iteration-{label}-result.json').write_text(proc.stdout, encoding='utf-8')
    assert value['success'], value
    print(json.dumps({'phase': label, 'success': True, 'pages': value.get('pages')}), flush=True)
    return value['data'] if all_records else value['results'][0]['data']

direct = run('direct', fields)
paged = run('paged', fields, True)
assert len(direct) == len(paged) == 2
canonical = lambda records: Counter(json.dumps(r, sort_keys=True) for r in records)
assert canonical(direct) == canonical(paged)
assert len({leaf(r, 'TxnID') for r in paged}) == 1
assert len({leaf(r, 'TxnLineID') for r in paged}) == 2
amounts = run('amount-only', {**fields, 'IncludeRetElement': 'Amount'}, True)
assert len(amounts) == 2
assert all([c['name'] for c in r['children']] == ['Amount'] for r in amounts)
assert Counter(leaf(r, 'Amount') for r in amounts) == Counter(leaf(r, 'Amount') for r in paged)
projected = run('id-and-amount', {**fields, 'IncludeRetElement': ['TxnID', 'Amount']}, True)
assert len(projected) == 2 and all({c['name'] for c in r['children']} == {'TxnID', 'Amount'} for r in projected)
assert Counter(leaf(r, 'Amount') for r in projected) == Counter(leaf(r, 'Amount') for r in paged)
metadata = {'operation': 'TransactionQuery', 'records': 2, 'pages': 2, 'distinctTransactionIds': 1,
    'summaryAndPostingLineRetained': True, 'pagedRecordsMatchUnboundedQuery': True,
    'amountOnlyProjectionVerified': True, 'callerRequestedTransactionIdPreserved': True,
    'scope': 'Read-only isolated deposit fixture in Test.QBW, page size1. No universal transaction-type/filter/permission claim.'}
(DEST / f'lifecycle-{STAMP}-transaction-iteration-verification.json').write_text(json.dumps(metadata, indent=2), encoding='utf-8')
print(json.dumps(metadata), flush=True)
