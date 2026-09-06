"""Java 8 + actual Forge SecurityManager + empty-probe/reaccept Windows pipe regression."""
from pathlib import Path
import ctypes, hashlib, json, os, subprocess, threading, uuid
from ctypes import wintypes

root = Path(__file__).resolve().parents[3]
work = root / '.local/join-agent/security-check'
work.mkdir(parents=True, exist_ok=True)
jar = root / 'src/GameIntegration/join/mojin-join-agent.jar'
forge = next((root / '.local/loading-live-20260906/instances/vw/libraries/net/minecraftforge/forge').glob('*/*.jar'))
java = json.loads((root / '.local/loading-live-20260906/prepared.json').read_text(encoding='utf-8'))['vw']['java']
javac = next((root.parent / '.tools/temurin25').glob('*/bin/javac.exe'))
cp = os.pathsep.join(map(str, [jar, forge, work]))
subprocess.run([str(javac), '--release', '8', '-cp', cp, '-d', str(work), str(Path(__file__).with_name('NamedPipeSecurityCheck.java'))], check=True, capture_output=True, creationflags=subprocess.CREATE_NO_WINDOW)

k = ctypes.WinDLL('kernel32', use_last_error=True)
k.CreateNamedPipeW.restype = wintypes.HANDLE
k.CreateNamedPipeW.argtypes = [wintypes.LPCWSTR, wintypes.DWORD, wintypes.DWORD, wintypes.DWORD, wintypes.DWORD, wintypes.DWORD, wintypes.DWORD, ctypes.c_void_p]
k.ConnectNamedPipe.argtypes = [wintypes.HANDLE, ctypes.c_void_p]
k.ReadFile.argtypes = [wintypes.HANDLE, ctypes.c_void_p, wintypes.DWORD, ctypes.POINTER(wintypes.DWORD), ctypes.c_void_p]
k.WriteFile.argtypes = k.ReadFile.argtypes
k.FlushFileBuffers.argtypes = [wintypes.HANDLE]
k.DisconnectNamedPipe.argtypes = [wintypes.HANDLE]
k.CloseHandle.argtypes = [wintypes.HANDLE]
pipe = 'mojin-join-' + uuid.uuid4().hex
state = {'emptyProbes': 0, 'requests': 0}
errors = []

def serve():
    try:
        while state['requests'] == 0:
            handle = k.CreateNamedPipeW(rf'\\.\pipe\{pipe}', 3, 0, 1, 4096, 4096, 0, None)
            if handle == wintypes.HANDLE(-1).value: raise ctypes.WinError(ctypes.get_last_error())
            try:
                if not k.ConnectNamedPipe(handle, None) and ctypes.get_last_error() != 535: raise ctypes.WinError(ctypes.get_last_error())
                buffer = ctypes.create_string_buffer(4096)
                count = wintypes.DWORD()
                ok = k.ReadFile(handle, buffer, 4096, ctypes.byref(count), None)
                if not ok and ctypes.get_last_error() not in (109, 232): raise ctypes.WinError(ctypes.get_last_error())
                if count.value == 0:
                    state['emptyProbes'] += 1
                    continue
                assert json.loads(buffer.raw[:count.value]) == {'instance': 'vw'}
                state['requests'] += 1
                reply = (json.dumps({'ticket': 'A' * 43}) + '\n').encode()
                if not k.WriteFile(handle, reply, len(reply), ctypes.byref(count), None): raise ctypes.WinError(ctypes.get_last_error())
                k.FlushFileBuffers(handle)
            finally:
                k.DisconnectNamedPipe(handle)
                k.CloseHandle(handle)
    except Exception as error:
        errors.append(type(error).__name__)

thread = threading.Thread(target=serve, daemon=True)
thread.start()
result = subprocess.run([java, '-javaagent:' + str(jar), '-Dmojin.join.pipe=' + pipe, '-Dmojin.join.instance=vw', '-cp', cp, 'NamedPipeSecurityCheck'], capture_output=True, timeout=15, creationflags=subprocess.CREATE_NO_WINDOW)
thread.join(3)
assert result.returncode == 0 and b'SECURITY_PIPE_PASS' in result.stdout and not errors and state['requests'] == 1 and state['emptyProbes'] >= 1, (result.returncode, state, errors)
report = dict(passed=True, jarSha256=hashlib.sha256(jar.read_bytes()).hexdigest(), **state)
(work / 'result.json').write_text(json.dumps(report, indent=2) + '\n', encoding='utf-8')
print('PASS Java 8 + real FMLSecurityManager: empty probes discarded, exactly one ticket request', state)
