"""Exercise the actual native installer and process builder, without starting Minecraft.

Already fetched files seed an isolated download cache. Java is extracted by the native installer.
This is installation/argument verification, not clean-network or real-server acceptance.
"""
import argparse
import json
import pathlib
import shutil
import subprocess
import urllib.parse
from pack_distribution import ROOT

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--dotnet', type=pathlib.Path, default=ROOT.parent / '.tools/dotnet10/dotnet.exe')
parser.add_argument('--instances', nargs='+', choices=['m3e', 'dc2', 'mb'], default=['m3e', 'dc2', 'mb'])
args = parser.parse_args()
config = json.loads((ROOT / 'packs/distributions.json').read_text(encoding='utf-8'))
target = ROOT / '.local/native-install-check'
cache = target / 'cache'; cache.mkdir(parents=True, exist_ok=True)
for instance in args.instances:
    manifest_path = ROOT / f'artifacts/native/{instance}-manifest.json'
    manifest = json.loads(manifest_path.read_text(encoding='utf-8'))
    spec = config['instances'][instance]
    source = pathlib.Path(spec['source'])
    if not source.is_absolute(): source = (ROOT / source).resolve()
    engine = ROOT / '.local/engines' / instance
    archives = [manifest['runtime']['archive'], *(bundle['archive'] for bundle in manifest.get('bundles', []))]
    for item in [*manifest['files'], *archives]:
        destination = cache / item['sha256']
        if destination.is_file(): continue
        candidates = [ROOT / '.local/source-cache' / (item['sha256'] + '.jar'), engine / item['path'], source / item['path']]
        for url in item['sources']:
            parsed = urllib.parse.urlsplit(url)
            if parsed.hostname == 'launcher.boshan.uk':
                relative = urllib.parse.unquote(parsed.path.lstrip('/'))
                candidates += [ROOT / 'artifacts/native/public' / relative, ROOT / 'artifacts/distributions/public' / relative]
                if relative.endswith('.mrpack'): candidates.append(ROOT / 'artifacts/distributions' / relative.rsplit('/', 1)[-1])
        for candidate in candidates:
            if candidate.is_file() and candidate.stat().st_size == item['size']:
                shutil.copy2(candidate, destination); break
    print(json.dumps({'instance': instance, 'stage': 'native-install', 'cacheReused': True, 'gameStarted': False}), flush=True)
    subprocess.run([str(args.dotnet), str(ROOT / 'src/Publisher/bin/Release/net10.0/Publisher.dll'),
                    'check-install', str(manifest_path), str(target)], check=True, cwd=ROOT,
                   creationflags=subprocess.CREATE_NO_WINDOW)
