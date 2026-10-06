"""Additional live SDK fixtures, exclusively on the expendable Test.QBW."""
import datetime
import json
import pathlib
import subprocess
ROOT=pathlib.Path(__file__).resolve().parent.parent
COMPANY=ROOT/'QBData/Test.QBW'
EXE=ROOT/'artifacts/win-x64/qbcli.exe'
DEST=ROOT/'artifacts/fixtures'
STAMP=datetime.datetime.now().strftime('%H%M%S')
PREFIX='EXT'+STAMP
BASE='SDK154955'
calls=[]
def ref(suffix):return {'FullName':BASE+' '+suffix}
def newref(suffix):return {'FullName':PREFIX+' '+suffix}
def add(operation,body):calls.append({'requestId':str(len(calls)+1),'operation':operation,'fields':{operation:body}})
def run(label):
    plan=DEST/f'lifecycle-{STAMP}-{label}-plan.json'
    plan.write_text(json.dumps({'protocolVersion':'13.0','country':'US','onError':'continueOnError','requests':calls}),encoding='utf-8')
    result=subprocess.run([str(EXE),'sdk','batch','--input',str(plan),'--company-file',str(COMPANY),'--apply','--batch-size','100'],capture_output=True,text=True,encoding='utf-8')
    (DEST/f'lifecycle-{STAMP}-{label}-result.json').write_text(result.stdout,encoding='utf-8')
    value=json.loads(result.stdout)
    if 'error' in value:raise RuntimeError(value['error'])
    print(json.dumps({'label':label,'prefix':PREFIX,'results':[{k:r.get(k) for k in ('operation','outcome','statusCode','statusMessage')} for r in value['results']]}),flush=True)
    calls.clear()
    return value['results']

add('AccountAdd',{'Name':PREFIX+' Bank2','AccountType':'Bank'})
add('AccountAdd',{'Name':PREFIX+' FixedAsset','AccountType':'FixedAsset'})
add('OtherNameAdd',{'Name':PREFIX+' Other'})
add('VehicleAdd',{'Name':PREFIX+' Vehicle','Desc':'Fictional SDK vehicle'})
add('DateDrivenTermsAdd',{'Name':PREFIX+' Day15','DayOfMonthDue':15})
add('SalesRepAdd',{'Initial':STAMP[:5],'SalesRepEntityRef':{'FullName':'SDK '+BASE}})
add('ToDoAdd',{'Notes':PREFIX+' SDK task','Type':'Task','Priority':'Low'})
add('PriceLevelAdd',{'Name':PREFIX+' Price','PriceLevelFixedPercentage':'5.00'})
add('BillingRateAdd',{'Name':PREFIX+' Rate','FixedBillingRate':'25.00'})
add('LeadAdd',{'FullName':PREFIX+' Lead','Status':'Warm'})
add('UnitOfMeasureSetAdd',{'Name':PREFIX+' Count','UnitOfMeasureType':'Count','BaseUnit':{'Name':'Each','Abbreviation':'ea'}})
add('ItemGroupAdd',{'Name':PREFIX+' Group','ItemGroupLine':{'ItemRef':ref('Service'),'Quantity':'1'}})
add('ItemInventoryAdd',{'Name':PREFIX+' Inventory','IncomeAccountRef':ref('Income'),'COGSAccountRef':ref('COGS'),'AssetAccountRef':ref('Inventory'),
    'SalesPrice':'25.00','PurchaseCost':'10.00','QuantityOnHand':'10','TotalValue':'100.00','InventoryDate':'2026-10-05'})
