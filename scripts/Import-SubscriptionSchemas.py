"""Import official subscription schemas omitted from the legacy request index."""
import importlib.util
import json
import pathlib
ROOT=pathlib.Path(__file__).resolve().parent.parent
spec=importlib.util.spec_from_file_location('sdk_import',ROOT/'scripts/Import-SdkReference.py')
module=importlib.util.module_from_spec(spec);spec.loader.exec_module(module)
target=ROOT/'src/QbCli.QuickBooks/Resources/sdk-reference.json'
reference=json.loads(target.read_text(encoding='utf-8'))
names=['DataEventSubscriptionAdd','DataEventSubscriptionQuery','UIEventSubscriptionAdd','UIEventSubscriptionQuery',
       'UIExtensionSubscriptionAdd','UIExtensionSubscriptionQuery','SubscriptionDel']
for name in names:
    request=module.fetch(name+'Rq.json')
    entry={'name':name,**{'minVer'+c:request.get('minVer'+c,request['minVerUS']) if request['supports'].get(c,False) else '0' for c in ('US','CA','UK','AU','OE')}}
    operation=module.operation(entry);operation['transport']='subscription'
    reference['operations']=[o for o in reference['operations'] if o['name']!=name]+[operation]
reference['operations'].sort(key=lambda o:o['name'])
target.write_text(json.dumps(reference,separators=(',',':'),ensure_ascii=False)+'\n',encoding='utf-8')
print(json.dumps({'importedSubscriptionOperations':len(names)}))
