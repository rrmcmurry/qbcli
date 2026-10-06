"""Export only operation/status metadata from local fixtures; never company values."""
import json
import pathlib
ROOT=pathlib.Path(__file__).resolve().parent.parent
DEST=ROOT/'artifacts/fixtures'
records=[]
paths=[DEST/'read-probes-result.json',DEST/'SDK154955-base-result.json',DEST/'SDK154955-transactions-result.json']
paths += [DEST/'subscriptions-read-result.json'] if (DEST/'subscriptions-read-result.json').exists() else []
paths += [DEST/'time-tracking-result.json'] if (DEST/'time-tracking-result.json').exists() else []
paths+=sorted(DEST.glob('lifecycle-*-result.json'))
for path in paths:
    value=json.loads(path.read_text(encoding='utf-8-sig'))
    if value.get('documentType') in ('invoice','bill'):continue # Normalized summaries are not SDK outcome records.
    plan_path=path.with_name(path.name.replace('-result.json','-plan.json'))
    protocol=json.loads(plan_path.read_text(encoding='utf-8-sig')).get('protocolVersion','13.0') if plan_path.exists() else value.get('protocolVersion','13.0')
    for result in value.get('results',[]):
        records.append({'operation':result['operation'],'kind':'read_smoke' if path.name=='read-probes-result.json' else 'fixture_request',
                        'outcome':result['outcome'],'statusCode':result['statusCode'],'protocol':protocol,'scope':value.get('executionScope','company')})
# Preserve failed bulk SDK attempts that stopped before a separate execution
# result was exported. Do not duplicate successfully exported bulk requests.
for path in sorted(DEST.glob('lifecycle-*-bulk-apply.json')):
    value=json.loads(path.read_text(encoding='utf-8'))
    execution=value.get('execution') or {}
    for result in execution.get('results',[]):
        if result['outcome'] in ('failed','warning','unknown'):
            records.append({'operation':result['operation'],'kind':'bulk_failed_attempt','outcome':result['outcome'],
                'statusCode':result['statusCode'],'protocol':value.get('plan',{}).get('protocolVersion','13.0'),'scope':'company'})
output={'tested':'2026-10-05','host':'QuickBooks Accountant Desktop Plus 2024 R21','country':'US','protocols':sorted({r['protocol'] for r in records}),
        'scope':'Request acceptance on a synthetic test company, not exhaustive field, edition, permission or lifecycle verification. Expected duplicate-name failure is included. Company values and identifiers are omitted.',
        'requests':records,'behaviorChecks':['CheckMod line class changed to RentalA with amount/account preserved',
        'CustomerQuery iterator completed 3 pages at page size 1 including inactive records',
        'continueOnError duplicate CustomerAdd failed with 3100 while following ClassAdd succeeded']}
