"""Verify retained mapping read controls; no SDK calls or company writes."""
import json
import pathlib
import sys
root=pathlib.Path(__file__).resolve().parent.parent;dest=root/'artifacts/fixtures'
stamp=sys.argv[1];assert len(stamp)==6 and stamp.isdigit()
company=(root/'QBData/Test.QBW').resolve()
assert company==pathlib.Path(__import__("os").environ["QBCLI_LIVE_TEST_COMPANY"]).resolve(strict=True)
checks=[]
for protocol in ('14.0','16.0','17.0'):
    prefix=dest/f'lifecycle-{stamp}-mapping-{protocol}'
    plan=json.loads(pathlib.Path(str(prefix)+'-plan.json').read_text(encoding='utf-8'))
    result=json.loads(pathlib.Path(str(prefix)+'-result.json').read_text(encoding='utf-8'))
    assert plan['protocolVersion']==protocol and plan['country']=='US'
    assert pathlib.Path(result['companyFile'])==company and not result['mutatesCompany']
    assert len(result['results'])==len(plan['requests']) and len({r['requestId'] for r in result['results']})==len(plan['requests'])
    rows={r['requestId']:r for r in result['results']}
    for request in plan['requests']:
        assert request['operation']=='Form1099CategoryAccountMappingQuery'
        fields=request['fields'];form=fields['FormType'];category=fields.get('MappingCategory','ALL')
        assert form in ('MISC','NEC') and not (protocol=='17.0' and category=='BOX14')
        row=rows[request['requestId']];assert row['operation']==request['operation'] and row['outcome'] in ('succeeded','failed','no_match')
        expected=1000 if category=='BOX15' or category=='BOX16' and form=='NEC' else 3110 if category=='BOX14' else 0
        assert row['statusCode']==expected and row['outcome']==('failed' if expected else 'succeeded')
        checks.append({'protocol':protocol,'formType':form,'enumValue':category,'operation':row['operation'],
            'outcome':row['outcome'],'statusCode':row['statusCode'],'independentRetainedEvidenceVerification':True,
            'scope':'Saved read plans/results on Test.QBW QBD2024R21 US; request correlation and exact company/protocol checked without replay. BOX16 accepted for MISC at16/17 but rejected for NEC1000; BOX14 at16 rejected3110 on this host despite documented historical schema support. No modification/migration/tax filing or other releases proof.'})
assert len(checks)==20
(dest/f'lifecycle-{stamp}-mapping-enum-verification.json').write_text(json.dumps(checks,indent=2),encoding='utf-8')
print(json.dumps({'independentReadControlsVerified':len(checks),'box16MiscAcceptedProtocols':['16.0','17.0'],'companyMutations':0}))
