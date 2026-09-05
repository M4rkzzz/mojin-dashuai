"""Download and verify the exact portable-format tools, outside the launcher package."""
import hashlib
import json
import urllib.request
from pack_distribution import ROOT, public_url, safe_path

for row in json.loads((ROOT / 'packs/format-tools.json').read_text(encoding='utf-8'))['files']:
    target = ROOT / '.local' / safe_path(row['file'])
    target.parent.mkdir(parents=True, exist_ok=True)
    data = target.read_bytes() if target.is_file() else b''
    if len(data) != row['size'] or hashlib.sha256(data).hexdigest() != row['sha256']:
        with urllib.request.urlopen(public_url(row['url']), timeout=30) as response:
            data = response.read(row['size'] + 1)
        if len(data) != row['size'] or hashlib.sha256(data).hexdigest() != row['sha256']:
            raise ValueError('Pinned tool verification failed: ' + row['file'])
        target.write_bytes(data)
    print(row['file'] + ': verified')
