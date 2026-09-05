"""Prepare the pinned dc2 r4 candidate without signing, hosting or touching old packs."""
import argparse
import copy
import hashlib
import json
from pathlib import Path
import zipfile

from pack_distribution import encode_json, safe_path, write_zip

ROOT = Path(__file__).resolve().parents[1]
RECIPE = ROOT / 'packs/revisions/dc2-r4'


def prepare(baseline, standard, output):
    recipe = json.loads((RECIPE / 'revision.json').read_text(encoding='utf-8'))
    old = json.loads(baseline.read_text(encoding='utf-8-sig'))
    if (old['instance'], old['version'], old['sequence']) != ('dc2', recipe['baseVersion'], recipe['baseSequence']):
        raise ValueError('Unexpected live baseline; inspect before preparing another revision')
    if output.exists():
        raise ValueError('Output already exists; preserve existing candidates')
    previous = {f['path']: f for f in old['files']}
    if not all(name in previous for name in recipe['remove']):
        raise ValueError('Expected retired files are missing from baseline')
    with zipfile.ZipFile(standard) as archive:
        entries = {entry.filename: archive.read(entry) for entry in archive.infolist() if not entry.is_dir()}
    index = json.loads(entries['modrinth.index.json'])
    if index['versionId'] != old['version']:
        raise ValueError('Standard import and native baseline versions differ')
    for name, data in entries.items():
        if name.startswith('overrides/'):
            relative = safe_path(name[10:])
            if relative not in previous or hashlib.sha256(data).hexdigest() != previous[relative]['sha256']:
                raise ValueError('Standard override differs from current native baseline: ' + relative)
    output.mkdir(parents=True)
    candidate = copy.deepcopy(old)
    candidate.update(version=recipe['version'], sequence=recipe['sequence'], compatibility=recipe['compatibility'], bundles=[], validationEvidence=[])
    files = {name: copy.deepcopy(row) for name, row in previous.items() if name not in recipe['remove']}
    index['files'] = [f for f in index['files'] if f['path'] not in recipe['remove']]
    for name in recipe['remove']:
        entries.pop('overrides/' + name, None)
    for item in recipe['add']:
        name = safe_path(item['path'])
        if name in files:
            raise ValueError('New mod already exists in baseline')
        files[name] = {key: item[key] for key in ('path', 'size', 'sha256', 'sources', 'distributionBasis', 'officialOnly')}
        files[name]['policy'] = 'managed'
        index['files'].append({'path': name, 'hashes': {'sha1': item['sha1'], 'sha512': item['sha512']},
                               'downloads': item['sources'], 'fileSize': item['size'],
                               'env': {'client': 'required', 'server': 'unsupported'}})
    changes = {p.relative_to(RECIPE / 'overrides').as_posix(): p.read_bytes()
               for p in (RECIPE / 'overrides').rglob('*') if p.is_file()}
    # DefaultOptions is for new independent installs; the unified launcher only
    # appends missing new-mod bindings, preserving existing player shortcuts.
    key_path = 'config/defaultoptions/keybindings.txt'
    keys = entries['overrides/' + key_path].decode('utf-8').rstrip() + '\n'
    for key in ('map', 'minimap.zoomIn', 'minimap.zoomOut'):
        full = 'key_key.ftbchunks.' + key
        if not any(line.startswith(full + ':') for line in keys.splitlines()):
            keys += full + ':key.keyboard.unknown:NONE\n'
    changes[key_path] = keys.encode('utf-8')
    changes['kubejs/assets/tombstone/cfpa-license.txt'] = (RECIPE / 'CFPA-LICENSE.txt').read_bytes()
    attribution = ('Chinese translations: CFPAOrg/Minecraft-Mod-Language-Package contributors\n'
                   'License: CC BY-NC-SA 4.0\n'
                   'Pinned source: https://github.com/CFPAOrg/Minecraft-Mod-Language-Package/tree/f703fe5f7cef22e706dc92d45fa0092a2c04fb76/projects/assets/corail-tombstone/1.20\n'
                   'Adaptation: retained only Corail 9.1.4 keys with matching format placeholders.\n')
    changes['kubejs/assets/tombstone/attribution.txt'] = attribution.encode('utf-8')
    for name, data in changes.items():
        safe_path(name)
        digest = hashlib.sha256(data).hexdigest()
        target = output / 'new-objects' / digest
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_bytes(data)
        files[name] = {'path': name, 'size': len(data), 'sha256': digest,
                       'sources': ['https://launcher-direct.boshan.uk:21708/objects/sha256/' + digest],
                       'policy': 'seed' if name.startswith('config/') else 'managed',
                       'distributionBasis': 'Operator defaults / attributed CFPA translation under CC BY-NC-SA 4.0'}
        entries['overrides/' + name] = data
    candidate['files'] = sorted(files.values(), key=lambda f: f['path'])
    index['versionId'] = recipe['version']
    index['files'].sort(key=lambda f: f['path'])
    entries['modrinth.index.json'] = encode_json(index)
    if any(name.lower().endswith('.jar') for name in entries):
        raise ValueError('Standard package must not contain bundled mod JARs')
    pack = output / ('dc2-' + recipe['version'] + '-draft.mrpack')
    write_zip(pack, entries)
    (output / 'dc2-manifest.candidate.json').write_bytes(encode_json(candidate))
    added = sorted(set(files) - set(previous))
    changed = sorted(name for name in set(files) & set(previous) if files[name] != previous[name])
    report = {'releaseReady': False, 'published': False, 'serverAcceptanceReceived': False,
              'version': recipe['version'], 'sequence': recipe['sequence'],
              'baseVersion': old['version'], 'baseEnvelopeSha256': recipe['baseEnvelopeSha256'],
              'standardPackage': pack.name, 'standardPackageSha256': hashlib.sha256(pack.read_bytes()).hexdigest(),
              'standardReferences': len(index['files']), 'bundledModJars': 0,
              'nativeFiles': len(files), 'added': added, 'changed': changed,
              'removed': recipe['remove'], 'officialOnly': [f['path'] for f in files.values() if f.get('officialOnly')],
              'mapsAndPlayerFilesTouched': False, 'oldArtifactsOverwritten': False,
              'pending': ['server migration and startup acceptance', 'complete archive build',
                          'launcher support for officialOnly and minimum numeric version 0.1.2.12',
                          'real game join and interface observation']}
    (output / 'preparation.json').write_bytes(encode_json(report))
    return report


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--baseline', type=Path, required=True)
    parser.add_argument('--standard', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    print(json.dumps(prepare(args.baseline, args.standard, args.output), ensure_ascii=True))
