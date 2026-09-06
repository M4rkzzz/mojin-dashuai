"""Build the read-only, Java 8 compatible loading reporter. No game dependencies."""
from pathlib import Path
import subprocess
import zipfile
import hashlib

root = Path(__file__).resolve().parents[1]
javac = next((root.parent / '.tools/temurin25').glob('*/bin/javac.exe'))
classes = root / '.local/loading-agent/classes'
classes.mkdir(parents=True, exist_ok=True)
subprocess.run([str(javac), '-J-Duser.language=en', '--release', '8', '-encoding', 'UTF-8', '-d', str(classes),
                str(root / 'src/GameIntegration/loading/MojinLoadingAgent.java')], check=True,
               creationflags=subprocess.CREATE_NO_WINDOW)
output = root / 'src/Launcher.Desktop/Assets/GameLoading/mojin-loading-agent.jar'
output.parent.mkdir(parents=True, exist_ok=True)
with zipfile.ZipFile(output, 'w', compression=zipfile.ZIP_DEFLATED) as archive:
    entries = {'META-INF/MANIFEST.MF': b'Manifest-Version: 1.0\r\nPremain-Class: uk.boshan.loading.MojinLoadingAgent\r\n\r\n'}
    entries.update({p.relative_to(classes).as_posix(): p.read_bytes() for p in classes.rglob('MojinLoadingAgent*.class')})
    for name, content in sorted(entries.items()):
        info = zipfile.ZipInfo(name, (2026, 9, 6, 0, 0, 0))
        info.compress_type = zipfile.ZIP_DEFLATED
        archive.writestr(info, content)
print(output.name, output.stat().st_size, hashlib.sha256(output.read_bytes()).hexdigest())