output['privateDefinitionDeletionChecks']=[json.loads(path.read_text(encoding='utf-8')) for path in sorted(DEST.glob('lifecycle-*-private-definition-verification.json'))]
output['privateDefinitionRecreationChecks']=[json.loads(path.read_text(encoding='utf-8')) for path in sorted(DEST.glob('lifecycle-*-private-definition-recreation-verification.json'))]
output['numericPrecisionChecks']=[json.loads(path.read_text(encoding='utf-8')) for path in sorted(DEST.glob('lifecycle-*-numeric-precision-verification.json'))]
output['modifierReadBackChecks']=[check for path in sorted(DEST.glob('lifecycle-*-modifier-verification.json')) for check in json.loads(path.read_text(encoding='utf-8'))]
output['salesPostingIdentityChecks']=[check for path in sorted(DEST.glob('lifecycle-*-sales-posting-verification.json')) for check in json.loads(path.read_text(encoding='utf-8'))]
output['salesClassPreservationChecks']=[check for path in sorted(DEST.glob('lifecycle-*-sales-class-verification.json')) for check in json.loads(path.read_text(encoding='utf-8'))]
output['salesSpecialItemClassChecks']=[check for path in sorted(DEST.glob('lifecycle-*-special-sales-verification.json')) for check in json.loads(path.read_text(encoding='utf-8'))]
output['salesSpecialItemBulkChecks']=[check for path in sorted(DEST.glob('lifecycle-*-special-sales-bulk-verification.json')) for check in json.loads(path.read_text(encoding='utf-8'))]
output['salesPaymentItemClassChecks']=[check for path in sorted(DEST.glob('lifecycle-*-payment-sales-verification.json')) for check in json.loads(path.read_text(encoding='utf-8'))]
output['salesPaymentItemBulkChecks']=[check for path in sorted(DEST.glob('lifecycle-*-payment-sales-bulk-verification.json')) for check in json.loads(path.read_text(encoding='utf-8'))]
output['salesDescriptionClassChecks']=[check for path in sorted(DEST.glob('lifecycle-*-description-sales-verification.json')) for check in json.loads(path.read_text(encoding='utf-8'))]
output['salesDescriptionBulkChecks']=[check for path in sorted(DEST.glob('lifecycle-*-description-sales-bulk-verification.json')) for check in json.loads(path.read_text(encoding='utf-8'))]
output['salesBulkReclassificationChecks']=[json.loads(path.read_text(encoding='utf-8')) for path in sorted(DEST.glob('lifecycle-*-bulk-sales-verification.json'))]
output['inventoryIteratorChecks']=[json.loads(path.read_text(encoding='utf-8')) for path in sorted(DEST.glob('lifecycle-*-inventory-iterator-verification.json'))]
output['inventoryDefaultAccountChecks']=[check for path in sorted(DEST.glob('lifecycle-*-inventory-defaults-verification.json')) for check in json.loads(path.read_text(encoding='utf-8'))]
output['inventoryClassPreservationChecks']=[check for path in sorted(DEST.glob('lifecycle-*-inventory-class-verification.json')) for check in json.loads(path.read_text(encoding='utf-8'))]
output['inventoryBulkReclassificationChecks']=[json.loads(path.read_text(encoding='utf-8')) for path in sorted(DEST.glob('lifecycle-*-bulk-inventory-sales-verification.json'))]
output['additionalSalesItemBulkChecks']=[json.loads(path.read_text(encoding='utf-8')) for path in sorted(DEST.glob('lifecycle-*-additional-sales-verification.json'))]
output['purchaseItemClassPreservationChecks']=[check for path in sorted(DEST.glob('lifecycle-*-purchase-item-verification.json')) for check in json.loads(path.read_text(encoding='utf-8'))]
output['purchaseItemBulkReclassificationChecks']=[check for path in sorted(DEST.glob('lifecycle-*-purchase-item-bulk-verification.json')) for check in json.loads(path.read_text(encoding='utf-8'))]
output['purchaseBillableClassChecks']=[check for path in sorted(DEST.glob('lifecycle-*-purchase-item-billable-verification.json')) for check in json.loads(path.read_text(encoding='utf-8'))]
output['purchaseLinkProbeChecks']=[json.loads(path.read_text(encoding='utf-8')) for path in sorted(DEST.glob('lifecycle-*-purchase-link-probe-verification.json'))]
output['purchaseOrderLinkControlChecks']=[check for path in sorted(DEST.glob('lifecycle-*-po-links-verification.json')) for check in json.loads(path.read_text(encoding='utf-8'))]
output['purchasePostingIdentityChecks']=[json.loads(path.read_text(encoding='utf-8')) for path in sorted(DEST.glob('lifecycle-*-purchase-posting-identity-verification.json'))]
output['purchaseInventoryClassChecks']=[check for path in sorted(DEST.glob('lifecycle-*-purchase-item-inventory-verification.json')) for check in json.loads(path.read_text(encoding='utf-8'))]
output['purchaseInventoryBulkChecks']=[check for path in sorted(DEST.glob('lifecycle-*-purchase-item-inventory-bulk-verification.json')) for check in json.loads(path.read_text(encoding='utf-8'))]
output['purchaseInventoryIteratorChecks']=[json.loads(path.read_text(encoding='utf-8')) for path in sorted(DEST.glob('lifecycle-*-purchase-inventory-iterator-verification.json'))]
output['documentSummaryChecks']=[check for path in sorted(DEST.glob('lifecycle-*-document-reads-verification.json')) for check in json.loads(path.read_text(encoding='utf-8'))]
output['partialDocumentPaymentChecks']=[check for path in sorted(DEST.glob('lifecycle-*-partial-documents-verification.json')) for check in json.loads(path.read_text(encoding='utf-8'))]
output['creditDiscountPaymentChecks']=[check for path in sorted(DEST.glob('lifecycle-*-credit-discounts-verification.json')) for check in json.loads(path.read_text(encoding='utf-8'))]
output['creditOnlyApplicationChecks']=[check for path in sorted(DEST.glob('lifecycle-*-credit-only-verification.json')) for check in json.loads(path.read_text(encoding='utf-8'))]
output['paymentDistributionModifierChecks']=[check for path in sorted(DEST.glob('lifecycle-*-payment-distribution-verification.json')) for check in json.loads(path.read_text(encoding='utf-8'))]
output['paymentReallocationChecks']=[check for path in sorted(DEST.glob('lifecycle-*-payment-reallocation-verification.json')) for check in json.loads(path.read_text(encoding='utf-8'))]
output['additionalCreditModifierChecks']=[check for path in sorted(DEST.glob('lifecycle-*-additional-credit-mods-verification.json')) for check in json.loads(path.read_text(encoding='utf-8'))]
output['appliedPaymentCreditDeletionChecks']=[check for path in sorted(DEST.glob('lifecycle-*-applied-deletion-verification.json')) for check in json.loads(path.read_text(encoding='utf-8'))]
output['appliedPaymentCreditVoidChecks']=[check for path in sorted(DEST.glob('lifecycle-*-applied-void-verification.json')) for check in json.loads(path.read_text(encoding='utf-8'))]
output['reportNormalizationChecks']=[check for path in sorted(DEST.glob('lifecycle-*-report-normalization-verification.json')) for check in json.loads(path.read_text(encoding='utf-8'))]
output['transactionNormalizationChecks']=[check for path in sorted(DEST.glob('lifecycle-*-transaction-normalization-verification.json')) for check in json.loads(path.read_text(encoding='utf-8'))]
output['reportVariantChecks']=[check for path in sorted(DEST.glob('lifecycle-*-report-variant-coverage.json')) for check in json.loads(path.read_text(encoding='utf-8'))]
output['bulkReclassificationChecks']=[json.loads(path.read_text(encoding='utf-8')) for path in sorted(DEST.glob('lifecycle-*-bulk-verification.json'))]
output['journalResumeChecks']=[json.loads(path.read_text(encoding='utf-8')) for path in sorted(DEST.glob('lifecycle-*-resume-verification.json'))]
output['journalProcessChecks']=[json.loads(path.read_text(encoding='utf-8')) for path in sorted(DEST.glob('lifecycle-*-resume-process-verification.json'))]
output['specialAccountChecks']=[check for path in sorted(DEST.glob('lifecycle-*-special-account-coverage.json')) for check in json.loads(path.read_text(encoding='utf-8'))]
output['callbackHostChecks']=[json.loads(path.read_text(encoding='utf-8')) for path in sorted(DEST.glob('lifecycle-*-callback-verification.json'))]
output['persistentCallbackChecks']=[json.loads(path.read_text(encoding='utf-8')) for path in sorted(DEST.glob('lifecycle-*-auto-callback-verification.json'))]
output['quickBooksEventChecks']=[json.loads(path.read_text(encoding='utf-8')) for path in sorted(DEST.glob('lifecycle-*-qb-events-verification.json'))]
output['mappingEnumChecks']=[check for path in sorted(DEST.glob('lifecycle-*-mapping-enum-verification.json')) for check in json.loads(path.read_text(encoding='utf-8'))]
output['batchByteBoundChecks']=[check for path in sorted(DEST.glob('lifecycle-*-byte-bound-verification.json')) for check in json.loads(path.read_text(encoding='utf-8'))]
output['leadLifecycleChecks']=[check for path in sorted(DEST.glob('lifecycle-*-lead-verification.json')) for check in json.loads(path.read_text(encoding='utf-8'))]
output['featureDiscoveryChecks']=[json.loads(path.read_text(encoding='utf-8')) for path in sorted(DEST.glob('lifecycle-*-feature-discovery-verification.json'))]
output['macroChecks']=[check for path in sorted(DEST.glob('lifecycle-*-macro-verification.json')) for check in json.loads(path.read_text(encoding='utf-8'))]
output['eventRecoveryChecks']=[json.loads(path.read_text(encoding='utf-8')) for path in sorted(DEST.glob('lifecycle-*-event-recovery-verification.json'))]
output['lineMacroChecks']=[check for path in sorted(DEST.glob('lifecycle-*-line-macro-verification.json')) for check in json.loads(path.read_text(encoding='utf-8'))]
output['transactionIterationChecks']=[json.loads(path.read_text(encoding='utf-8')) for path in sorted(DEST.glob('lifecycle-*-transaction-iteration-verification.json'))]
if (DEST/'batch-serialization-benchmark.json').exists():
    output['batchSerializationBenchmark']=json.loads((DEST/'batch-serialization-benchmark.json').read_text(encoding='utf-8'))
(ROOT/'src/QbCli/Resources/live-coverage.json').write_text(json.dumps(output,indent=2),encoding='utf-8')
print(json.dumps({'recordedRequests':len(records),'operationsAccepted':len({r['operation'] for r in records if r['outcome'] in ('succeeded','no_match')})}))
