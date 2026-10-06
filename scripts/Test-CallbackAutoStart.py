"""Synthetic COM auto-start verification. No QuickBooks session/subscription.

Creates one fresh test-owned HKCU CLSID, invokes the documented callback ABI,
waits on each recorded helper process handle, and removes its registration.
"""
import ctypes
import datetime
import hashlib
import json
import pathlib
import subprocess
import time
import uuid
import winreg
import argparse

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--manual-debug', action='store_true', help='Diagnostic only: start the helper explicitly; never claims COM auto-activation.')
parser.add_argument('--mirror-debug', action='store_true', help='Diagnostic only: mirror this fresh owned registration into the32-bit user view and remove it afterward.')
parser.add_argument('--activation-context',choices=['local','local-64','all-local'],default='local-64',help='Documented CLSCTX flags for diagnostic comparison; registration is unchanged.')
OPTIONS = parser.parse_args()

ROOT = pathlib.Path(__file__).resolve().parent.parent
COMPANY = (ROOT / 'QBData/Test.QBW').resolve()
assert COMPANY == pathlib.Path(__import__("os").environ["QBCLI_LIVE_TEST_COMPANY"]).resolve(strict=True)
EXE = ROOT / 'artifacts/win-x64/qbcli.exe'
DEST = ROOT / 'artifacts/fixtures'
STAMP = datetime.datetime.now().strftime('%H%M%S')
CLSID = uuid.uuid4()
IDENTITY = '{' + str(CLSID) + '}'
DIRECTORY = DEST / f'lifecycle-{STAMP}-auto-callback-journals'
DIRECTORY.mkdir()
SETTINGS = DEST / f'lifecycle-{STAMP}-auto-callback-settings.json'
SETTINGS.write_text(json.dumps({'callbackClsid': IDENTITY, 'durationSeconds': 7, 'journalDirectory': str(DIRECTORY)}), encoding='utf-8')
INPUT_ID = DEST / f'lifecycle-{STAMP}-auto-callback-identity.json'
INPUT_ID.write_text(json.dumps({'callbackClsid': IDENTITY}), encoding='utf-8')

def cli(label, command, input_path, apply=False):
    args = [str(EXE), 'events', command, '--input', str(input_path), '--company-file', str(COMPANY)]
    if apply:
        args.append('--apply')
    result = subprocess.run(args, capture_output=True, text=True, encoding='utf-8')
    (DEST / f'lifecycle-{STAMP}-auto-callback-{label}.json').write_text(result.stdout, encoding='utf-8')
    return result.returncode, json.loads(result.stdout)

class GUID(ctypes.Structure):
    _fields_ = [('data1', ctypes.c_uint32), ('data2', ctypes.c_uint16), ('data3', ctypes.c_uint16), ('data4', ctypes.c_ubyte * 8)]

ole = ctypes.OleDLL('ole32')
ole.CoInitializeEx.argtypes = [ctypes.c_void_p, ctypes.c_uint32]
ole.CoCreateInstance.argtypes = [ctypes.POINTER(GUID), ctypes.c_void_p, ctypes.c_uint32, ctypes.POINTER(GUID), ctypes.POINTER(ctypes.c_void_p)]
ole.CoUninitialize.restype = None
automation = ctypes.WinDLL('oleaut32')
automation.SysAllocString.argtypes = [ctypes.c_wchar_p]
automation.SysAllocString.restype = ctypes.c_void_p
automation.SysFreeString.argtypes = [ctypes.c_void_p]
kernel = ctypes.WinDLL('kernel32', use_last_error=True)
kernel.OpenProcess.argtypes = [ctypes.c_uint32, ctypes.c_int, ctypes.c_uint32]
kernel.OpenProcess.restype = ctypes.c_void_p
kernel.WaitForSingleObject.argtypes = [ctypes.c_void_p, ctypes.c_uint32]
kernel.WaitForSingleObject.restype = ctypes.c_uint32
kernel.CloseHandle.argtypes = [ctypes.c_void_p]

code, preview = cli('preview', 'register', SETTINGS)
assert code == 0 and not preview['submitted']
try:
    winreg.OpenKey(winreg.HKEY_CURRENT_USER, 'Software\\Classes\\CLSID\\' + IDENTITY).Close()
    raise AssertionError('Preview created a registration')
except FileNotFoundError:
    pass
