"""Compare enum facts in current official XML examples against imported schemas.
No enum is made executable by this audit. Missing country/version evidence must
be resolved before introducing a new executable value.
"""
import collections
import hashlib
import html
import json
import pathlib
import re
import xml.etree.ElementTree as ET
ROOT=pathlib.Path(__file__).resolve().parent.parent
reference=json.loads((ROOT/'src/QbCli.QuickBooks/Resources/sdk-reference.json').read_text(encoding='utf-8'))
def schema_nodes(node,prefix=''):
    if node['type']=='skipwithchildren':return {}
    path=prefix if node['type'] in ('OR','skip') else prefix+'/'+node['name']
    result={} if node['type'] in ('OR','skip') else {path:node}
    for child in node.get('children',[]):result.update(schema_nodes(child,path))
    return result
def xml_enums(node,prefix=''):
    path=prefix+'/'+node.tag
    result={}
    for child in node:
        if child.tag==ET.Comment:
            match=re.fullmatch(r'\s*(\w+) may have one of the following values:\s*(.*?)\s*',child.text or '',re.S)
            if match:
                raw=[s.strip() for s in match[2].split(',') if s.strip()]
                clean=lambda s:re.sub(r'\s*\[(?:DEFAULT|PRIVATE)\]\s*','',s,flags=re.I).strip()
                result[path+'/'+match[1]]={'public':[clean(s) for s in raw if '[PRIVATE]' not in s.upper()],
                    'private':[clean(s) for s in raw if '[PRIVATE]' in s.upper()]}
        else:result.update(xml_enums(child,path))
    return result
comparisons=[];differences=[];unparsed=[]
for operation in reference['operations']:
    file=ROOT/'artifacts/sdk-current'/(operation['name']+'.html')
    if not file.exists():continue
    source=file.read_text(encoding='utf-8')
    try:
        sample=next(html.unescape(re.sub('<[^>]+>','',p)) for p in re.findall(r'<pre[^>]*>(.*?)</pre>',source,re.S) if '<?qbxml' in html.unescape(re.sub('<[^>]+>','',p)))
        xml=ET.fromstring(sample,parser=ET.XMLParser(target=ET.TreeBuilder(insert_comments=True)))
        for kind,suffix in [('request','Rq'),('response','Rs')]:
            current=xml.find('.//'+operation['name']+suffix)
            if current is None:continue
            nodes=schema_nodes(operation[kind])
            for path,enum in xml_enums(current).items():
                node=nodes.get(path)
                if node is None:continue
                private=node.get('excludedPrivateEnumValues',[])
                public=[v for v in enum['public'] if v not in private]
                imported=node.get('enumValues',[])
                entry={'operation':operation['name'],'kind':kind,'path':path,'currentPublicValues':public,
                    'missingExecutableValues':sorted(set(public)-set(imported)),
                    'legacyValuesNotListedCurrently':sorted(set(imported)-set(public)),
                    'privateValuesExcluded':enum['private'],
                    'executableValueVersionRules':node.get('enumValueVersions',{}),
                    'executableEnumSourceUrl':node.get('enumSourceUrl'),
                    'url':'https://static.developer.intuit.com/output_html/qbdesktop/docs/api-reference/qbdesktop/'+operation['name'].lower()+'.html',
                    'sourceSha256':hashlib.sha256(file.read_bytes()).hexdigest()}
                comparisons.append(entry)
                if entry['missingExecutableValues'] or entry['legacyValuesNotListedCurrently']:differences.append(entry)
    except Exception as ex:unparsed.append({'operation':operation['name'],'errorType':type(ex).__name__})
result={'retrieved':'2026-10-05','scope':'Enum comment comparisons only. Current example omissions do not withdraw documented legacy values; new values need separate country/version evidence. Private SDK values stay excluded.',
        'comparedFields':len(comparisons),'differences':differences,'unparsed':unparsed}
(ROOT/'artifacts/sdk-enum-audit.json').write_text(json.dumps(result,indent=2),encoding='utf-8')
(ROOT/'src/QbCli/Resources/sdk-enum-audit.json').write_text(json.dumps(result,indent=2),encoding='utf-8')
print(json.dumps({'comparedFields':len(comparisons),'differences':len(differences),'unparsed':len(unparsed),'missingRequestValues':[{k:e[k] for k in ('operation','path','missingExecutableValues')} for e in differences if e['kind']=='request' and e['missingExecutableValues']]}))
