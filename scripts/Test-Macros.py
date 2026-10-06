"""Linked SDK macro requests, only in the explicitly expendable Test.QBW."""
import datetime
import json
import pathlib
import subprocess

ROOT = pathlib.Path(__file__).resolve().parent.parent
COMPANY = (ROOT / 'QBData/Test.QBW').resolve()
assert COMPANY == pathlib.Path(__import__("os").environ["QBCLI_LIVE_TEST_COMPANY"]).resolve(strict=True)
EXE = ROOT / 'artifacts/win-x64/qbcli.exe'
DEST = ROOT / 'artifacts/fixtures'
STAMP = datetime.datetime.now().strftime('%H%M%S')

def ref(name):
    return {'FullName': 'SDK154955 ' + name}

def macro(name):
    return {'$value': '', '$attributes': {'useMacro': 'TxnID:' + name}}

def leaf(node, key):
    return next((c['value'] for c in node['children'] if c['name'] == key), None)

def run(label, requests, batch_size=50, apply=False, expected_success=True):
    path = DEST / f'lifecycle-{STAMP}-macro-{label}-plan.json'
    path.write_text(json.dumps({'protocolVersion': '13.0', 'country': 'US',
        'onError': 'stopOnError', 'requests': requests}), encoding='utf-8')
    args = [str(EXE), 'sdk', 'batch', '--input', str(path), '--batch-size', str(batch_size),
        '--company-file', str(COMPANY)]
    if apply:
        args.append('--apply')
    proc = subprocess.run(args, capture_output=True, text=True, encoding='utf-8')
    path.with_name(path.name.replace('-plan', '-result')).write_text(proc.stdout, encoding='utf-8')
    result = json.loads(proc.stdout)
    print(json.dumps({'phase': label, 'success': result.get('success'), 'outcomes': [
        {k: r.get(k) for k in ('operation', 'outcome', 'statusCode')} for r in result.get('results', [])]}), flush=True)
    assert result['success'] == expected_success, result
    return result

checks = []
for mode, bound in [('same_message', 50), ('split_messages', 1), ('forty_character_names', 50), ('resumed_chain', 1)]:
    invoice_name, payment_name = STAMP + mode + 'I', STAMP + mode + 'P'
    if mode == 'forty_character_names':
        invoice_name, payment_name = 'I' + STAMP + 'x' * 33, 'P' + STAMP + 'x' * 33
        assert len(invoice_name) == len(payment_name) == 40
    calls = [
        {'requestId': 'invoice', 'operation': 'InvoiceAdd', 'fields': {'InvoiceAdd': {
            '$attributes': {'defMacro': 'TxnID:' + invoice_name}, 'CustomerRef': ref('Customer'),
            'ARAccountRef': ref('AR'), 'TxnDate': '2026-10-05', 'RefNumber': 'M' + STAMP,
            'Memo': 'Macro lifecycle ' + mode, 'InvoiceLineAdd': {
                'ItemRef': ref('Service'), 'Quantity': '1', 'Rate': '27.12'}}}},
        {'requestId': 'payment', 'operation': 'ReceivePaymentAdd', 'fields': {'ReceivePaymentAdd': {
            '$attributes': {'defMacro': 'TxnID:' + payment_name}, 'CustomerRef': ref('Customer'),
            'ARAccountRef': ref('AR'), 'TxnDate': '2026-10-05', 'TotalAmount': '27.12',
            'AppliedToTxnAdd': {'TxnID': macro(invoice_name), 'PaymentAmount': '27.12'}}}},
        {'requestId': 'deposit', 'operation': 'DepositAdd', 'fields': {'DepositAdd': {
            'TxnDate': '2026-10-05', 'DepositToAccountRef': ref('Bank'),
            'DepositLineAdd': {'PaymentTxnID': macro(payment_name)}}}},
    ]
    if mode == 'resumed_chain':
        blockers = [{'requestId': 'block' + str(i), 'operation': 'ClassAdd', 'fields': {
            'ClassAdd': {'Name': 'MACRORESUME' + STAMP}}} for i in range(2)]
        origin = run(mode + '-origin', blockers + calls, 1, True, False)
        assert [r['outcome'] for r in origin['results']] == ['succeeded', 'failed'] + ['unprocessed'] * 3
        for action in ['preview', 'apply']:
            args = [str(EXE), 'sdk', 'resume', '--input', origin['journalFile'], '--company-file', str(COMPANY), '--batch-size', '1']
            if action == 'apply':
                args.append('--apply')
            proc = subprocess.run(args, capture_output=True, text=True, encoding='utf-8')
            (DEST / f'lifecycle-{STAMP}-macro-resume-{action}.json').write_text(proc.stdout, encoding='utf-8')
            resumed = json.loads(proc.stdout)
            assert resumed['success'], resumed
            if action == 'preview':
                assert resumed['resumption']['canApply']
                assert [r['requestId'] for r in resumed['resumption']['pendingPlan']['requests']] == ['invoice', 'payment', 'deposit']
            else:
                created = resumed['execution']
                assert created['success'] and created['journalFile'] != origin['journalFile']
                (DEST / f'lifecycle-{STAMP}-macro-{mode}-result.json').write_text(json.dumps(created), encoding='utf-8')
    else:
        created = run(mode, calls, bound, True)
    assert len(created['messageSetResponses']) == (1 if bound == 50 else 3)
    invoice, payment, deposit = [r['data'][0] for r in created['results']]
    invoice_id, payment_id, deposit_id = [leaf(n, 'TxnID') for n in (invoice, payment, deposit)]
    verified = run(mode + '-verify', [
        {'requestId': 'invoice', 'operation': 'InvoiceQuery', 'fields': {'TxnID': invoice_id, 'IncludeLinkedTxns': True}},
        {'requestId': 'payment', 'operation': 'ReceivePaymentQuery', 'fields': {'TxnID': payment_id, 'IncludeLineItems': True}},
        {'requestId': 'deposit', 'operation': 'DepositQuery', 'fields': {'TxnID': deposit_id, 'IncludeLineItems': True}},
    ])
    current_invoice, current_payment, current_deposit = [r['data'][0] for r in verified['results']]
    assert leaf(current_invoice, 'BalanceRemaining') == '0.00'
    assert leaf(current_invoice, 'IsPaid') == 'true'
    assert leaf(current_invoice, 'Subtotal') == '27.12'
    assert leaf(current_payment, 'TotalAmount') == '27.12'
    applied = next(c for c in current_payment['children'] if c['name'] == 'AppliedToTxnRet')
    assert leaf(applied, 'TxnID') == invoice_id and leaf(applied, 'Amount') == '27.12'
    deposited = next(c for c in current_deposit['children'] if c['name'] == 'DepositLineRet')
    assert leaf(deposited, 'TxnID') == payment_id and leaf(deposited, 'Amount') == '27.12'
    assert leaf(current_deposit, 'DepositTotal') == '27.12'
    checks.append({'mode': mode, 'requestCount': 3, 'messageSetCount': len(created['messageSetResponses']),
        'invoicePaidVerified': True, 'paymentApplicationVerified': True, 'depositPaymentLinkVerified': True,
        'amountsVerified': True, 'macroNameLength': len(invoice_name),
        'wholeUnexecutedChainResumedInNewSession': mode == 'resumed_chain',
        'sessionScopedMacrosAcrossMessageSets': mode == 'split_messages'})
(DEST / f'lifecycle-{STAMP}-macro-verification.json').write_text(json.dumps(checks, indent=2), encoding='utf-8')
print(json.dumps(checks), flush=True)
