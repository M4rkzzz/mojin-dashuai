"""Resolve pinned CurseForge IDs through its documented public MCIM metadata cache.

No API keys are used. The cache is metadata provenance, not evidence of permission
to redistribute a JAR. Existing local hashes must match before accepting a result.
"""
import argparse
import datetime
import json
from pathlib import Path
import urllib.request

ROOT = Path(__file__).resolve().parents[1]
BASE = 'https://mod.mcimirror.top/curseforge/v1/'


def request(path, body):
    req = urllib.request.Request(BASE + path, data=json.dumps(body).encode(), headers={
        'Content-Type': 'application/json', 'User-Agent': 'MojinDashuai-PackBuilder/0.1'})
    with urllib.request.urlopen(req, timeout=45) as response:
        data = json.load(response)
    return data.get('data', data) if isinstance(data, dict) else data


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--instances', nargs='+', choices=['m3e', 'dc2', 'mb'], default=['dc2', 'mb'])
    args = parser.parse_args()
    for name in args.instances:
        path = ROOT / f'packs/{name}-source-audit.json'
        audit = json.loads(path.read_text(encoding='utf-8'))
        ids = sorted({r['fileId'] for r in audit['files'] if r.get('fileId')})
        metadata = {}
        cache = ROOT / '.local/curseforge-metadata'
        cache.mkdir(parents=True, exist_ok=True)
        missing = []
        for file_id in ids:
            cached = cache / f'{file_id}.json'
            if cached.exists():
                metadata[file_id] = json.loads(cached.read_text())
            else:
                missing.append(file_id)
        for start in range(0, len(missing), 50):
            for item in request('mods/files', {'fileIds': missing[start:start + 50]}):
                if item['id'] not in ids:
                    raise ValueError('Unrequested file metadata')
                metadata[item['id']] = item
                (cache / f"{item['id']}.json").write_text(json.dumps(item), encoding='utf-8')
            print(json.dumps({'instance': name, 'metadata': len(metadata), 'requested': len(ids)}), flush=True)
        matched = 0
        for row in audit['files']:
            item = metadata.get(row.get('fileId'))
            if not item:
                continue
            hashes = {h['algo']: h['value'].lower() for h in item.get('hashes', [])}
            filename = item['fileName']
            if item['modId'] != row['projectId'] or '/' in filename or '\\' in filename:
                raise ValueError('File identity mismatch')
            if row.get('sha1') and (hashes.get(1) != row['sha1'] or item['fileLength'] != row['size']):
                row['curseforgeMismatch'] = True
                continue
            modules = {m['name'] for m in item.get('modules', [])}
            folder = ('mods' if filename.lower().endswith('.jar') else
                      'resourcepacks' if 'pack.mcmeta' in modules else
                      'shaderpacks' if 'shaders' in modules else None)
            if not folder:
                raise ValueError('Unrecognized required pack file type: ' + str(item['id']))
            if name == 'dc2' or not row.get('path'):
                row['path'] = folder + '/' + filename
            row.setdefault('size', item['fileLength'])
            row.setdefault('sha1', hashes.get(1))
            if item.get('downloadUrl'):
                row['sources'] = list(dict.fromkeys([*row.get('sources', []), item['downloadUrl']]))
            row['curseforgeEvidence'] = {
                'provider': 'mcim-public-metadata-cache', 'metadataUrl': BASE + f"mods/{item['modId']}/files/{item['id']}",
                'projectId': item['modId'], 'fileId': item['id'], 'fileName': filename,
                'fileSha1': hashes.get(1), 'fileMd5': hashes.get(2),
                'checkedAt': datetime.datetime.now(datetime.timezone.utc).isoformat()}
            if not row.get('distributionBasis'):
                row['distributionBasis'] = 'Pinned author file on CurseForge; direct download only. Metadata resolved via the public MCIM cache; redistribution is not implied.'
            matched += 1
        audit['releaseReady'] = False
        path.write_text(json.dumps(audit, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
        print(json.dumps({'instance': name, 'matched': matched, 'total': len(audit['files'])}), flush=True)


if __name__ == '__main__':
    main()
