"""Compare current official request/response table string lengths with the OSR.

Table hierarchy comes from documented toggle-child depths. Ambiguous paths or
nonliteral limits are reported, never guessed or imported automatically.
Only US character limits and literal implementation versions are compared;
other-country and numeric limits need their own evidence. No documentation
prose is included in the output.
"""
import collections
import hashlib
import json
import pathlib
import re
from decimal import Decimal
from html.parser import HTMLParser

ROOT = pathlib.Path(__file__).resolve().parent.parent

class FieldTableParser(HTMLParser):
    def __init__(self):
        super().__init__(convert_charrefs=True)
        self.mode = None
        self.divs = []
        self.table_depth = 0
        self.row = None
        self.cell = None
        self.tag_capture = False
        self.rows = {'request': [], 'response': []}

    def handle_starttag(self, tag, attrs):
        attrs = dict(attrs)
        classes = attrs.get('class', '').split()
        if tag == 'div':
            self.divs.append(self.mode)
            if 'sphinx-tab-content' in classes:
                self.mode = ('request' if 'sphinx-data-tab-0' in classes else
                    'response' if 'sphinx-data-tab-1' in classes else None)
        if tag == 'table':
            self.table_depth += 1
        if tag == 'tr' and self.table_depth == 1 and self.mode:
            self.row = {'kind': self.mode, 'tag': '', 'depth': None, 'cells': []}
        if tag == 'td' and self.row is not None and self.table_depth == 1:
            self.cell = []
        if tag == 'p' and self.row is not None and self.cell is not None and not self.row['cells'] and self.table_depth == 1:
            if 'generic-toggler' in classes:
                depth = next((int(m.group(1)) for c in classes if (m := re.fullmatch(r'toggle-child(\d+)', c))), 0)
                self.row['depth'] = depth
                self.tag_capture = True

    def handle_endtag(self, tag):
        if tag == 'p':
            self.tag_capture = False
        if tag == 'td' and self.cell is not None and self.table_depth == 1:
            self.row['cells'].append(' '.join(''.join(self.cell).split()))
            self.cell = None
        if tag == 'tr' and self.row is not None and self.table_depth == 1:
            if len(self.row['cells']) == 5 and self.row['depth'] is not None:
                self.rows[self.row['kind']].append(self.row)
            self.row = None
        if tag == 'table':
            self.table_depth -= 1
        if tag == 'div' and self.divs:
            self.mode = self.divs.pop()

    def handle_data(self, value):
        if self.cell is not None and self.table_depth == 1:
            self.cell.append(value)
            if self.tag_capture:
                self.row['tag'] += value

def schema_paths(node, prefix=''):
    if node['type'] == 'skipwithchildren':
        return {}
    invisible = node['type'] in ('OR', 'skip')
    path = prefix if invisible else prefix + '/' + node['name']
    output = {} if invisible else {path: node}
    for attribute in node.get('attributes', []):
        output[path + '/@' + attribute['name']] = attribute
    for child in node.get('children', []):
        output.update(schema_paths(child, path))
    return output

def map_rows(rows, nodes, root):
    stack = {}
    for row in rows:
        depth = row['depth']
        tag = re.sub(r'\s*\([^)]*\)\s*', '', row['tag']).strip()
        names = [tag]
        if tag.startswith('I'):
            names.append(tag[1:])
        names += [name[:-4] for name in names[:] if name.endswith('List')]
        names += ['@' + name[:1].lower() + name[1:] for name in names[:]]
        if depth == 0:
            candidates = [p for p in nodes if p.rsplit('/', 1)[-1] in names]
            if len(candidates) != 1:
                candidates = [root] if tag == 'I' + root.rsplit('/', 1)[-1][:-2] else []
            if tag == 'IResponse':
                candidates = [root]
        elif depth - 1 in stack:
            parent = stack[depth - 1]
            candidates = [parent + '/' + name for name in names if parent + '/' + name in nodes]
            if tag.startswith('OR') or tag.startswith('('):
                candidates = [parent]
            if tag in ('Detail', 'DetailList'):
                detail_type = row['cells'][1].removeprefix('I').removesuffix('List')
                candidates = [parent + '/' + detail_type] if parent + '/' + detail_type in nodes else []
            if not candidates:
                # QBFC puts request-wrapper properties (e.g. IncludeRetElement)
                # alongside the add aggregate. Match only a unique root sibling.
                candidates = [root + '/' + name for name in names if root + '/' + name in nodes]
            if not candidates and tag.endswith('Filter') and row['cells'][1] == 'I' + tag:
                # QBFC-only query filter containers flatten into qbXML. Descendant
                # rows still have to match exact known qbXML paths and types.
                candidates = [parent]
        else:
            candidates = []
        stack = {d: p for d, p in stack.items() if d < depth}
        path = candidates[0] if len(candidates) == 1 else None
        if path:
            stack[depth] = path
        yield row, path

