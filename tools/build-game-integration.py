"""Build the small client-only Forge 1.7.10 connection adapter using pinned engine libraries."""
import hashlib
from pathlib import Path
import subprocess
import zipfile
from pack_distribution import ROOT

engine = ROOT / '.local/engines/m3e'
forge = engine / 'libraries/net/minecraftforge/forge/1.7.10-10.13.4.1614-1.7.10/forge-1.7.10-10.13.4.1614-1.7.10.jar'
if not forge.is_file():
    forge, = (engine / 'libraries/net/minecraftforge/forge').rglob('*.jar')
classpath = ';'.join(str(path) for path in [forge, *engine.glob('libraries/com/google/guava/guava/17.0/*.jar')])
javac = next((ROOT.parent / '.tools/temurin25').glob('*/bin/javac.exe'))
classes = ROOT / '.local/game-integration/m3e/classes'; classes.mkdir(parents=True, exist_ok=True)
compile_result = subprocess.run([str(javac), '-J-Duser.language=en', '--release', '8', '-encoding', 'UTF-8', '-cp', classpath, '-d', str(classes),
                str(ROOT / 'src/GameIntegration/m3e/MojinAutoConnect.java')], capture_output=True,
               creationflags=subprocess.CREATE_NO_WINDOW)
if compile_result.returncode:
    print(compile_result.stderr.decode('utf-8', errors='replace'))
    raise SystemExit(compile_result.returncode)
output = ROOT / 'artifacts/game-integration/mojin-autoconnect-1.7.10-0.1.0.jar'
output.parent.mkdir(parents=True, exist_ok=True)
with zipfile.ZipFile(output, 'w', compression=zipfile.ZIP_DEFLATED) as archive:
    for path in sorted(classes.rglob('*.class')):
        entry = zipfile.ZipInfo(path.relative_to(classes).as_posix(), (2026, 9, 5, 0, 0, 0))
        entry.compress_type = zipfile.ZIP_DEFLATED
        archive.writestr(entry, path.read_bytes())
print(output.name, hashlib.sha256(output.read_bytes()).hexdigest())
