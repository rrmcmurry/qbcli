"""Cross-process COM callback probe using the official installed interface ABI.
This sends synthetic notifications; it does not create a QuickBooks subscription.
"""
import ctypes
import datetime
import json
import pathlib
import subprocess
import time
import uuid
ROOT=pathlib.Path(__file__).resolve().parent.parent
COMPANY=(ROOT/'QBData/Test.QBW').resolve()
assert COMPANY==pathlib.Path(__import__("os").environ["QBCLI_LIVE_TEST_COMPANY"]).resolve(strict=True)
DEST=ROOT/'artifacts/fixtures'
STAMP=datetime.datetime.now().strftime('%H%M%S')
CLSID=uuid.uuid4()
settings=DEST/f'lifecycle-{STAMP}-callback-settings.json'
settings.write_text(json.dumps({'callbackClsid':'{'+str(CLSID)+'}','durationSeconds':12}),encoding='utf-8')
journal=DEST/f'lifecycle-{STAMP}-callback-events.jsonl'
output=DEST/f'lifecycle-{STAMP}-callback-result.json'
stderr=DEST/f'lifecycle-{STAMP}-callback-stderr.txt'
with output.open('w',encoding='utf-8') as stdout,stderr.open('w',encoding='utf-8') as errors:
    process=subprocess.Popen([str(ROOT/'artifacts/win-x64/qbcli.exe'),'events','listen','--input',str(settings),'--company-file',str(COMPANY),'--journal',str(journal)],stdout=stdout,stderr=errors,creationflags=subprocess.CREATE_NO_WINDOW)
    deadline=time.monotonic()+6
    while time.monotonic()<deadline:
        if process.poll() is not None:raise RuntimeError('Listener exited before ready: '+output.read_text())
        if journal.exists() and any(json.loads(line).get('kind')=='listener_ready' for line in journal.read_text().splitlines()):break
        time.sleep(.05)
    else:raise RuntimeError('Listener did not register in time')
    class GUID(ctypes.Structure):
        _fields_=[('data1',ctypes.c_uint32),('data2',ctypes.c_uint16),('data3',ctypes.c_uint16),('data4',ctypes.c_ubyte*8)]
    guid=lambda value:GUID.from_buffer_copy(value.bytes_le)
    ole=ctypes.OleDLL('ole32')
    ole.CoInitializeEx.argtypes=[ctypes.c_void_p,ctypes.c_uint32]
    ole.CoInitializeEx(None,2)
    try:
        clsid=guid(CLSID);iid=guid(uuid.UUID('a3a4e33e-e747-48cf-947b-93fde67ff72f'));pointer=ctypes.c_void_p()
        ole.CoCreateInstance.argtypes=[ctypes.POINTER(GUID),ctypes.c_void_p,ctypes.c_uint32,ctypes.POINTER(GUID),ctypes.POINTER(ctypes.c_void_p)]
        ole.CoCreateInstance(ctypes.byref(clsid),None,4,ctypes.byref(iid),ctypes.byref(pointer))
        vtable=ctypes.cast(pointer,ctypes.POINTER(ctypes.POINTER(ctypes.c_void_p))).contents
        inform=ctypes.WINFUNCTYPE(ctypes.c_long,ctypes.c_void_p,ctypes.c_void_p)(vtable[3])
        release=ctypes.WINFUNCTYPE(ctypes.c_uint32,ctypes.c_void_p)(vtable[2])
        automation=ctypes.WinDLL('oleaut32');automation.SysAllocString.argtypes=[ctypes.c_wchar_p];automation.SysAllocString.restype=ctypes.c_void_p;automation.SysFreeString.argtypes=[ctypes.c_void_p];automation.SysFreeString.restype=None
        def send(path):
            message=f'<QBXML><QBXMLEvents><DataEvent><CompanyFilePath>{path}</CompanyFilePath><HostInfo><ProductName>Synthetic</ProductName><MajorVersion>34</MajorVersion><MinorVersion>0</MinorVersion><Country>US</Country></HostInfo><ListEvent><ListEventType>Class</ListEventType><ListEventOperation>Add</ListEventOperation><ListID>Synthetic</ListID></ListEvent></DataEvent></QBXMLEvents></QBXML>'
            bstr=automation.SysAllocString(message)
            try:assert inform(pointer,bstr)==0
            finally:automation.SysFreeString(bstr)
        try:
            send(str(COMPANY));send(str(ROOT/'QBData/OtherNotAuthorized.QBW'))
        finally:release(pointer)
    finally:
        ole.CoUninitialize.restype=None
        ole.CoUninitialize()
    process.wait(timeout=20)
result=json.loads(output.read_text())
assert process.returncode==0 and result['success'],result
assert result['receivedEvents']==1 and result['ignoredEvents']==1 and result['failedCallbacks']==0
records=[json.loads(line) for line in journal.read_text().splitlines()]
assert len([r for r in records if r['kind']=='event'])==1
assert 'OtherNotAuthorized' not in journal.read_text()
metadata={'crossProcessComActivation':True,'officialCallbackInterfaceMarshaled':True,'exactCompanyFilter':True,'durableNotificationRecorded':True,'persistentRegistrationCreated':False,'quickBooksSubscriptionOrDeliveryTested':False}
(DEST/f'lifecycle-{STAMP}-callback-verification.json').write_text(json.dumps(metadata,indent=2),encoding='utf-8')
print(json.dumps(metadata),flush=True)