code, registered = cli('register', 'register', SETTINGS, True)
assert code == 0 and registered['submitted'] and registered['persistentRegistration']
if OPTIONS.mirror_debug:
    path = 'Software\\Classes\\CLSID\\' + IDENTITY
    def copy_key(suffix):
        with winreg.OpenKey(winreg.HKEY_CURRENT_USER, path + suffix, 0, winreg.KEY_READ | winreg.KEY_WOW64_64KEY) as source:
            with winreg.CreateKeyEx(winreg.HKEY_CURRENT_USER, path + suffix, 0, winreg.KEY_WRITE | winreg.KEY_WOW64_32KEY) as target:
                i = 0
                while True:
                    try:
                        name, value, kind = winreg.EnumValue(source, i)
                    except OSError:
                        break
                    winreg.SetValueEx(target, name, 0, kind, value)
                    i += 1
    copy_key('')
    copy_key('\\LocalServer32')
print(json.dumps({'clientPointerBits': ctypes.sizeof(ctypes.c_void_p) * 8}), flush=True)
for view in [winreg.KEY_WOW64_64KEY, winreg.KEY_WOW64_32KEY]:
    try:
        with winreg.OpenKey(winreg.HKEY_CLASSES_ROOT, 'CLSID\\' + IDENTITY + '\\LocalServer32', 0, winreg.KEY_READ | view) as registered_server:
            value = winreg.QueryValueEx(registered_server, '')[0]
            print(json.dumps({'registryView': view, 'mergedServerPresent': True, 'commandMatchesExpected': '--callback-clsid ' + IDENTITY in value}), flush=True)
    except FileNotFoundError:
        print(json.dumps({'registryView': view, 'mergedServerPresent': False}), flush=True)
finished_handles = []
completed_activations = 0
activation_failure = None
ole.CoInitializeEx(None, 2)
try:
    for iteration in range(2):
        before = set(DIRECTORY.glob('*.jsonl'))
        manual_process = None
        if OPTIONS.manual_debug:
            manual_process = subprocess.Popen([str(EXE.with_name('qbcli-callback.exe')), '--callback-clsid', IDENTITY, '-Embedding'], creationflags=subprocess.CREATE_NO_WINDOW)
            deadline = time.monotonic() + 5
            while time.monotonic() < deadline:
                if manual_process.poll() is not None:
                    raise RuntimeError('Helper exited before readiness: ' + str(manual_process.returncode))
                if any('listener_ready' in p.read_text(encoding='utf-8') for p in set(DIRECTORY.glob('*.jsonl')) - before):
                    break
                time.sleep(.05)
            else:
                raise RuntimeError('Explicitly started helper did not become ready')
        clsid = GUID.from_buffer_copy(CLSID.bytes_le)
        iid = GUID.from_buffer_copy(uuid.UUID('00000000-0000-0000-c000-000000000046').bytes_le)
        pointer = ctypes.c_void_p()
        # Do not start a helper process ourselves: COM must activate LocalServer32.
        try:
            context={'local':4,'local-64':4 | 0x80000,'all-local':1 | 2 | 4}[OPTIONS.activation_context]
            ole.CoCreateInstance(ctypes.byref(clsid), None, context, ctypes.byref(iid), ctypes.byref(pointer))
        except OSError as error:
            activation_failure = f'0x{error.winerror & 0xffffffff:08X}'
            # A directly launched helper still owns its registration until exit.
            if manual_process is not None:
                manual_process.wait(timeout=20)
            break
        base_table = ctypes.cast(pointer, ctypes.POINTER(ctypes.POINTER(ctypes.c_void_p))).contents
        query_interface = ctypes.WINFUNCTYPE(ctypes.c_long, ctypes.c_void_p, ctypes.POINTER(GUID), ctypes.POINTER(ctypes.c_void_p))(base_table[0])
        base_release = ctypes.WINFUNCTYPE(ctypes.c_uint32, ctypes.c_void_p)(base_table[2])
        callback_iid = GUID.from_buffer_copy(uuid.UUID('a3a4e33e-e747-48cf-947b-93fde67ff72f').bytes_le)
        callback_pointer = ctypes.c_void_p()
        try:
            assert query_interface(pointer, ctypes.byref(callback_iid), ctypes.byref(callback_pointer)) == 0, 'Callback interface marshaling failed'
        finally:
            base_release(pointer)
        pointer = callback_pointer
        vtable = ctypes.cast(pointer, ctypes.POINTER(ctypes.POINTER(ctypes.c_void_p))).contents
        inform = ctypes.WINFUNCTYPE(ctypes.c_long, ctypes.c_void_p, ctypes.c_void_p)(vtable[3])
        release = ctypes.WINFUNCTYPE(ctypes.c_uint32, ctypes.c_void_p)(vtable[2])
        try:
            for path in [COMPANY, ROOT / 'QBData/OtherNotAuthorized.QBW']:
                xml = f'<QBXML><QBXMLEvents><DataEvent><CompanyFilePath>{path}</CompanyFilePath><ListEvent><ListEventType>Class</ListEventType><ListEventOperation>Add</ListEventOperation><ListID>Synthetic</ListID></ListEvent></DataEvent></QBXMLEvents></QBXML>'
                value = automation.SysAllocString(xml)
                try:
                    assert inform(pointer, value) == 0
                finally:
                    automation.SysFreeString(value)
        finally:
            release(pointer)
        new = set(DIRECTORY.glob('*.jsonl')) - before
        assert len(new) == 1, 'Activation did not create one new journal'
        journal = new.pop()
        records = [json.loads(line) for line in journal.read_text(encoding='utf-8').splitlines()]
        pid = records[0]['processId']
        process_handle = kernel.OpenProcess(0x100000, False, pid)  # SYNCHRONIZE only
        assert process_handle, ('Cannot observe helper process', ctypes.get_last_error())
        finished_handles.append(process_handle)
        active_code, active_cleanup = cli(f'active-{iteration}', 'cleanup', INPUT_ID, True)
        assert active_code == 4 and active_cleanup['error']['code'] == 'callback_identity_in_use'
        assert kernel.WaitForSingleObject(process_handle, 20000) == 0, 'Helper has not exited; do not reactivate or remove its registration'
        records = [json.loads(line) for line in journal.read_text(encoding='utf-8').splitlines()]
        assert records[-1]['kind'] == 'listener_finished'
        assert records[-1]['received'] == 1 and records[-1]['ignored'] == 1 and records[-1]['failed'] == 0
        assert sum(r['kind'] == 'event' for r in records) == 1
        assert 'OtherNotAuthorized' not in journal.read_text(encoding='utf-8')
        with winreg.OpenKey(winreg.HKEY_CURRENT_USER, 'Software\\Classes\\CLSID\\' + IDENTITY):
            pass  # Registration persists after the bounded helper exits.
        print(json.dumps({'activation': iteration + 1, 'helperProcessExited': True, 'received': 1, 'ignored': 1}), flush=True)
        completed_activations += 1
