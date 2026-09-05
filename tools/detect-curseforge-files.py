"""Find exact supplied JARs; fingerprints are only a lookup, SHA1/size are identity."""
import hashlib
import importlib.util
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
SOURCE = Path('D:/Desktop/魔金大帅/.minecraft/versions/MSE')
spec = importlib.util.spec_from_file_location('cf_sources', ROOT / 'tools/resolve-curseforge-sources.py')
cf = importlib.util.module_from_spec(spec)
spec.loader.exec_module(cf)


def fingerprint(data):
    data = data.translate(None, b'\t\n\r ')
    mask = 0xffffffff
    m = 0x5bd1e995
    h = (1 ^ len(data)) & mask
    end = len(data) & ~3
    for i in range(0, end, 4):
        k = int.from_bytes(data[i:i+4], 'little')
        k = k * m & mask
        k ^= k >> 24
        k = k * m & mask
        h = (h * m & mask) ^ k
    if len(data) > end:
        h ^= int.from_bytes(data[end:], 'little')
        h = h * m & mask
    h ^= h >> 13
    h = h * m & mask
    return (h ^ (h >> 15)) & mask


def main():
    path = ROOT / 'packs/m3e-source-audit.json'
    audit = json.loads(path.read_text(encoding='utf-8'))
    rows = {r['path']: r for r in audit['files']}
    lookups = {}
    for file in sorted((SOURCE / 'mods').rglob('*.jar')):
        relative = file.relative_to(SOURCE).as_posix()
        data = file.read_bytes()
        sha256 = hashlib.sha256(data).hexdigest()
        if relative not in rows:
            rows[relative] = {'path': relative, 'sha256': sha256, 'sha1': hashlib.sha1(data).hexdigest(),
                             'size': len(data), 'sources': [], 'distributionBasis': None,
                             'status': 'needs-source-and-license-review'}
        if rows[relative]['sha256'] != sha256:
            raise ValueError('Supplied baseline changed: ' + relative)
        if not rows[relative].get('projectId'):
            lookups[fingerprint(data)] = rows[relative]
    ids = list(lookups)
    matched = 0
    cache = ROOT / '.local/curseforge-metadata'
    cache.mkdir(parents=True, exist_ok=True)
    for start in range(0, len(ids), 30):
        result = cf.request('fingerprints', {'fingerprints': ids[start:start + 30]})
        for match in result.get('exactMatches', []):
            file = match['file']
            row = lookups.get(file.get('fileFingerprint'))
            if row is None:
                continue
            sha1 = next((h['value'] for h in file['hashes'] if h['algo'] == 1), None)
            if sha1 != row['sha1'] or file['fileLength'] != row['size']:
                continue
            row.update({'projectId': file['modId'], 'fileId': file['id']})
            (cache / f"{file['id']}.json").write_text(json.dumps(file), encoding='utf-8')
            matched += 1
        print(json.dumps({'checked': min(start+30, len(ids)), 'matched': matched}), flush=True)
    audit['files'] = list(rows.values())
    audit['releaseReady'] = False
    path.write_text(json.dumps(audit, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')


if __name__ == '__main__':
    main()
