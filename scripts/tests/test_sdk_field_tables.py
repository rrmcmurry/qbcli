"""Source-table hierarchy tests; no QuickBooks, network or company-file access."""
import importlib.util
import pathlib
import unittest
from decimal import Decimal

SOURCE = pathlib.Path(__file__).resolve().parents[1] / 'Audit-SdkFieldTables.py'
SPEC = importlib.util.spec_from_file_location('field_tables', SOURCE)
AUDIT = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(AUDIT)

def node(name, children=(), kind='Aggregate', attributes=(), version=None):
    return {'name': name, 'type': kind, 'children': list(children), 'attributes': list(attributes),
        'versions': {} if version is None else {'US': str(version)}}

def row(tag, depth, type_name='', limit='', implementation='', prose=''):
    return f'<tr><td><p class="generic-toggler toggle-child{depth}">{tag}</p><div>{prose}</div></td><td>{type_name}</td><td>{limit}</td><td>{implementation}</td><td></td></tr>'

class FieldTables(unittest.TestCase):
    def test_response_details_and_nested_prose_table(self):
        nested = '<table><tr><td><p class="generic-toggler toggle-child9">NotAField</p></td></tr></table>'
        html = '<div class="sphinx-tab-content sphinx-data-tab-1"><table>' + ''.join([
            row('IResponse', 0), row('StatusCode', 1), row('Detail', 1, 'ICustomerRet'),
            row('Name', 2, 'IQBStringType', '41 chars', prose=nested),
            row('Detail', 1, 'IErrorRecovery'), row('Message', 2, 'IQBStringType', '50 chars')]) + '</table></div>'
        parser = AUDIT.FieldTableParser()
        parser.feed(html)
        schema = node('CustomerAddRs', [node('CustomerRet', [node('Name', kind='STRTYPE')]),
            node('ErrorRecovery', [node('Message', kind='STRTYPE')])], attributes=[node('statusCode', kind='INTTYPE')])
        mapped = list(AUDIT.map_rows(parser.rows['response'], AUDIT.schema_paths(schema), '/CustomerAddRs'))
        self.assertEqual([path for _, path in mapped], ['/CustomerAddRs', '/CustomerAddRs/@statusCode',
            '/CustomerAddRs/CustomerRet', '/CustomerAddRs/CustomerRet/Name',
            '/CustomerAddRs/ErrorRecovery', '/CustomerAddRs/ErrorRecovery/Message'])
        self.assertEqual(len(mapped), 6)
        self.assertFalse(parser.rows['request'])

    def test_query_filter_qbfc_wrappers_flatten_but_real_aggregates_remain(self):
        html = '<div class="sphinx-tab-content sphinx-data-tab-0"><table>' + ''.join([
            row('ICustomerQuery', 0), row('ORCustomerListQuery', 1),
            row('CustomerListFilter', 2, 'ICustomerListFilter'),
            row('NameFilter', 3, 'INameFilter'), row('Name', 4, 'IQBStringType', '41 chars')]) + '</table></div>'
        parser = AUDIT.FieldTableParser()
        parser.feed(html)
        schema = node('CustomerQueryRq', [node('OR', [node('nothing', [node('NameFilter', [node('Name', kind='STRTYPE')])], 'skip')], 'OR')])
        mapped = list(AUDIT.map_rows(parser.rows['request'], AUDIT.schema_paths(schema), '/CustomerQueryRq'))
        self.assertEqual([path for _, path in mapped], ['/CustomerQueryRq'] * 3 +
            ['/CustomerQueryRq/NameFilter', '/CustomerQueryRq/NameFilter/Name'])

    def test_unknown_fields_are_not_matched_by_name_in_another_aggregate(self):
        schema = node('CustomerAddRq', [node('CustomerAdd', [node('BillAddress', [node('Addr1', kind='STRTYPE')])])])
        rows = [{'depth': 0, 'tag': 'ICustomerAdd', 'cells': ['', '', '', '', '']},
            {'depth': 1, 'tag': 'Addr1', 'cells': ['', 'IQBStringType', '41 chars', '', '']}]
        mapped = list(AUDIT.map_rows(rows, AUDIT.schema_paths(schema), '/CustomerAddRq'))
        self.assertIsNone(mapped[1][1])

    def test_effective_version_keeps_hidden_choice_and_parent_constraints(self):
        schema = node('InvoiceAddRq', [node('InvoiceAdd', [node('OR', [
            node('Name', kind='STRTYPE', version=2)], 'OR', version=7)], version=6)])
        minimum = AUDIT.effective_versions(schema, Decimal('5'))
        self.assertEqual(minimum['/InvoiceAddRq/InvoiceAdd/Name'], Decimal('7'))

if __name__ == '__main__':
    unittest.main()
