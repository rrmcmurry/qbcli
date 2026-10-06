"""TxnID/TxnLineID macro clearing and independent ledger read-back in Test.QBW."""
import datetime
import json
import pathlib
import subprocess
from decimal import Decimal

ROOT = pathlib.Path(__file__).resolve().parent.parent
COMPANY = (ROOT / 'QBData/Test.QBW').resolve()
assert COMPANY == pathlib.Path(__import__("os").environ["QBCLI_LIVE_TEST_COMPANY"]).resolve(strict=True)
EXE = ROOT / 'artifacts/win-x64/qbcli.exe'
DEST = ROOT / 'artifacts/fixtures'
STAMP = datetime.datetime.now().strftime('%H%M%S')

def leaf(node, key):
    return next((c['value'] for c in node['children'] if c['name'] == key), None)

def child(node, key):
    return next(c for c in node['children'] if c['name'] == key)

def walk(node):
    yield node
    for c in node['children']:
        yield from walk(c)

def macro(tag, name):
    return {'$value': '', '$attributes': {'useMacro': tag + ':' + name}}

def call(operation, fields, request_id):
    return {'requestId': request_id, 'operation': operation, 'fields': fields}

def run(label, requests, batch_size=50, apply=False):
    path = DEST / f'lifecycle-{STAMP}-line-macro-{label}-plan.json'
    path.write_text(json.dumps({'protocolVersion': '13.0', 'country': 'US', 'onError': 'stopOnError', 'requests': requests}), encoding='utf-8')
    args = [str(EXE), 'sdk', 'batch', '--input', str(path), '--batch-size', str(batch_size), '--company-file', str(COMPANY)]
    if apply:
        args.append('--apply')
    proc = subprocess.run(args, capture_output=True, text=True, encoding='utf-8')
    path.with_name(path.name.replace('-plan', '-result')).write_text(proc.stdout, encoding='utf-8')
    value = json.loads(proc.stdout)
    print(json.dumps({'phase': label, 'success': value.get('success'), 'outcomes': [
        {k: r.get(k) for k in ('operation', 'outcome', 'statusCode')} for r in value.get('results', [])]}), flush=True)
    assert value['success'], value
    return value

def ledger_fields(account):
    return {'GeneralDetailReportType': 'GeneralLedger', 'DisplayReport': False,
        'ReportPeriod': {'FromReportDate': '2026-10-05', 'ToReportDate': '2026-10-05'},
        'ReportAccountFilter': {'ListID': account}, 'ReportTxnTypeFilter': {'TxnTypeFilter': 'Deposit'},
        'IncludeColumn': ['TxnID', 'ClearedStatus', 'Amount'], 'ReportBasis': 'Accrual'}

def ledger_row(report, txn_id):
    columns = {n['attributes']['colID']: leaf(n, 'ColType') for n in walk(report) if n['name'] == 'ColDesc'}
    rows = []
    for node in walk(report):
        if node['name'] != 'DataRow':
            continue
        values = {columns[c['attributes']['colID']]: c['attributes'].get('value', '')
            for c in node['children'] if c['name'] == 'ColData'}
        if values.get('TxnID') == txn_id:
            rows.append(values)
    assert len(rows) == 1, rows
    return rows[0]

checks = []
for mode, bound in [('same_message', 50), ('split_messages', 1)]:
    prefix = 'LINE' + STAMP + ('A' if bound == 50 else 'B')
    base = run(mode + '-base', [call('AccountAdd', {'AccountAdd': {'Name': prefix + suffix, 'AccountType': 'Bank'}}, suffix)
        for suffix in [' Deposit', ' CashBack']], apply=True)
    bank, cashback_bank = [leaf(r['data'][0], 'ListID') for r in base['results']]
    txn_macro, line_macro = prefix + 'Txn', prefix + 'Cash'
    created = run(mode + '-create-clear', [
        call('DepositAdd', {'DepositAdd': {'$attributes': {'defMacro': 'TxnID:' + txn_macro},
            'TxnDate': '2026-10-05', 'DepositToAccountRef': {'ListID': bank}, 'Memo': 'TxnLineID macro lifecycle',
            'CashBackInfoAdd': {'$attributes': {'defMacro': 'TxnLineID:' + line_macro},
                'AccountRef': {'ListID': cashback_bank}, 'Amount': '5.00'},
            'DepositLineAdd': {'AccountRef': {'FullName': 'SDK154955 Income'}, 'Amount': '20.00'}}}, 'deposit'),
        call('ClearedStatusMod', {'ClearedStatusMod': {'TxnID': macro('TxnID', txn_macro), 'ClearedStatus': 'Cleared'}}, 'clear-deposit'),
        call('ClearedStatusMod', {'ClearedStatusMod': {'TxnID': macro('TxnID', txn_macro),
            'TxnLineID': macro('TxnLineID', line_macro), 'ClearedStatus': 'Cleared'}}, 'clear-cashback'),
    ], bound, True)
    assert len(created['messageSetResponses']) == (1 if bound == 50 else 3)
    original = created['results'][0]['data'][0]
    txn_id = leaf(original, 'TxnID')
    read = run(mode + '-verify', [
        call('DepositQuery', {'TxnID': txn_id, 'IncludeLineItems': True}, 'deposit'),
        call('GeneralDetailReportQuery', ledger_fields(bank), 'bank'),
        call('GeneralDetailReportQuery', ledger_fields(cashback_bank), 'cashback'),
    ])
    current = read['results'][0]['data'][0]
    cashback = child(current, 'CashBackInfoRet')
    assert leaf(current, 'DepositTotal') == '15.00' and leaf(cashback, 'Amount') == '5.00'
    assert leaf(child(current, 'DepositToAccountRef'), 'ListID') == bank
    assert leaf(child(cashback, 'AccountRef'), 'ListID') == cashback_bank
    rows = [ledger_row(r['data'][0], txn_id) for r in read['results'][1:]]
    assert [r.get('ClearedStatus', '') for r in rows] == ['Cleared', 'Cleared'], rows
    assert [abs(Decimal(r['Amount'])) for r in rows] == [Decimal('15'), Decimal('5')], rows
    line_id = leaf(cashback, 'TxnLineID')
    assert line_id
    run(mode + '-unclear-cashback', [call('ClearedStatusMod', {'ClearedStatusMod': {
        'TxnID': txn_id, 'TxnLineID': line_id, 'ClearedStatus': 'NotCleared'}}, 'unclear')], apply=True)
    after = run(mode + '-verify-isolation', [
        call('GeneralDetailReportQuery', ledger_fields(bank), 'bank'),
        call('GeneralDetailReportQuery', ledger_fields(cashback_bank), 'cashback'),
    ])
    after_rows = [ledger_row(r['data'][0], txn_id) for r in after['results']]
    assert [r.get('ClearedStatus', '') for r in after_rows] == ['Cleared', 'NotCleared'], after_rows
    assert [r['Amount'] for r in after_rows] == [r['Amount'] for r in rows]
    checks.append({'mode': mode, 'messageSetCount': len(created['messageSetResponses']),
        'transactionAndLineMacrosAccepted': True, 'bothPostingStatusesReadBackViaLedger': True,
        'cashBackStatusChangedIndependently': True, 'postingAmountsAndAccountIdentitiesVerified': True,
        'scope': 'One deposit and cash-back line per mode, synthetic Test.QBW. Other line macro elements and report layouts are not exhaustively verified.'})
(DEST / f'lifecycle-{STAMP}-line-macro-verification.json').write_text(json.dumps(checks, indent=2), encoding='utf-8')
print(json.dumps(checks), flush=True)
