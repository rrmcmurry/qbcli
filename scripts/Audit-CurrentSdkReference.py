"""Compare official SDK 17 XMLOps field paths with the imported legacy OSR.
Caches official pages locally; emits structural gaps, not documentation prose.
Does not infer executable schemas from examples with incomplete annotations.
"""
import concurrent.futures
import hashlib
import html
import json
import pathlib
import re
import urllib.request
import xml.etree.ElementTree as ET

ROOT=pathlib.Path(__file__).resolve().parent.parent
CACHE=ROOT/'artifacts/sdk-current'
CACHE.mkdir(parents=True,exist_ok=True)
BASE='https://static.developer.intuit.com/output_html/qbdesktop/docs/api-reference/qbdesktop/'
LEGACY={o['name']:o for o in json.loads((ROOT/'src/QbCli.QuickBooks/Resources/sdk-reference.json').read_text(encoding='utf-8'))['operations']}
NAMES=[line.split('(')[0] for line in (ROOT/'src/QbCli/Resources/sdk-operations.txt').read_text().splitlines()]

def schema_paths(node,prefix=''):
    if node['type']=='skipwithchildren':return set()
    current=prefix if node['type'] in ('skip','OR') else prefix+'/'+node['name']
    result=set() if node['type'] in ('skip','OR') else {current}
    for child in node.get('children',[]):result.update(schema_paths(child,current))
    return result

def xml_paths(node,prefix=''):
    current=prefix+'/'+node.tag
    result={current}
    for child in node:result.update(xml_paths(child,current))
    return result

def schema_attributes(node,prefix=''):
    if node['type']=='skipwithchildren':return set()
    current=prefix if node['type'] in ('skip','OR') else prefix+'/'+node['name']
    result={current+'/@'+attribute['name'] for attribute in node.get('attributes',[])}
    for child in node.get('children',[]):result.update(schema_attributes(child,current))
    return result

def xml_attributes(node,prefix=''):
    current=prefix+'/'+node.tag
    result={current+'/@'+name for name in node.attrib}
    for child in node:result.update(xml_attributes(child,current))
    return result

def audit(name):
    url=BASE+name.lower()+'.html';path=CACHE/(name+'.html')
    try:
        if not path.exists():
            with urllib.request.urlopen(url,timeout=45) as response:path.write_bytes(response.read())
        source=path.read_text(encoding='utf-8');sample=None
        for pre in re.findall(r'<pre[^>]*>(.*?)</pre>',source,re.S):
            text=html.unescape(re.sub('<[^>]+>','',pre))
            if '<?qbxml' in text:sample=text;break
        if sample is None:return {'operation':name,'state':'no_xml_sample','url':url}
        xml=ET.fromstring(sample)
        rq=xml.find('.//'+name+'Rq');rs=xml.find('.//'+name+'Rs')
        gaps={};attribute_gaps={};attribute_counts={}
        for kind,node in [('request',rq),('response',rs)]:
            if node is None:continue
            current=xml_paths(node)
            legacy=schema_paths(LEGACY[name][kind]) if name in LEGACY else set()
            gaps[kind]=sorted(current-legacy)
            attributes=xml_attributes(node)
            legacy_attributes=schema_attributes(LEGACY[name][kind]) if name in LEGACY else set()
            attribute_gaps[kind]=sorted(attributes-legacy_attributes)
            attribute_counts[kind]=len(attributes)
        return {'operation':name,'state':'compared' if name in LEGACY else 'missing_legacy_operation',
                'url':url,'sourceSha256':hashlib.sha256(path.read_bytes()).hexdigest(),'missingPaths':gaps,
                'missingAttributes':attribute_gaps,'comparedAttributeCount':attribute_counts}
    except Exception as error:return {'operation':name,'state':'unparsed','errorType':type(error).__name__,'url':url}

with concurrent.futures.ThreadPoolExecutor(max_workers=6) as pool:results=list(pool.map(audit,NAMES))
out=ROOT/'artifacts/sdk-current-audit.json'
out.write_text(json.dumps({'retrieved':'2026-10-05','scope':'Element/attribute path comparison only; examples do not prove complete types, country/version/cardinality constraints or enums.','operations':results},indent=2),encoding='utf-8')
embedded=ROOT/'src/QbCli/Resources/current-reference-audit.json'
embedded.write_text(out.read_text(encoding='utf-8'),encoding='utf-8')
from collections import Counter
print(json.dumps({'states':dict(Counter(r['state'] for r in results)),
                  'operationsWithRequestGaps':sum(bool(r.get('missingPaths',{}).get('request')) for r in results),
                  'operationsWithRequestAttributeGaps':sum(bool(r.get('missingAttributes',{}).get('request')) for r in results),
                  'comparedAttributes':sum(sum(r.get('comparedAttributeCount',{}).values()) for r in results),'report':str(out)}))
