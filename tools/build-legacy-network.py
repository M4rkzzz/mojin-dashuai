"""Build the Forge-event socket option adapter, without replacing game classes."""
from pathlib import Path
import os,subprocess,zipfile
ROOT=Path(__file__).resolve().parents[1]
javac=next((ROOT.parent/'.tools/temurin25').glob('*/bin/javac.exe'))
classes=ROOT/'.local/network-optimization/legacy-classes';classes.mkdir(parents=True,exist_ok=True)
classpath=os.pathsep.join(str(Path(os.environ['TEMP'])/name) for name in ['forge-1.7.10-10.13.4.1614-universal.jar','minecraft_server.1.7.10.jar'])
r=subprocess.run([str(javac),'--release','8','-encoding','UTF-8','-cp',classpath,'-d',str(classes),str(ROOT/'src/GameIntegration/network/LegacyNetworkFix.java')],capture_output=True,creationflags=subprocess.CREATE_NO_WINDOW)
if r.returncode:raise RuntimeError(r.stderr.decode(errors='replace'))
out=ROOT/'artifacts/game-integration/mojin-legacy-network-1.7.10-1.0.0.jar';out.parent.mkdir(parents=True,exist_ok=True)
with zipfile.ZipFile(out,'w',zipfile.ZIP_DEFLATED) as jar:
    for file in sorted(classes.rglob('*.class')):jar.writestr(file.relative_to(classes).as_posix(),file.read_bytes())
print(out,out.stat().st_size)
