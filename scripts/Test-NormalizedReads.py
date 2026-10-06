"""Fresh read-only SDK comparisons of reports and transaction summaries in Test.QBW.

No fixture writes, backups, restore, overlap or request retries. Exports remain
in ignored artifacts; verification metadata contains only counts and test scope.
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
parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--executable', type=pathlib.Path, default=ROOT / 'artifacts/win-x64-0.8.10/qbcli.exe')
parser.add_argument('--scope', choices=['all','reports','transactions'], default='all')
options = parser.parse_args()
EXE = options.executable.resolve()
DEST = ROOT / 'artifacts/fixtures'
STAMP = datetime.datetime.now().strftime('%H%M%S')

def run(label, args, complete=True):
    proc = subprocess.run([str(EXE), *args, '--company-file', str(COMPANY), '--json'], capture_output=True, text=True, encoding='utf-8')
    (DEST / f'lifecycle-{STAMP}-normalized-{label}.json').write_text(proc.stdout, encoding='utf-8')
    result = json.loads(proc.stdout, parse_float=decimal.Decimal)
    assert proc.returncode == 0 and result['success'], 'SDK/normalized read failed'
    assert pathlib.Path(result['companyFile']).resolve() == COMPANY
    if complete:
        assert result['complete']
    return result

def sdk(label, operation, fields, all_pages=False):
    path = DEST / f'lifecycle-{STAMP}-normalized-{label}-fields.json'
    path.write_text(json.dumps(fields), encoding='utf-8')
    return run(label + '-sdk', ['sdk', 'run', operation, '--input', str(path), *(['--all', '--page-size', '25'] if all_pages else [])], complete=all_pages)

def one(node, name):
    matches = [c for c in node['children'] if c['name'] == name]
    assert len(matches) <= 1
    return matches[0] if matches else None

def scalar(node, name):
    field = one(node, name)
    return field.get('value') if field else None

TYPES = {'AMTTYPE':'amount', 'STRTYPE':'text', 'DATETYPE':'date', 'DATETIMETYPE':'dateTime',
    'INTTYPE':'integer', 'QUANTYPE':'quantity', 'PERCENTTYPE':'percent', 'BOOLTYPE':'boolean'}
report_checks = []
for command, report_type in ([] if options.scope == 'transactions' else [('trial-balance', 'TrialBalance'), ('profit-loss', 'ProfitAndLossStandard'), ('balance-sheet', 'BalanceSheetStandard')]):
    for basis in ['accrual', 'cash']:
        label = command + '-' + basis
        period = {'ToReportDate':'2026-10-05'}
        dates = ['--as-of', '2026-10-05']
        if command == 'profit-loss':
            period['FromReportDate'] = '2026-01-01'
            dates = ['--from', '2026-01-01', '--to', '2026-10-05']
        source = sdk(label, 'GeneralSummaryReportQuery', {'GeneralSummaryReportType':report_type,
            'DisplayReport':False, 'ReportPeriod':period, 'ReportBasis':basis.title(), 'ReturnRows':'All', 'ReturnColumns':'All'})
        raw = source['results'][0]['data'][0]
        actual = run(label + '-report', ['reports', command, *dates, '--basis', basis])
        columns = []
        for col in [c for c in raw['children'] if c['name'] == 'ColDesc']:
            columns.append({'id':int(col['attributes']['colID']), 'valueType':TYPES[col['attributes']['dataType']],
                'meaning':scalar(col, 'ColType'), 'titles':[{'row':int(t['attributes']['titleRow']), 'text':t['attributes'].get('value')}
                    for t in col['children'] if t['name'] == 'ColTitle']})
        by_id = {c['id']:c for c in columns}
        rows = []
        for row in one(raw, 'ReportData')['children']:
            entity = one(row, 'RowData')
            cells = []
            for cell in [c for c in row['children'] if c['name'] == 'ColData']:
                attr = cell['attributes']; col_id = int(attr['colID'])
                value_type = TYPES[attr['dataType']] if 'dataType' in attr else by_id[col_id]['valueType']
                text = attr.get('value')
                cells.append({'columnId':col_id, 'valueType':value_type, 'text':text,
                    'amount':decimal.Decimal(text) if value_type == 'amount' and text not in (None, '') else None})
            rows.append({'number':int(row['attributes']['rowNumber']), 'kind':row['name'].removesuffix('Row').lower(),
                'text':row['attributes'].get('value'), 'entityType':entity['attributes'].get('rowType') if entity else None,
                'entityName':entity['attributes'].get('value') if entity else None, 'cells':cells})
        assert actual['columns'] == columns and actual['rows'] == rows, 'Report differs from fresh SDK fields'
        assert actual['title'] == scalar(raw, 'ReportTitle') and actual['subtitle'] == scalar(raw, 'ReportSubtitle')
        source_basis = scalar(raw, 'ReportBasis')
        assert actual['basis'] == (source_basis.lower() if source_basis else None)
        assert actual['titleRowCount'] == int(scalar(raw, 'NumColTitleRows'))
        assert len(rows) == int(scalar(raw, 'NumRows')) and len(columns) == int(scalar(raw, 'NumColumns'))
        report_checks.append({'reportType':report_type, 'requestedBasis':basis, 'columns':len(columns), 'rows':len(rows),
            'cells':sum(len(r['cells']) for r in rows), 'everyFieldMatchesIndependentSdkRead':True,
            'freshLiveComparison':True, 'complete':True, 'scope':'Current Test.QBW snapshot; explicit dates, All rows/columns, no report window.'})
        print(json.dumps(report_checks[-1]), flush=True)
if report_checks:
    (DEST / f'lifecycle-{STAMP}-report-normalization-verification.json').write_text(json.dumps(report_checks, indent=2), encoding='utf-8')

def transaction(raw):
    result = {key:scalar(raw, field) for key, field in [('id','TxnID'), ('type','TxnType'), ('date','TxnDate'), ('number','RefNumber'), ('memo','Memo')]}
    amount = scalar(raw, 'Amount'); result['amount'] = decimal.Decimal(amount) if amount is not None else None
    for key, field in [('entity','EntityRef'), ('account','AccountRef'), ('currency','CurrencyRef')]:
        ref = one(raw, field)
        result[key] = {'id':scalar(ref,'ListID'), 'fullName':scalar(ref,'FullName')} if ref else None
    return result

transaction_checks = []
for label, since, until, memo in ([] if options.scope == 'reports' else [('all',None,None,None), ('day','2026-10-05','2026-10-05',None),
    ('empty',None,'2025-12-31',None), ('memo','2026-01-01','2026-12-31','salePOST191618')]):
    fields = {'TransactionDetailLevelFilter':'SummaryOnly'}
    dates = {}
    args = ['transactions', 'search' if memo else 'list', '--page-size', '25']
    if since:
        dates['FromTxnDate'] = since; args += ['--since',since]
    if until:
        dates['ToTxnDate'] = until; args += ['--until',until]
    if dates:
        fields['TransactionDateRangeFilter'] = dates
    if memo:
        args += ['--memo',memo]
    raw = sdk('transactions-' + label, 'TransactionQuery', fields, all_pages=True)
    actual = run('transactions-' + label + '-summary',args)
    expected = [transaction(r) for r in raw['data']]
    if memo:
        expected = [r for r in expected if memo.upper() in (r['memo'] or '').upper()]
    expected.sort(key=lambda r:(r['date'],r['type'],r['id']))
    assert actual['results'] == expected and actual['scannedRecords'] == len(raw['data'])
    assert actual['pages'] == raw['pages'] and actual['selection'] == {'since':since,'until':until,'memoContains':memo}
    assert len({(r['id'],r['type']) for r in expected}) == len(expected)
    transaction_checks.append({'selectionCase':label, 'scannedRecords':len(raw['data']), 'matches':len(expected),
        'pages':raw['pages'], 'everyFieldMatchesIndependentSdkRead':True, 'freshLiveComparison':True,
        'complete':True, 'scope':'Current Test.QBW TransactionQuery SummaryOnly; permission/feature and TimeTracking exclusions apply. ASCII case-insensitive summary memo comparison only.'})
    print(json.dumps(transaction_checks[-1]), flush=True)
    (DEST / f'lifecycle-{STAMP}-transaction-normalization-verification.json').write_text(json.dumps(transaction_checks, indent=2), encoding='utf-8')