add('ItemFixedAssetAdd',{'Name':PREFIX+' FixedItem','AcquiredAs':'New','PurchaseDesc':'Fictional fixture asset','PurchaseDate':'2026-10-05','PurchaseCost':'100.00','AssetAccountRef':newref('FixedAsset')})
add('ItemSalesTaxAdd',{'Name':PREFIX+' Tax','TaxRate':'5.00','TaxVendorRef':ref('Vendor')})
add('SalesTaxCodeAdd',{'Name':'T'+STAMP[:2],'IsTaxable':True,'Desc':PREFIX+' test tax code'})
add('PayrollItemWageAdd',{'Name':PREFIX+' Wage','WageType':'HourlyRegular','ExpenseAccountRef':ref('Expense')})
owner='{F6A6DC28-4823-4CEE-B497-8B20CE70F34B}'
add('DataExtDefAdd',{'OwnerID':owner,'DataExtName':PREFIX+' Private','DataExtType':'STR255TYPE','AssignToObject':'Customer'})
base=run('additional-base')
accepted={r['operation'] for r in base if r['outcome']=='succeeded'}
add('ChargeAdd',{'CustomerRef':ref('Customer'),'ARAccountRef':ref('AR'),'ItemRef':ref('Service'),'Quantity':'1','Rate':'8.00'})
add('ItemReceiptAdd',{'VendorRef':ref('Vendor'),'APAccountRef':ref('AP'),'ExpenseLineAdd':{'AccountRef':ref('Expense'),'Amount':'4.00'}})
add('SalesOrderAdd',{'CustomerRef':ref('Customer'),'SalesOrderLineAdd':{'ItemRef':ref('Service'),'Quantity':'1','Rate':'25.00'}})
if 'AccountAdd' in accepted:
    add('TransferAdd',{'TransferFromAccountRef':ref('Bank'),'TransferToAccountRef':newref('Bank2'),'Amount':'2.00'})
if 'VehicleAdd' in accepted:
    add('VehicleMileageAdd',{'VehicleRef':newref('Vehicle'),'TripStartDate':'2026-10-05','TripEndDate':'2026-10-05','TotalMiles':'5.00','BillableStatus':'NotBillable'})
if 'DataExtDefAdd' in accepted:
    add('DataExtAdd',{'OwnerID':owner,'DataExtName':PREFIX+' Private','ListDataExtType':'Customer','ListObjRef':ref('Customer'),'DataExtValue':'Synthetic fixture value'})
if 'ItemInventoryAdd' in accepted:
    add('ItemInventoryAssemblyAdd',{'Name':PREFIX+' Assembly','IncomeAccountRef':ref('Income'),'COGSAccountRef':ref('COGS'),'AssetAccountRef':ref('Inventory'),
        'ItemInventoryAssemblyLine':{'ItemInventoryRef':newref('Inventory'),'Quantity':'1'}})
    add('InventoryAdjustmentAdd',{'AccountRef':ref('Expense'),'InventoryAdjustmentLineAdd':{'ItemRef':newref('Inventory'),'QuantityAdjustment':{'QuantityDifference':'1'}}})
transaction_results=json.loads((DEST/(BASE+'-transactions-result.json')).read_text(encoding='utf-8'))
bill=next(r['data'][0] for r in transaction_results['results'] if r['operation']=='BillAdd')
bill_id=next(c['value'] for c in bill['children'] if c['name']=='TxnID')
add('BillPaymentCreditCardAdd',{'PayeeEntityRef':ref('Vendor'),'APAccountRef':ref('AP'),'CreditCardAccountRef':ref('Card'),'AppliedToTxnAdd':{'TxnID':bill_id,'PaymentAmount':'1.00'}})
add('TimeTrackingAdd',{'TxnDate':'2026-10-05','EntityRef':{'FullName':'SDK '+BASE},'Duration':'PT1H','BillableStatus':'NotBillable'})
tx=run('additional-transactions')
if any(r['operation']=='ItemInventoryAssemblyAdd' and r['outcome']=='succeeded' for r in tx):
    add('BuildAssemblyAdd',{'ItemInventoryAssemblyRef':newref('Assembly'),'QuantityToBuild':'1'})
    run('additional-assembly')
