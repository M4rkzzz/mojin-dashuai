"""Build isolated Java 8 activity observer with relocated ASM and Gson."""
from pathlib import Path
import struct,subprocess,zipfile

root=Path(__file__).resolve().parents[1]
javac=next((root.parent/'.tools/temurin25').glob('*/bin/javac.exe'))
asm=root/'.local/engines/mb/libraries/org/ow2/asm/asm/9.10.1/asm-9.10.1.jar'
gson=root/'.local/engines/dc2/libraries/com/google/code/gson/gson/2.10.1/gson-2.10.1.jar'
classes=root/'.local/activities/classes';classes.mkdir(parents=True,exist_ok=True)
result=subprocess.run([str(javac),'-J-Duser.language=en','--release','8','-encoding','UTF-8','-cp',str(asm)+';'+str(gson),'-d',str(classes),*map(str,(root/'src/GameIntegration/activities').glob('*.java'))],capture_output=True,creationflags=0x08000000)
if result.returncode:raise RuntimeError(result.stderr.decode('utf-8','replace'))

def relocate(data):
    count=struct.unpack('>H',data[8:10])[0];out=bytearray(data[:10]);pos=10;index=1
    sizes={3:4,4:4,5:8,6:8,7:2,8:2,9:4,10:4,11:4,12:4,15:3,16:2,17:4,18:4,19:2,20:2}
    while index<count:
        tag=data[pos];pos+=1;out.append(tag)
        if tag==1:
            n=struct.unpack('>H',data[pos:pos+2])[0];pos+=2;value=data[pos:pos+n];pos+=n
            for a,b in [(b'org/objectweb/asm',b'uk/boshan/activities/shaded/asm'),(b'com/google/gson',b'uk/boshan/activities/shaded/gson'),(b'com.google.gson',b'uk.boshan.activities.shaded.gson')]:value=value.replace(a,b)
            out.extend(struct.pack('>H',len(value)));out.extend(value)
        else:
            n=sizes[tag];out.extend(data[pos:pos+n]);pos+=n
            if tag in (5,6):index+=1
        index+=1
    out.extend(data[pos:]);return bytes(out)

entries={'META-INF/MANIFEST.MF':b'Manifest-Version: 1.0\r\nPremain-Class: uk.boshan.activities.ActivityAgent\r\n\r\n'}
for p in classes.rglob('*.class'):entries[p.relative_to(classes).as_posix()]=relocate(p.read_bytes())
for jar,prefix,newprefix in [(asm,'org/objectweb/asm/','uk/boshan/activities/shaded/asm/'),(gson,'com/google/gson/','uk/boshan/activities/shaded/gson/')]:
    with zipfile.ZipFile(jar) as z:
        for n in z.namelist():
            if n.startswith(prefix) and n.endswith('.class'):entries[n.replace(prefix,newprefix)]=relocate(z.read(n))
            if 'LICENSE' in n.upper():entries['META-INF/licenses/'+jar.stem+'/'+Path(n).name]=z.read(n)
entries['META-INF/licenses/ASM.txt']=(root/'src/GameIntegration/join/ASM-LICENSE.txt').read_bytes()
entries['META-INF/licenses/Gson.txt']=(root/'src/GameIntegration/activities/GSON-LICENSE.txt').read_bytes()
out=root/'artifacts/activities/mojin-activities-server-agent.jar';out.parent.mkdir(parents=True,exist_ok=True)
with zipfile.ZipFile(out,'w') as z:
    for n,b in sorted(entries.items()):
        info=zipfile.ZipInfo(n,(2026,9,6,0,0,0));info.compress_type=zipfile.ZIP_DEFLATED;z.writestr(info,b)
print('Built',out.name,out.stat().st_size,'bytes')
