"""Audit structural facts in current official examples; do not infer schemas.

Required flags inside legacy choice groups may be conditional. Differences are
review inputs, never automatic executable constraint changes.
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

def schemas(node,prefix='',choice=False):
    if node['type']=='skipwithchildren':return {}
    path=prefix if node['type'] in ('OR','skip') else prefix+'/'+node['name']
    result={} if node['type'] in ('OR','skip') else {path:(node,choice)}
    for attr in node.get('attributes',[]):result[path+'/@'+attr['name']]=(attr,choice)
    for child in node.get('children',[]):result.update(schemas(child,path,choice or node['type']=='OR'))
    return result

def examples(node,prefix='',annotation=None):
    path=prefix+'/'+node.tag
    descendants=[c for c in node if c.tag!=ET.Comment]
    if descendants and len(node) and node[0].tag==ET.Comment:
        annotation=node[0].text
    text=(node.text or '').strip()
    fact={'type':'Aggregate' if descendants else text}
    if annotation and re.fullmatch(r'\s*(required|optional)(?:, may repeat)?\s*',annotation):
        fact.update(required=annotation.strip().startswith('required'),repeats='may repeat' in annotation)
    result={path:fact}
    for key,value in node.attrib.items():result[path+'/@'+key]={'type':value}
    for index,child in enumerate(node):
        if child.tag==ET.Comment:continue
        following=node[index+1] if index+1<len(node) else None
        annotation=following.text if following is not None and following.tag==ET.Comment else None
        result.update(examples(child,path,annotation))
    return result

counts=collections.Counter();differences=[];unparsed=[]
for operation in reference['operations']:
    path=ROOT/'artifacts/sdk-current'/(operation['name']+'.html')
    if not path.exists():continue
    try:
        source=path.read_text(encoding='utf-8')
        sample=next(html.unescape(re.sub('<[^>]+>','',p)) for p in re.findall(r'<pre[^>]*>(.*?)</pre>',source,re.S)
            if '<?qbxml' in html.unescape(re.sub('<[^>]+>','',p)))
        xml=ET.fromstring(sample,parser=ET.XMLParser(target=ET.TreeBuilder(insert_comments=True)))
        for kind,suffix in [('request','Rq'),('response','Rs')]:
            current=xml.find('.//'+operation['name']+suffix)
            if current is None:continue
            nodes=schemas(operation[kind])
            for field,fact in examples(current).items():
                if field not in nodes:continue
                node,choice=nodes[field]
                mismatch={}
                if fact['type'] in ('Aggregate',) or re.fullmatch('[A-Z]+TYPE',fact['type']):
                    counts['types']+=1
                    if fact['type']!=node['type']:mismatch['type']={'example':fact['type'],'executable':node['type']}
                if 'required' in fact:
                    counts['requiredFlags']+=1;counts['repeatFlags']+=1
                    if fact['required']!=node['required']:mismatch['required']={'example':fact['required'],'executable':node['required']}
                    repeat='n' in node['count']
                    if fact['repeats']!=repeat:mismatch['repeats']={'example':fact['repeats'],'executable':repeat}
                if mismatch:
                    differences.append({'operation':operation['name'],'kind':kind,'path':field,'differences':mismatch,
                        'legacyChoiceAncestor':choice,'url':'https://static.developer.intuit.com/output_html/qbdesktop/docs/api-reference/qbdesktop/'+operation['name'].lower()+'.html',
                        'sourceSha256':hashlib.sha256(path.read_bytes()).hexdigest()})
    except Exception as error:unparsed.append({'operation':operation['name'],'errorType':type(error).__name__})
result={'retrieved':'2026-10-05','scope':'XML example types and explicit required/repetition comments only; conditional choice semantics, numeric bounds, max lengths and country/version evidence need separate review. Differences do not automatically change executable validation.',
        'comparedFacts':dict(counts),'differences':differences,'unparsed':unparsed}
(ROOT/'artifacts/sdk-constraint-audit.json').write_text(json.dumps(result,indent=2),encoding='utf-8')
(ROOT/'src/QbCli/Resources/sdk-constraint-audit.json').write_text(json.dumps(result,indent=2),encoding='utf-8')
print(json.dumps({'comparedFacts':dict(counts),'differences':len(differences),'differenceKinds':dict(collections.Counter(k for e in differences for k in e['differences'])),'unparsed':len(unparsed)}))
