"""Assemble complete native manifests from CmlLib engine references and standard packs.

Only .local engine directories and artifacts are written. The supplied clients stay untouched.
"""
import argparse
import concurrent.futures
import fnmatch
import hashlib
import json
import pathlib
import shutil
import threading
import time
import urllib.parse
import urllib.request
import urllib.error
import zipfile
from pack_distribution import ROOT, safe_path

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--instances', nargs='+', choices=['m3e', 'dc2', 'mb'], default=['m3e', 'dc2', 'mb'])
parser.add_argument('--limit-mib', type=int, default=2)
parser.add_argument('--sequence', type=int, default=1)
args = parser.parse_args()
if args.sequence < 1: parser.error('--sequence must be positive')
config = json.loads((ROOT / 'packs/distributions.json').read_text(encoding='utf-8'))
output = ROOT / 'artifacts/native'
output.mkdir(parents=True, exist_ok=True)
public = output / 'public'
cache_roots = [ROOT / '.local/lab/mb', pathlib.Path('D:/Desktop/魔金大帅/.minecraft'),
               pathlib.Path('D:/Desktop/【三服】肉丸工艺/dmeatball/.minecraft')]
budget_lock = threading.Lock()
started, transferred = time.monotonic(), 0


def hashes(path):
    sha1, sha256 = hashlib.sha1(), hashlib.sha256()
    with path.open('rb') as source:
        while chunk := source.read(1024 * 1024):
            sha1.update(chunk); sha256.update(chunk)
    return sha1.hexdigest(), sha256.hexdigest()


def valid(path, row):
    return path.is_file() and (not row.get('size') or path.stat().st_size == row['size']) and (
        not row.get('sha1') or hashes(path)[0] == row['sha1'])


def fetch(url, target):
    global transferred
    target.parent.mkdir(parents=True, exist_ok=True)
    part = target.with_name(target.name + '.part')
    request = urllib.request.Request(url, headers={'User-Agent': 'MojinDashuai-Publisher/0.1'})
    response = None
    for attempt in range(4):
        try:
            response = urllib.request.urlopen(request, timeout=45); break
        except urllib.error.HTTPError as error:
            if error.code not in (429, 500, 502, 503, 504) or attempt == 3: raise
            time.sleep(2 ** (attempt + 1))
        except (urllib.error.URLError, TimeoutError):
            if attempt == 3: raise
            time.sleep(2 ** (attempt + 1))
    with response, part.open('wb') as output_file:
        while chunk := response.read(131072):
            output_file.write(chunk)
            with budget_lock:
                transferred += len(chunk)
                wait = transferred / max(args.limit_mib * 1048576, 1) - (time.monotonic() - started)
            if args.limit_mib and wait > 0:
                time.sleep(wait)
    part.replace(target)


def object_source(path, sha):
    relative = 'objects/' + sha + '.bin'
    target = public / relative
    if not target.exists():
        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(path, target)
    return config['publicBase'] + '/' + relative


def record(path, relative, sources, policy='managed', basis='Pinned game engine dependency and operator-prepared configuration'):
    return {'path': safe_path(relative), 'size': path.stat().st_size, 'sha256': hashes(path)[1],
            'sources': sources, 'policy': policy, 'distributionBasis': basis}


def prepare_file(engine, row):
    relative = safe_path(row['path']); target = engine / relative
    if not valid(target, row):
        for root in cache_roots:
            source = root / relative
            if valid(source, row):
                target.parent.mkdir(parents=True, exist_ok=True); shutil.copy2(source, target); break
        else:
            if not row.get('url'):
                raise ValueError('Generated loader file missing: ' + relative)
            fetch(row['url'], target)
            if not valid(target, row):
                raise ValueError('Engine file mismatch: ' + relative)
    sha = hashes(target)[1]
    # Mojang assets remain direct downloads; loader libraries also have an exact self-hosted copy.
    sources = [row['url']] if row.get('url') else []
    if not relative.startswith('assets/objects/'):
        sources.append(object_source(target, sha))
    result = [record(target, relative, list(dict.fromkeys(sources)))]
    for copy in row.get('copies', []):
        destination = engine / safe_path(copy)
        if not destination.exists():
            destination.parent.mkdir(parents=True, exist_ok=True); shutil.copy2(target, destination)
        result.append({**result[0], 'path': copy})
    return result


def runtime(major):
    spec = json.loads((ROOT / f'packs/java-{major}-source.json').read_text(encoding='utf-8'))
    archive = ROOT / f'.local/runtimes/java-{major}.zip' if major != 25 else ROOT.parent / 'downloads/OpenJDK25U-jdk_x64_windows_hotspot.zip'
    if archive.stat().st_size != spec['size'] or hashes(archive)[1] != spec['sha256']:
        raise ValueError('Runtime archive mismatch')
    with zipfile.ZipFile(archive) as zip_file:
        java_path, = [item.filename for item in zip_file.infolist() if item.filename.endswith('/bin/java.exe')]
        expanded = sum(item.file_size for item in zip_file.infolist())
    hosted = object_source(archive, spec['sha256'])
    return {'id': 'temurin-' + spec['version'], 'major': major, 'version': spec['version'], 'platform': 'windows-x64',
            'archive': record(archive, 'runtime.zip', [spec['url'], hosted], basis='Eclipse Temurin; GPL-2.0 with Classpath Exception'),
            'javaPath': java_path, 'expandedSize': expanded}


