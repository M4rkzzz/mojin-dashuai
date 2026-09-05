"""Resolve identical local JARs to their author-published Modrinth files.

This only records origins and licenses. It does not upload JARs or mark a pack ready.
"""
import concurrent.futures
import datetime
import json
from pathlib import Path
import urllib.parse
import urllib.request

ROOT = Path(__file__).resolve().parents[1]
HEADERS = {'User-Agent': 'M4rkzzz/mojin-dashuai (exact pack source verification)'}

def request(path, body=None):
    data = None if body is None else json.dumps(body).encode()
    req = urllib.request.Request('https://api.modrinth.com/v2/' + path, data=data,
                                 headers={**HEADERS, 'Content-Type': 'application/json'})
    with urllib.request.urlopen(req, timeout=30) as response:
        return json.load(response)

def main():
    audits = {name: json.loads((ROOT / 'packs' / f'{name}-source-audit.json').read_text(encoding='utf-8')) for name in ('m3e', 'mb')}
    hashes = sorted({row['sha1'] for audit in audits.values() for row in audit['files'] if row.get('sha1')})
    versions = request('version_files', {'hashes': hashes, 'algorithm': 'sha1'})
    ids = sorted({version['project_id'] for version in versions.values()})
    with concurrent.futures.ThreadPoolExecutor(max_workers=4) as executor:
        projects = dict(zip(ids, executor.map(lambda project: request('project/' + urllib.parse.quote(project, safe='')), ids)))
    checked = datetime.datetime.now(datetime.timezone.utc).isoformat()
    for name, audit in audits.items():
        resolved = 0
        for row in audit['files']:
            version = versions.get(row.get('sha1'))
            if version is None:
                continue
            matches = [file for file in version['files'] if file.get('hashes', {}).get('sha1') == row['sha1'] and file.get('size') == row['size']]
            if len(matches) != 1:
                continue
            file = matches[0]
            origin = urllib.parse.urlparse(file['url'])
            if origin.scheme != 'https' or origin.hostname != 'cdn.modrinth.com' or origin.username or origin.password:
                continue
            project = projects[version['project_id']]
            license_info = project.get('license', {})
            row['sources'] = list(dict.fromkeys([file['url'], *row.get('sources', [])]))
            row['distributionBasis'] = 'Download the identical author-published file directly from Modrinth; no rehosting. Project license: ' + license_info.get('id', 'not specified')
            row['originEvidence'] = {
                'provider': 'modrinth', 'projectId': project['id'], 'versionId': version['id'],
                'projectUrl': 'https://modrinth.com/mod/' + project['slug'],
                'metadataUrl': 'https://api.modrinth.com/v2/version/' + version['id'],
                'fileSha1': row['sha1'], 'fileSha512': file['hashes'].get('sha512'),
                'license': license_info, 'checkedAt': checked,
            }
            row['status'] = 'origin-identified-download-verification-pending'
            resolved += 1
        audit['releaseReady'] = False
        (ROOT / 'packs' / f'{name}-source-audit.json').write_text(json.dumps(audit, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
        print(json.dumps({'instance': name, 'exactAuthorOrigins': resolved, 'files': len(audit['files']), 'releaseReady': False}))

if __name__ == '__main__':
    main()