def effective_versions(node, minimum, prefix=''):
    if node['type'] == 'skipwithchildren':
        return {}
    own = Decimal(node.get('versions', {}).get('US', '0'))
    minimum = max(minimum, own)
    invisible = node['type'] in ('OR', 'skip')
    path = prefix if invisible else prefix + '/' + node['name']
    output = {} if invisible else {path: minimum}
    for attribute in node.get('attributes', []):
        output[path + '/@' + attribute['name']] = max(minimum, Decimal(attribute.get('versions', {}).get('US', '0')))
    for child in node.get('children', []):
        output.update(effective_versions(child, minimum, path))
    return output

def main():
    reference = json.loads((ROOT / 'src/QbCli.QuickBooks/Resources/sdk-reference.json').read_text(encoding='utf-8'))
    counts = collections.Counter()
    differences, unresolved, nonliteral, version_differences, ancestor_versions = [], [], [], [], []
    for operation in reference['operations']:
        file = ROOT / 'artifacts/sdk-current' / (operation['name'] + '.html')
        if not file.exists():
            continue
        parser = FieldTableParser()
        parser.feed(file.read_text(encoding='utf-8'))
        source = {'url': 'https://static.developer.intuit.com/output_html/qbdesktop/docs/api-reference/qbdesktop/' + operation['name'].lower() + '.html',
            'sourceSha256': hashlib.sha256(file.read_bytes()).hexdigest()}
        for kind, rows in parser.rows.items():
            nodes = schema_paths(operation[kind])
            root = '/' + operation[kind]['name']
            versions = effective_versions(operation[kind], Decimal(operation.get('versions', {}).get('US', '0')))
            for row, path in map_rows(rows, nodes, root):
                counts['tableRows'] += 1
                limit = row['cells'][2]
                if path is None:
                    unresolved.append({'operation': operation['name'], 'kind': kind, 'tag': row['tag'], 'depth': row['depth'], **source})
                    continue
                counts['mappedRows'] += 1
                table_version = row['cells'][3]
                clean_tag = re.sub(r'\s*\([^)]*\)\s*', '', row['tag']).strip().removesuffix('List')
                if re.fullmatch(r'\d+\.\d+', table_version) and clean_tag in (nodes[path]['name'], 'I' + nodes[path]['name']):
                    counts['usMinimumVersionsCompared'] += 1
                    if Decimal(table_version) != versions[path]:
                        fact = {'operation': operation['name'], 'kind': kind, 'path': path,
                            'tableMinimum': table_version, 'executableEffectiveUSMinimum': str(versions[path]), **source}
                        if Decimal(table_version) > versions[path]:
                            version_differences.append(fact)
                        else:
                            ancestor_versions.append(fact)
                match = re.fullmatch(r'(\d+) chars?', limit)
                if not match:
                    if limit:
                        nonliteral.append({'operation': operation['name'], 'kind': kind, 'path': path,
                            'reason': 'not_a_literal_character_limit',
                            'cellTextSha256': hashlib.sha256(limit.encode('utf-8')).hexdigest(), **source})
                    continue
                node = nodes[path]
                if node['type'] not in ('STRTYPE', 'IDTYPE'):
                    continue
                counts['usCharacterLimitsCompared'] += 1
                existing = node.get('maxLength', {}).get('DT')
                if existing != match.group(1):
                    differences.append({'operation': operation['name'], 'kind': kind, 'path': path,
                        'tableLimit': int(match.group(1)), 'executableUSLimit': existing, **source})
    result = {'retrieved': '2026-10-05', 'scope': 'Current official QBFC request/response tables mapped to known qbXML paths. Literal character limits compared with executable US/DT string and ID limits; literal implementation versions compared with effective US operation/ancestor/field minima. Country qualifiers, nonliteral/numeric constraints and unmapped QBFC properties are not inferred. Differences require review, not automatic import. Lower table minima than enclosing operations/aggregates are contextual observations, not newly permitted protocol versions.',
        'comparedFacts': dict(counts), 'differences': differences, 'versionDifferences': version_differences,
        'tableMinimumBelowEffectiveAncestor': ancestor_versions,
        'unresolvedPaths': unresolved, 'nonliteralLimits': nonliteral}
    (ROOT / 'artifacts/sdk-field-table-audit.json').write_text(json.dumps(result, indent=2), encoding='utf-8')
    (ROOT / 'src/QbCli/Resources/sdk-field-table-audit.json').write_text(json.dumps(result, separators=(',', ':')) + '\n', encoding='utf-8')
    print(json.dumps({'comparedFacts': dict(counts), 'differences': len(differences), 'versionDifferences': len(version_differences),
        'ancestorVersionObservations': len(ancestor_versions), 'unresolvedPaths': len(unresolved), 'nonliteralLimits': len(nonliteral)}))

if __name__ == '__main__':
    main()
