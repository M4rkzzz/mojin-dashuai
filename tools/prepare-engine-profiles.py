"""Prepare pinned engine profiles in isolated directories, then extract CmlLib's required files."""
import argparse
import hashlib
import json
from pathlib import Path
import shutil
import subprocess
import urllib.request
from pack_distribution import ROOT

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--instances', nargs='+', choices=['m3e', 'dc2', 'mb'], default=['m3e', 'dc2', 'mb'])
parser.add_argument('--dotnet', type=Path, default=ROOT.parent / '.tools/dotnet10/dotnet.exe')
args = parser.parse_args()
tools = json.loads((ROOT / 'packs/engine-tools.json').read_text(encoding='utf-8'))

def download(url, path, algorithm=None, digest=None):
    if path.is_file() and (not digest or hashlib.new(algorithm, path.read_bytes()).hexdigest() == digest): return
    request = urllib.request.Request(url, headers={'User-Agent': 'MojinDashuai-Publisher/0.1'})
    data = urllib.request.urlopen(request, timeout=60).read()
    if digest and hashlib.new(algorithm, data).hexdigest() != digest: raise ValueError('Pinned engine download mismatch')
    path.parent.mkdir(parents=True, exist_ok=True); path.write_bytes(data)

for instance in args.instances:
    engine = ROOT / '.local/engines' / instance; engine.mkdir(parents=True, exist_ok=True)
    if instance == 'm3e':
        version = 'MSE'
        source = Path('D:/Desktop/魔金大帅/.minecraft/versions/MSE/MSE.json')
        target = engine / 'versions/MSE/MSE.json'; target.parent.mkdir(parents=True, exist_ok=True)
        profile = json.loads(source.read_text(encoding='utf-8-sig'))
        # MSE is a merged vanilla + Forge profile. Later Forge coordinates override
        # vanilla versions, as in the supplied client's working PCL launch command.
        libraries = {}
        for library in profile['libraries']:
            parts = library['name'].split(':')
            key = (parts[0], parts[1], tuple(parts[3:]),
                   json.dumps(library.get('rules'), sort_keys=True),
                   json.dumps(library.get('natives'), sort_keys=True))
            libraries[key] = library
        profile['libraries'] = list(libraries.values())
        target.write_text(json.dumps(profile, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
    else:
        spec = tools[instance]; version = spec['launchVersion']
        target = engine / f'versions/{version}/{version}.json'
        if not target.is_file():
            index = json.load(urllib.request.urlopen('https://piston-meta.mojang.com/mc/game/version_manifest_v2.json', timeout=30))
            entry = next(item for item in index['versions'] if item['id'] == spec['minecraft'])
            vanilla = engine / f'versions/{spec["minecraft"]}/{spec["minecraft"]}.json'
            download(entry['url'], vanilla, 'sha1', entry['sha1'])
            metadata = json.loads(vanilla.read_text(encoding='utf-8')); client = metadata['downloads']['client']
            download(client['url'], vanilla.with_suffix('.jar'), 'sha1', client['sha1'])
            installer = ROOT / '.local' / spec['url'].rsplit('/', 1)[-1]
            download(spec['url'], installer, 'sha256', spec['sha256'])
            (engine / 'launcher_profiles.json').write_text('{"profiles":{}}', encoding='utf-8')
            java_root = ROOT / '.local/runtimes/java-17' if spec['javaMajor'] == 17 else ROOT.parent / '.tools/temurin25'
            java = next(java_root.glob('*/bin/java.exe'))
            with (engine / 'loader-prepare.log').open('w', encoding='utf-8') as log:
                subprocess.run([str(java), '-Djava.awt.headless=true', '-jar', str(installer), '--installClient', str(engine)],
                               cwd=engine, stdout=log, stderr=subprocess.STDOUT, creationflags=subprocess.CREATE_NO_WINDOW, check=True, timeout=900)
    subprocess.run([str(args.dotnet), str(ROOT / 'src/Publisher/bin/Release/net10.0/Publisher.dll'), 'engine-files',
                    str(engine), version, str(engine / 'required.json')], cwd=ROOT, check=True, creationflags=subprocess.CREATE_NO_WINDOW)
    print(json.dumps({'instance': instance, 'launchVersion': version, 'prepared': True}), flush=True)
