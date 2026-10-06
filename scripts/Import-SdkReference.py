"""Import structural facts from Intuit's public OSR. No prose or SDK binaries are bundled."""
import concurrent.futures
import hashlib
import json
import pathlib
import re
import urllib.request

ROOT = pathlib.Path(__file__).resolve().parent.parent
BASE = 'https://static.developer.intuit.com/qbSDK-current/common/newosr/qbsdk/json/'
CACHE = ROOT / 'artifacts' / 'sdk-source'
CACHE.mkdir(parents=True, exist_ok=True)

def fetch(name):
    target = CACHE / name
    if not target.exists():
        with urllib.request.urlopen(BASE + name, timeout=45) as response:
            data = response.read()
        target.write_bytes(data)
    return json.loads(target.read_text(encoding='utf-8-sig'))

def node(data):
    result = {'name': data.get('xmlName', ''), 'type': data.get('xmlType', 'skipwithchildren'),
              'required': bool(data.get('required', False)), 'count': str(data.get('count', '1'))}
    for key in ('supports',):
        if key in data: result[key] = data[key]
    versions = {k[6:]: str(v) for k, v in data.items() if k.startswith('minVer') and k[6:] in ('US','CA','UK','AU','OE')}
    if versions: result['versions'] = versions
    limits = {k[3:]: str(v) for k, v in data.items() if k.startswith('max') and k[3:] in ('DT','CA','UK','AU','OE')}
    if limits: result['maxLength'] = limits
    if 'minDT' in data: result['minimum'] = str(data['minDT'])
    if 'maxDT' in data and result['type'] in ('INTTYPE','AMTTYPE','FLOATTYPE','QUANTYPE','PERCENTTYPE','PRICETYPE'):
        result['maximum'] = str(data['maxDT'])
        result.pop('maxLength', None)
    if 'xmlEnumValues' in data:
        result['enumValues'] = [value for v in data['xmlEnumValues'] if '[PRIVATE]' not in v.upper()
                                if (value:=re.sub(r'\s*\[DEFAULT\]\s*', '', v, flags=re.IGNORECASE).strip())]
        private=[re.sub(r'\s*\[PRIVATE\]\s*','',v,flags=re.IGNORECASE).strip() for v in data['xmlEnumValues'] if '[PRIVATE]' in v.upper()]
        if private:result['excludedPrivateEnumValues']=private
    if data.get('elements'): result['children'] = [node(v) for v in data['elements'] if v is not None]
    if data.get('attributes'): result['attributes'] = [node(v) for v in data['attributes'] if v is not None]
    return result

def operation(entry):
    name = entry['name']
    request, response = fetch(name + 'Rq.json'), fetch(name + 'Rs.json')
    return {'name': name, 'versions': {k[6:]: str(v) for k,v in entry.items() if k.startswith('minVer') and k[6:] in ('US','CA','UK','AU','OE')},
            'request': node(request), 'response': node(response),
            'sourceSha256': {kind: hashlib.sha256((CACHE / (name + suffix + '.json')).read_bytes()).hexdigest()
                             for kind,suffix in [('request','Rq'),('response','Rs')]}}

def main():
    catalog = fetch('requests.json')
    with concurrent.futures.ThreadPoolExecutor(max_workers=6) as pool:
        operations = list(pool.map(operation, catalog['messages']))
    result = {'schemaVersion': 1, 'sourceUrl': BASE, 'retrieved': '2026-10-05',
              'referenceProtocolMaximum': '13.0',
              'scope': 'Structural facts from the public legacy OSR through qbXML 13.0. Newer fields and operations require separate documented schemas. Runtime edition, permissions and feature settings still govern acceptance.',
              'operations': sorted(operations, key=lambda v: v['name'])}
    destination = ROOT / 'src' / 'QbCli.QuickBooks' / 'Resources' / 'sdk-reference.json'
    destination.parent.mkdir(parents=True, exist_ok=True)
    destination.write_text(json.dumps(result, separators=(',',':'), ensure_ascii=False) + '\n', encoding='utf-8')
    print(json.dumps({'operations':len(operations),'bytes':destination.stat().st_size,'destination':str(destination)}))

if __name__ == '__main__': main()
