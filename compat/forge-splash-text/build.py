"""Build the small Java 8 coremod from explicit existing Forge/ASM/LaunchWrapper dependencies."""
import argparse, pathlib, subprocess, zipfile

p = argparse.ArgumentParser()
p.add_argument('--jdk', type=pathlib.Path, required=True)
p.add_argument('--libraries', type=pathlib.Path, required=True)
p.add_argument('--output', type=pathlib.Path, required=True)
a = p.parse_args()
root = pathlib.Path(__file__).resolve().parent
classes = a.output.parent / 'splash-text-classes'
classes.mkdir(parents=True, exist_ok=True)
deps = [a.libraries / x for x in [
    'net/minecraftforge/forge/1.7.10-10.13.4.1614-1.7.10/forge-1.7.10-10.13.4.1614-1.7.10.jar',
    'org/ow2/asm/asm-all/5.0.3/asm-all-5.0.3.jar',
    'net/minecraft/launchwrapper/1.12/launchwrapper-1.12.jar']]
subprocess.run([str(a.jdk/'bin/javac.exe'), '--release', '8', '-encoding', 'UTF-8', '-cp', ';'.join(map(str,deps)),
                '-d', str(classes), *map(str, sorted((root/'src').rglob('*.java')))], check=True)
with zipfile.ZipFile(a.output, 'w', zipfile.ZIP_DEFLATED) as jar:
    entries = {'META-INF/MANIFEST.MF': b'Manifest-Version: 1.0\r\nFMLCorePlugin: uk.boshan.splash.SplashTextPlugin\r\n\r\n'}
    entries.update({f.relative_to(classes).as_posix(): f.read_bytes() for f in classes.rglob('*.class')})
    entries.update({'src/'+f.relative_to(root/'src').as_posix(): f.read_bytes() for f in (root/'src').rglob('*.java')})
    for name, data in sorted(entries.items()):
        info = zipfile.ZipInfo(name, (2026, 9, 6, 0, 0, 0))
        info.compress_type = zipfile.ZIP_DEFLATED
        jar.writestr(info, data)
print(a.output)