for instance in args.instances:
    spec = config['instances'][instance]
    engine = ROOT / '.local/engines' / instance
    extracted = json.loads((engine / 'required.json').read_text(encoding='utf-8'))
    rows = {row['path']: row for row in extracted['files']}
    records = {}
    errors = []
    print(json.dumps({'instance': instance, 'phase': 'engine', 'files': len(rows)}), flush=True)
    with concurrent.futures.ThreadPoolExecutor(max_workers=4) as executor:
        futures = {executor.submit(prepare_file, engine, row): row for row in rows.values()}
        completed, last_report = 0, time.monotonic()
        for future in concurrent.futures.as_completed(futures):
            try:
                for item in future.result(): records[item['path']] = item
            except Exception as error:
                errors.append({'path': futures[future]['path'], 'error': str(error)})
            completed += 1
            if time.monotonic() - last_report > 20:
                print(json.dumps({'instance': instance, 'completed': completed, 'files': len(rows), 'errors': len(errors)}), flush=True)
                last_report = time.monotonic()
    if errors:
        (output / f'{instance}-errors.json').write_text(json.dumps(errors, ensure_ascii=False, indent=2), encoding='utf-8')
        raise SystemExit(f'{instance}: {len(errors)} engine files still missing; see artifacts/native/{instance}-errors.json')
    for folder in ['versions', 'assets/indexes']:
        for path in (engine / folder).rglob('*.json'):
            relative = path.relative_to(engine).as_posix()
            records[relative] = record(path, relative, [object_source(path, hashes(path)[1])])
    content = json.loads((ROOT / f'artifacts/distributions/{instance}-content.json').read_text(encoding='utf-8'))
    for item in content['files']:
        records[item['path']] = {key: item[key] for key in ['path', 'size', 'sha256', 'sources', 'policy', 'distributionBasis']}
    if instance == 'm3e':
        adapter = ROOT / 'artifacts/game-integration/mojin-autoconnect-1.7.10-0.1.0.jar'
        if not adapter.is_file(): raise ValueError('Run tools/build-game-integration.py first')
        relative = 'mods/' + adapter.name
        records[relative] = record(adapter, relative, [object_source(adapter, hashes(adapter)[1])],
                                   basis='Launcher-owned client connection adapter; source in src/GameIntegration/m3e')
    version_base = f'distributions/{instance}/{spec["version"]}'
    overrides = ROOT / 'artifacts/distributions/public' / version_base
    for path in overrides.rglob('*'):
        if not path.is_file(): continue
        relative = path.relative_to(overrides).as_posix()
        if relative in ('index.toml', 'pack.toml') or relative.endswith('.pw.toml'): continue
        # Personal configuration initializes once; pack scripts and machine/quest definitions remain managed.
        seeded = relative in ('options.txt', 'servers.dat') or relative.startswith(('config/', 'shaderpacks/', 'resourcepacks/'))
        if any(fnmatch.fnmatchcase(relative, pattern) for pattern in ['config/modularmachinery/**', 'config/ftbquests/**']): seeded = False
        records[relative] = record(path, relative, [config['publicBase'] + '/' + version_base + '/' + urllib.parse.quote(relative)],
                                   'seed' if seeded else 'managed', 'Operator-provided pack overrides; personal files excluded')
    if spec['format'] == 'modrinth':
        pack_report = json.loads((ROOT / f'artifacts/distributions/{instance}-report.json').read_text(encoding='utf-8'))
        bundle = ROOT / 'artifacts/distributions' / pack_report['portableArtifact']
        bundle_url = config['publicBase'] + '/' + version_base + '/' + bundle.name
        bundle_prefix = 'overrides/'
    else:
        bundle = public / version_base / 'overrides.zip'; bundle.parent.mkdir(parents=True, exist_ok=True)
        with zipfile.ZipFile(bundle, 'w', compression=zipfile.ZIP_DEFLATED, compresslevel=3) as archive:
            for path in sorted(overrides.rglob('*')):
                if not path.is_file(): continue
                relative = path.relative_to(overrides).as_posix()
                if relative in ('index.toml', 'pack.toml') or relative.endswith('.pw.toml'): continue
                info = zipfile.ZipInfo(relative, (2026, 9, 5, 0, 0, 0)); info.compress_type = zipfile.ZIP_DEFLATED
                archive.writestr(info, path.read_bytes())
        bundle_url = config['publicBase'] + '/' + version_base + '/overrides.zip'
        bundle_prefix = ''
    manifest = {'instance': instance, 'version': spec['version'], 'sequence': args.sequence, 'minecraft': spec['minecraft'],
                'loader': spec['loader'], 'loaderVersion': spec['loaderVersion'], 'launchVersion': extracted['launchVersion'],
                'runtime': runtime(spec['javaMajor']), 'memoryMiB': spec['memoryMiB'],
                'compatibility': instance + '-' + spec['minecraft'] + '-' + spec['loader'],
                'files': sorted(records.values(), key=lambda item: item['path']),
                'validationEvidence': [f'packs/acceptance/{instance}-{args.sequence}.json'],
                'bundles': [{'archive': record(bundle, 'overrides.zip', [bundle_url], basis='Generated standard pack overrides'), 'prefix': bundle_prefix}]}
    (output / f'{instance}-manifest.json').write_text(json.dumps(manifest, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
    (output / f'{instance}-errors.json').unlink(missing_ok=True)
    print(json.dumps({'instance': instance, 'phase': 'manifest-ready', 'files': len(records),
                      'bytes': sum(item['size'] for item in records.values()), 'java': spec['javaMajor'], 'gameAcceptance': False}), flush=True)
