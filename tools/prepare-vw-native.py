"""Prepare the frozen vw first-release input without signing, uploading or launching.

Only the frozen client directory and explicitly pinned m3e engine records are read.
The output directory must be new. Remote construction can reuse existing objects.
"""
from __future__ import annotations

import argparse
import copy
import hashlib
import json
from pathlib import Path
import zipfile

from pack_distribution import ROOT, digest, encode_json, mrpack_index, safe_path, write_zip

VERSION = '1.1.9.1-boshan-r1'
LAUNCH = 'vw-1.7.10-forge-10.13.4.1614'
FROZEN_SHA = '26bccd5a5dac7040df5aa75bc25e81b36c935bbdb00601280f71a3394cadd81e'
BASE = 'https://launcher-direct.boshan.uk:21708/objects/sha256'
ADAPTERS = {
    'mods/mojin-autoconnect-1.7.10-0.1.0.jar',
    'mods/CustomSkinLoader_ForgeLegacy-14.17.jar',
    'mods/CompatibilityLayerForCustomSkinLoader-ALPHA-11.jar',
    'CustomSkinLoader/CustomSkinLoader.json',
}


def read_json(path):
    return json.loads(path.read_text(encoding='utf-8-sig'))


def verify(path, row):
    if path.is_symlink() or not path.is_file() or path.stat().st_size != row['size']:
        raise ValueError('Missing or changed pinned source: ' + row['path'])
    data = path.read_bytes()
    if hashlib.sha256(data).hexdigest() != row['sha256']:
        raise ValueError('Pinned source hash differs: ' + row['path'])
    return data


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--source-manifest', type=Path, default=ROOT.parent / '_voidwayfarer4-deploy/client-manifest.json')
    parser.add_argument('--source', type=Path, default=ROOT.parent / '_voidwayfarer4-deploy/client-overrides')
    parser.add_argument('--source-archive', type=Path, default=ROOT.parent / '_voidwayfarer4-deploy/VoidWayfarer-1.1.9.1-boshan-client-r1.zip')
    parser.add_argument('--engine-manifest', type=Path, default=ROOT / 'artifacts/native/m3e-manifest.json')
    parser.add_argument('--engine-root', type=Path, default=ROOT / '.local/engines/m3e')
    parser.add_argument('--output', required=True, type=Path)
    args = parser.parse_args()
    if args.output.exists():
        raise ValueError('Output must be a new directory')
    frozen, base = read_json(args.source_manifest), read_json(args.engine_manifest)
    if (frozen['instanceId'], frozen['minecraft'], frozen['forge'], frozen['javaMajor']) != ('vw', '1.7.10', '10.13.4.1614', 8):
        raise ValueError('Frozen identity differs')
    if len(frozen['files']) != 984 or frozen['archiveSha256'] != FROZEN_SHA:
        raise ValueError('Frozen inventory differs')
    if (base['minecraft'], base['loaderVersion'], base['runtime']['major']) != ('1.7.10', '10.13.4.1614', 8):
        raise ValueError('Engine/runtime compatibility differs')
    archive_data = verify(args.source_archive, {'path': args.source_archive.name, 'size': 82533359, 'sha256': FROZEN_SHA})
    expected = {safe_path(row['path']): row for row in frozen['files']}
    if len(expected) != len(frozen['files']) or len({p.casefold() for p in expected}) != len(expected):
        raise ValueError('Duplicate frozen path')
    source_root = args.source.resolve()
    actual = set()
    for path in args.source.rglob('*'):
        if path.is_symlink() or not path.resolve().is_relative_to(source_root):
            raise ValueError('Linked source path')
        if path.is_file():
            actual.add(path.relative_to(args.source).as_posix())
    if actual != set(expected):
        raise ValueError('Frozen directory has missing or extra files')
    contents = {p: verify(args.source / p, row) for p, row in expected.items()}
    with zipfile.ZipFile(args.source_archive) as archive:
        entries = [e for e in archive.infolist() if not e.is_dir()]
        if len(entries) != len(expected) or {e.filename for e in entries} != {'client-overrides/' + p for p in expected}:
            raise ValueError('Frozen ZIP inventory differs')
        if any(archive.read(e) != contents[e.filename.removeprefix('client-overrides/')] for e in entries):
            raise ValueError('Frozen ZIP differs from directory')
    del archive_data
    if any('boshan-islands' in p.lower() or 'mcqq' in p.lower() for p in expected):
        raise ValueError('Server-only addition in client')
    dependencies = [p for p in expected if p.startswith('falsepattern/')]
    if len(dependencies) != 9 or sum(p.endswith('.jar') for p in dependencies) != 5:
        raise ValueError('Pinned falsepattern dependencies are incomplete')
    for p in dependencies:
        if p.endswith(('.sha1', '.sha512')):
            jar, algorithm = p.rsplit('.', 1)
            if hashlib.new(algorithm, contents[jar]).hexdigest() != contents[p].decode('ascii').strip():
                raise ValueError('Dependency sidecar differs: ' + p)

    args.output.mkdir(parents=True)
    objects = args.output / 'new-objects'
    objects.mkdir()
    records = {}
    def record(path, data, policy='managed', basis='Frozen Void Wayfarer boshan-r1 operator distribution; original license and embedded notices retained'):
        hashes = digest(data)
        row = {'path': safe_path(path), 'size': len(data), 'sha256': hashes['sha256'],
               'sources': [BASE + '/' + hashes['sha256']], 'policy': policy, 'distributionBasis': basis}
        target = objects / hashes['sha256']
        if not target.exists(): target.write_bytes(data)
        if path in records: raise ValueError('Manifest path collision: ' + path)
        records[path] = row
        return {**row, 'sha1': hashes['sha1'], 'sha512': hashes['sha512']}

    pack_rows = []
    for path, data in sorted(contents.items()):
        seeded = path in ('options.txt', 'servers.dat') or path.startswith('config/')
        if path.startswith(('config/betterquesting/', 'config/GregTech/', 'config/AppliedEnergistics2/')):
            seeded = False
        pack_rows.append(record(path, data, 'seed' if seeded else 'managed'))
    base_rows = {r['path']: r for r in base['files']}
    for path in sorted(ADAPTERS):
        original = base_rows[path]
        # Adapter objects already exist on the unified origin; no broad source scan.
        records[path] = {**copy.deepcopy(original), 'sources': [BASE + '/' + original['sha256']],
                         'distributionBasis': 'Existing accepted m3e launcher integration, reused for vw; embedded notices retained'}
        if path == 'CustomSkinLoader/CustomSkinLoader.json':
            local = ROOT / 'artifacts/distributions/public/distributions/m3e/8.0.4-2-mojin.1' / path
        elif 'mojin-autoconnect' in path:
            local = ROOT / 'artifacts/game-integration' / Path(path).name
        else:
            local = ROOT / '.local/source-cache' / (original['sha256'] + '.jar')
        data = verify(local, original)
        contents[path] = data
        pack_rows.append({**records[path], **{k: v for k, v in digest(data).items() if k != 'sha256'}})
    engine_paths = [p for p in base_rows if p.startswith(('assets/', 'libraries/', 'versions/'))]
    engine_data = {}
    for path in engine_paths:
        original = base_rows[path]
        engine_data[path] = verify(args.engine_root / path, original)
        if path.endswith('/MSE.json'): continue
        new_path = path.replace('versions/MSE/MSE.jar', f'versions/{LAUNCH}/{LAUNCH}.jar')
        records[new_path] = {**copy.deepcopy(original), 'path': new_path, 'sources': [BASE + '/' + original['sha256']]}

    # Pin every launch descriptor download to the same immutable origin. Only
    # Windows x64 native classifiers present in this manifest are retained.
    launch = json.loads(engine_data['versions/MSE/MSE.json'])
    launch['id'] = LAUNCH
    def descriptor(path):
        original = base_rows[path]
        return {'sha1': hashlib.sha1(engine_data[path]).hexdigest(), 'size': original['size'], 'url': BASE + '/' + original['sha256']}
    launch['downloads'] = {'client': descriptor('versions/MSE/MSE.jar')}
    launch['assetIndex'].update(descriptor('assets/indexes/1.7.10.json'))
    launch['logging']['client']['file'].update(descriptor('assets/log_configs/client-1.7.xml'))
    for lib in launch['libraries']:
        lib.pop('url', None)
        downloads = lib.get('downloads', {})
        parts = lib['name'].split(':')
        relative = '/'.join((parts[0].replace('.', '/'), parts[1], parts[2], parts[1] + '-' + parts[2] + '.jar'))
        if 'libraries/' + relative in base_rows:
            downloads['artifact'] = {'path': relative, **descriptor('libraries/' + relative)}
        classifiers = downloads.get('classifiers', {})
        for key in list(classifiers):
            path = 'libraries/' + classifiers[key]['path']
            if path not in base_rows:
                del classifiers[key]
            else:
                classifiers[key].update(descriptor(path))
        if classifiers:
            downloads['classifiers'] = classifiers
        elif 'classifiers' in downloads:
            del downloads['classifiers']
        if downloads: lib['downloads'] = downloads
    record(f'versions/{LAUNCH}/{LAUNCH}.json', encode_json(launch), basis='Pinned accepted MC1.7.10/Forge1614 engine; distinct vw id and unified immutable download URLs')
    runtime = copy.deepcopy(base['runtime'])
    runtime['archive']['sources'] = [BASE + '/' + runtime['archive']['sha256']]
    manifest = {'instance': 'vw', 'version': VERSION, 'sequence': 1, 'minecraft': '1.7.10',
                'loader': 'forge', 'loaderVersion': '10.13.4.1614', 'launchVersion': LAUNCH,
                'runtime': runtime, 'memoryMiB': 4096, 'compatibility': 'vw-1.7.10-forge-r1',
                'files': sorted(records.values(), key=lambda r: r['path']), 'validationEvidence': [], 'bundles': []}
    # Run the same manifest guard used by the complete builder before producing metadata.
    import importlib.util
    spec = importlib.util.spec_from_file_location('complete_builder', ROOT / 'tools/build-complete-client.py')
    builder = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(builder)
    builder.validate_manifest(manifest, VERSION, 1, initial_release=True)
    (args.output / 'vw-manifest.input.json').write_bytes(encode_json(manifest))
    write_zip(args.output / 'new-objects.zip', {p.name: p.read_bytes() for p in objects.iterdir()})
    standard_name = f'vw-{VERSION}.mrpack'
    standard_spec = {'minecraft': '1.7.10', 'loader': 'forge', 'loaderVersion': '10.13.4.1614',
                     'version': VERSION, 'name': '虚空行者'}
    index = mrpack_index(standard_spec, [r for r in pack_rows if r['path'].endswith('.jar')])
    portable = {'modrinth.index.json': encode_json(index), **{
        'overrides/' + p: data for p, data in contents.items() if not p.endswith('.jar')}}
    write_zip(args.output / standard_name, portable)
    audit = {'instance': 'vw', 'releaseReady': False, 'sourceFrozenVerified': True, 'clientRequestedAuthorOrigins': False,
             'sourceManifestSha256': hashlib.sha256(args.source_manifest.read_bytes()).hexdigest(),
             'sourceArchiveSha256': FROZEN_SHA, 'frozenFileCount': len(expected),
             'frozenBytes': sum(r['size'] for r in expected.values()), 'unifiedAdapterFiles': sorted(ADAPTERS),
             'falsepatternFiles': dependencies, 'serverOnlyExcluded': frozen['serverOnlyAdditions'],
             'originEvidence': '../docs/handoff/四服虚空行者部署-2026-09-05.md',
             'distributionBasis': 'User-authorized full distribution of frozen operator-prepared client; original CC BY-SA pack license and per-component embedded notices retained. No new per-mod license claim.',
             'files': [{**r, 'status': 'prepared-unified-origin', 'publishedVerified': False} for r in manifest['files']]}
    (args.output / 'vw-source-audit.json').write_bytes(encode_json(audit))
    summary = {'instance': 'vw', 'version': VERSION, 'sequence': 1, 'files': len(records),
               'fileBytes': sum(r['size'] for r in records.values()), 'runtimeBytes': runtime['archive']['size'],
               'newObjects': len(list(objects.iterdir())), 'newObjectBytes': sum(p.stat().st_size for p in objects.iterdir()),
               'inputManifestSha256': hashlib.sha256((args.output / 'vw-manifest.input.json').read_bytes()).hexdigest(),
               'standardArtifact': standard_name, 'standardBytes': (args.output / standard_name).stat().st_size,
               'standardSha256': hashlib.sha256((args.output / standard_name).read_bytes()).hexdigest(),
               'reusedEngineFiles': len(engine_paths) - 1, 'rewrittenEngineDescriptors': 1,
               'allFrozenFilesUnchanged': all(records[p]['sha256'] == row['sha256'] for p, row in expected.items()),
               'signed': False, 'uploaded': False, 'activated': False, 'unifiedClientJoinedServer': False}
    (args.output / 'prepare-report.json').write_bytes(encode_json(summary))
    print(json.dumps(summary))


if __name__ == '__main__':
    main()