finally:
    ole.CoUninitialize()
    for handle in finished_handles:
        kernel.CloseHandle(handle)
    code, cleanup = cli('cleanup', 'cleanup', INPUT_ID, True)
    assert code == 0 and cleanup['removed'], 'Owned registration cleanup incomplete; retained identity input identifies it'
    if OPTIONS.mirror_debug:
        path = 'Software\\Classes\\CLSID\\' + IDENTITY
        winreg.DeleteKeyEx(winreg.HKEY_CURRENT_USER, path + '\\LocalServer32', winreg.KEY_WOW64_32KEY)
        winreg.DeleteKeyEx(winreg.HKEY_CURRENT_USER, path, winreg.KEY_WOW64_32KEY)

verified = completed_activations == 2 and activation_failure is None
metadata = {'syntheticComAutoActivation': verified and not OPTIONS.manual_debug, 'autoActivationAttempted': not OPTIONS.manual_debug,
    'activationContext': OPTIONS.activation_context, 'clientPointerBits':ctypes.sizeof(ctypes.c_void_p)*8,
    'serverExecutableSha256':hashlib.sha256(EXE.with_name('qbcli-callback.exe').read_bytes()).hexdigest(),
    'activationFailureHresult': activation_failure, 'manualDiagnosticStart': OPTIONS.manual_debug,
    'independentActivations': completed_activations, 'freshJournalPerActivation': verified,
    'experimental32BitViewMirror': OPTIONS.mirror_debug,
    'officialCallbackInterfaceMarshaled': verified, 'exactCompanyFilter': verified, 'durableAcknowledgement': verified,
    'liveCleanupRefused': verified, 'persistentRegistrationSurvivedReceiverExit': verified, 'ownedRegistrationRemoved': True,
    'quickBooksSubscriptionOrDeliverAlwaysDeliveryTested': False,
    'scope': 'Synthetic calls through COM activation of a fresh owned CLSID; no QuickBooks requests or subscriptions.'}
(DEST / f'lifecycle-{STAMP}-auto-callback-verification.json').write_text(json.dumps(metadata, indent=2), encoding='utf-8')
print(json.dumps(metadata), flush=True)
if not verified:
    raise SystemExit(1)
