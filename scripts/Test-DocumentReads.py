"""Read-only live verification of normalized invoice/bill lists in Test.QBW.

Run only after the existing SDK client has terminated. No fixture mutations,
backups or restores. Each comparison issues independent complete SDK reads.
"""
import argparse
import datetime
import decimal
import json
import pathlib
import subprocess

ROOT = pathlib.Path(__file__).resolve().parent.parent
COMPANY = (ROOT / 'QBData/Test.QBW').resolve()
assert COMPANY == pathlib.Path(__import__("os").environ["QBCLI_LIVE_TEST_COMPANY"]).resolve(strict=True)
DEST = ROOT / 'artifacts/fixtures'

def leaf(node, name):
    matches = [c for c in node['children'] if c['name'] == name]
    assert len(matches) <= 1
    return matches[0].get('value') if matches else None

def aggregate(node, name):
    matches = [c for c in node['children'] if c['name'] == name]
    assert len(matches) <= 1
    return matches[0] if matches else None

def expected(record, kind, bill_balances=None):
    party = aggregate(record, 'CustomerRef' if kind == 'Invoice' else 'VendorRef')
    assert party is not None
    currency = aggregate(record, 'CurrencyRef')
    paid = leaf(record, 'IsPaid')
    balance = leaf(record, 'BalanceRemaining') if kind=='Invoice' else (0 if paid in ('true','1') else (bill_balances or {}).get(leaf(record,'TxnID')))
    return {'id': leaf(record, 'TxnID'), 'number': leaf(record, 'RefNumber'), 'date': leaf(record, 'TxnDate'),
        'dueDate': leaf(record, 'DueDate'), 'party': {'id': leaf(party, 'ListID'), 'fullName': leaf(party, 'FullName')},
        'remainingAmount': decimal.Decimal(balance) if balance is not None else None,
        'paid': None if paid is None else paid in ('true', '1'), 'memo': leaf(record, 'Memo'),
        'currency': None if currency is None else {'id': leaf(currency, 'ListID'), 'name': leaf(currency, 'FullName')}}

def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--executable', type=pathlib.Path, default=ROOT / 'artifacts/win-x64/qbcli.exe')
    options = parser.parse_args()
    exe = options.executable.resolve()
    stamp = datetime.datetime.now().strftime('%H%M%S')
    prefix = DEST / f'lifecycle-{stamp}-document-reads'

    def run(label, args):
        proc = subprocess.run([str(exe), *args, '--company-file', str(COMPANY), '--json'],
            capture_output=True, text=True, encoding='utf-8')
        prefix.with_name(prefix.name + '-' + label + '.json').write_text(proc.stdout, encoding='utf-8')
        value = json.loads(proc.stdout, parse_float=decimal.Decimal)
        assert proc.returncode == 0 and value['success'] and value.get('complete',True), 'Read failed or incomplete'
        assert pathlib.Path(value['companyFile']).resolve() == COMPANY
        return value

    checks = []
    for kind, command in [('Invoice', 'invoices'), ('Bill', 'bills')]:
        for open_only in [False, True]:
            label = kind.lower() + ('-open' if open_only else '-all')
            fields = prefix.with_name(prefix.name + '-' + label + '-fields.json')
            fields.write_text(json.dumps({'PaidStatus': 'NotPaidOnly' if open_only else 'All',
                'IncludeLineItems': False, 'IncludeLinkedTxns': False}), encoding='utf-8')
            raw = run(label + '-sdk', ['sdk', 'run', kind + 'Query', '--input', str(fields), '--all', '--page-size', '1'])
            balances={}
            if kind=='Bill':
                vendors=sorted({leaf(aggregate(r,'VendorRef'),'ListID') for r in raw['data'] if leaf(r,'IsPaid') not in ('true','1')})
                if vendors:
                    plan=prefix.with_name(prefix.name+'-'+label+'-balances-plan.json')
                    plan.write_text(json.dumps({'protocolVersion':'13.0','country':'US','requests':[{'requestId':str(i),'operation':'BillToPayQuery','fields':{'PayeeEntityRef':{'ListID':v}}} for i,v in enumerate(vendors)]}),encoding='utf-8')
                    payable=run(label+'-balances',['sdk','batch','--input',str(plan)])
                    for response in payable['results']:
                        assert response['outcome'] in ('succeeded','no_match')
                        for row in response['data']:
                            detail=aggregate(row,'BillToPay')
                            if detail and leaf(detail,'TxnType')=='Bill':
                                identity=leaf(detail,'TxnID');assert identity not in balances
                                balances[identity]=decimal.Decimal(leaf(detail,'AmountDue'))
            normalized = run(label + '-normalized', [command, 'list', '--page-size', '1', *(['--open'] if open_only else [])])
            wanted = sorted([expected(r, kind,balances) for r in raw['data']],
                key=lambda r: (r['date'], r['number'] is not None, r['number'] or '', r['id']))
            assert normalized['results'] == wanted, 'Normalized summaries differ from fresh SDK data'
            assert normalized['openOnly'] == open_only and normalized['documentType'] == kind.lower()
            assert len({r['id'] for r in wanted}) == len(wanted)
            if open_only:
                assert all(r['paid'] is not True and r['remainingAmount'] != 0 for r in wanted)
            checks.append({'documentType': kind.lower(), 'openOnly': open_only, 'records': len(wanted),
                'remainingAmountSource':'BillToPayQuery.AmountDue_or_IsPaid' if kind=='Bill' else 'InvoiceQuery.BalanceRemaining',
                'sdkPages': raw['pages'], 'normalizedPages': normalized['pages'],
                'everySummaryFieldMatchesIndependentSdkRead': True, 'complete': True,
                'missingRemainingAmountRecords': sum(r['remainingAmount'] is None for r in wanted),
                'currencyRemainsExplicitOrUnknown': True,
                'scope': 'Current Test.QBW snapshot, read only. No synthetic partial-payment fixture was created by this verifier.'})
            print(json.dumps(checks[-1]), flush=True)
    prefix.with_name(prefix.name + '-verification.json').write_text(json.dumps(checks, indent=2), encoding='utf-8')

if __name__ == '__main__':
    main()
