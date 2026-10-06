"""Import narrowly verified newer structural fields from official reference pages.
Run Audit-CurrentSdkReference.py first. Unreviewed gaps stay non-executable.
"""
import html
import json
import pathlib
import re
import xml.etree.ElementTree as ET
ROOT=pathlib.Path(__file__).resolve().parent.parent
target=ROOT/'src/QbCli.QuickBooks/Resources/sdk-reference.json'
reference=json.loads(target.read_text(encoding='utf-8'))
operations={o['name']:o for o in reference['operations']}
audit=json.loads((ROOT/'artifacts/sdk-current-audit.json').read_text(encoding='utf-8'))

def visible(node):
    if node['type'] in ('OR','skip'):
        return [name for c in node.get('children',[]) for name in visible(c)]
    return [node['name']]

def children(node):
    for child in node.get('children',[]):
        if child['type'] in ('OR','skip'):yield from children(child)
        else:yield child

added=[]
for entry in audit['operations']:
    if entry['operation'] not in operations:continue
    source=(ROOT/'artifacts/sdk-current'/(entry['operation']+'.html')).read_text(encoding='utf-8')
    sample=next(html.unescape(re.sub('<[^>]+>','',pre)) for pre in re.findall(r'<pre[^>]*>(.*?)</pre>',source,re.S) if '<?qbxml' in html.unescape(re.sub('<[^>]+>','',pre)))
    xml=ET.fromstring(sample)
    for kind,paths in entry['missingPaths'].items():
        for path in paths:
            field=path.split('/')[-1]
            if field not in ('FormType','ExpirationDateForSerialLotNumber','SOChannel','StoreName','StoreType'):continue
            schema=operations[entry['operation']][kind]
            parts=path.strip('/').split('/')
            current=xml.find('.//'+parts[0])
            for part in parts[1:-1]:
                schema=next(c for c in children(schema) if c['name']==part)
                current=current.find(part)
            if any(c['name']==field for c in children(schema)):continue
            # Facts checked in current table: FormType is optional string since 14;
            # expiration is string, max 1099, SDK16, USA/Canada per field description.
            countries=['US'] if field in ('FormType','SOChannel','StoreName','StoreType') else ['US','CA']
            version='14' if field=='FormType' else '16'
            node={'name':field,'type':'STRTYPE','required':False,'count':'0 - 1',
                  'supports':{c:c in countries for c in ('US','CA','UK','AU','OE')},
                  'versions':{c:version for c in countries}}
            if field=='ExpirationDateForSerialLotNumber':node['maxLength']={'DT':'1099','CA':'1099'}
            if field=='SOChannel':
                node['type']='ENUMTYPE'
                # SDK15 release notes establish the documented enum spellings;
                # current field tables conservatively require protocol16.
                node['enumValues']=['Blank','Ecommerce']
                node['enumSourceUrl']='https://static.developer.intuit.com/resources/ReleaseNotes_QBXMLSDK_15_0.pdf'
            following=[c.tag for c in list(current)[list(current).index(current.find(field))+1:]]
            index=next((i for i,c in enumerate(schema.get('children',[])) if any(n in following for n in visible(c))),len(schema.get('children',[])))
            schema.setdefault('children',[]).insert(index,node)
            added.append({'operation':entry['operation'],'kind':kind,'path':path,'url':entry['url'],'sourceSha256':entry['sourceSha256']})
reference['referenceProtocolMaximum']='17.0'
reference['scope']='Legacy OSR structural schemas through qbXML 13.0, plus explicitly verified selected SDK14/16 fields and SDK17 enum changes from current official reference. Protocols through17 may use these known fields; unimported newer fields remain rejected. Not a complete SDK17 schema.'
existing=reference.get('supplements',[])
reference['supplements']=existing+[a for a in added if a not in existing]

# SDK14 release notes explicitly establish US-only BOX15 introduction and
# BOX14 removal. The public SDK features page corroborates BOX16 in SDK16,
# includes BOX14 at16, then explicitly removes BOX14 again at17.
enum_source='https://static.developer.intuit.com/resources/ReleaseNotes_QBXMLSDK_14_0.pdf'
feature_source='https://developer.intuit.com/app/developer/qbdesktop/docs/get-started/desktop-sdk-features'
def mapping_nodes(node):
    if node['name']=='MappingCategory' and node['type']=='ENUMTYPE':yield node
    for child in node.get('children',[]):yield from mapping_nodes(child)
for name in ('Form1099CategoryAccountMappingQuery','Form1099CategoryAccountMappingMod'):
    for kind in ('request','response'):
        for node in mapping_nodes(operations[name][kind]):
            if 'BOX15' not in node['enumValues']:node['enumValues'].append('BOX15')
            if 'BOX16' not in node['enumValues']:node['enumValues'].append('BOX16')
            node['enumSourceUrl']=feature_source
            node['enumValueVersions']={'BOX15':{'US':{'minimum':'14.0'},**{c:{'supported':False} for c in ('CA','UK','AU','OE')}},
                'BOX14':{'US':{'includedRanges':[{'maximumExclusive':'14.0'},{'minimum':'16.0','maximumExclusive':'17.0'}]}},
                'BOX16':{'US':{'minimum':'16.0'},**{c:{'supported':False} for c in ('CA','UK','AU','OE')}}}
reference['enumSupplements']=[{'operations':['Form1099CategoryAccountMappingQuery','Form1099CategoryAccountMappingMod'],
    'field':'MappingCategory','country':'US','protocol':'14.0','added':['BOX15'],'removed':['BOX14'],'sourceUrl':enum_source,
    'referenceConflict':'Current HTML examples differ by BOX15/BOX16; the public SDK features page explicitly corroborates BOX16 at16. BOX15 removal is not inferred from omission in that list.'},
    {'operations':['Form1099CategoryAccountMappingQuery','Form1099CategoryAccountMappingMod'],
    'field':'MappingCategory','country':'US','protocol':'16.0','added':['BOX14','BOX16'],'sourceUrl':feature_source},
    {'operations':['Form1099CategoryAccountMappingQuery','Form1099CategoryAccountMappingMod'],
    'field':'MappingCategory','country':'US','protocol':'17.0','removed':['BOX14'],'sourceUrl':feature_source,
    'behavior':'Official feature page says Form1099-MISC Box14 mappings moved to Form1099-NEC Box3. No automatic company mapping migration is performed by qbcli.'}]
target.write_text(json.dumps(reference,separators=(',',':'),ensure_ascii=False)+'\n',encoding='utf-8')
print(json.dumps({'supplementedFields':len(added)}))
