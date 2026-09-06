"""Build a Java 8 login gate; relocate ASM so Forge's ASM versions are unaffected."""
from pathlib import Path
import argparse, hashlib, struct, subprocess, zipfile

parser = argparse.ArgumentParser()
parser.add_argument('--server', action='store_true', help='Build the separately deployed server agent; leave the frozen client resource untouched')
args = parser.parse_args()

root = Path(__file__).resolve().parents[1]
javac = next((root.parent / '.tools/temurin25').glob('*/bin/javac.exe'))
asm = root / '.local/loading-live-20260906/instances/mb/libraries/org/ow2/asm/asm/9.10.1/asm-9.10.1.jar'
classes = root / '.local/join-agent/classes'
classes.mkdir(parents=True, exist_ok=True)
sources = sorted((root / 'src/GameIntegration/join').glob('*.java'))
compiled = subprocess.run([str(javac), '-J-Duser.language=en', '--release', '8', '-Xlint:-options', '-encoding', 'UTF-8', '-cp', str(asm), '-d', str(classes), *map(str, sources)], capture_output=True, text=True, creationflags=subprocess.CREATE_NO_WINDOW)
if compiled.returncode:
    raise RuntimeError(compiled.stdout + compiled.stderr)

def relocate(data):
    count = struct.unpack('>H', data[8:10])[0]
    result = bytearray(data[:10]); pos = 10; index = 1
    sizes = {3:4,4:4,5:8,6:8,7:2,8:2,9:4,10:4,11:4,12:4,15:3,16:2,17:4,18:4,19:2,20:2}
    while index < count:
        tag = data[pos]; pos += 1; result.append(tag)
        if tag == 1:
            length = struct.unpack('>H', data[pos:pos+2])[0]; pos += 2
            value = data[pos:pos+length].replace(b'org/objectweb/asm', b'uk/boshan/join/shaded/asm'); pos += length
            result.extend(struct.pack('>H', len(value))); result.extend(value)
        else:
            size = sizes[tag]; result.extend(data[pos:pos+size]); pos += size
            if tag in (5,6): index += 1
        index += 1
    result.extend(data[pos:]); return bytes(result)

entries = {'META-INF/MANIFEST.MF': b'Manifest-Version: 1.0\r\nPremain-Class: uk.boshan.join.JoinAgent\r\nCan-Redefine-Classes: false\r\nCan-Retransform-Classes: false\r\n\r\n'}
entries.update({p.relative_to(classes).as_posix(): relocate(p.read_bytes()) for p in classes.rglob('*.class')})
with zipfile.ZipFile(asm) as source:
    for name in source.namelist():
        if name.startswith('org/objectweb/asm/') and name.endswith('.class'):
            entries[name.replace('org/objectweb/asm/', 'uk/boshan/join/shaded/asm/')] = relocate(source.read(name))
    entries['META-INF/ASM-LICENSE.txt'] = (root / 'src/GameIntegration/join/ASM-LICENSE.txt').read_bytes()
output = root / 'src/GameIntegration/join' / ('mojin-join-server-agent.jar' if args.server else 'mojin-join-agent.jar')
with zipfile.ZipFile(output, 'w') as archive:
    for name, content in sorted(entries.items()):
        info = zipfile.ZipInfo(name, (2026,9,6,0,0,0)); info.compress_type = zipfile.ZIP_DEFLATED
        archive.writestr(info, content)
print(output, output.stat().st_size, hashlib.sha256(output.read_bytes()).hexdigest())
