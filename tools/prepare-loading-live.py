"""Prepare independent local loading-window test copies from installed content/cache, never production."""
from pathlib import Path
import concurrent.futures
import hashlib
import json
import shutil
import urllib.request

root = Path(__file__).resolve().parents[1]
target = root / '.local/loading-live-20260906'
if target.exists() and not (target/'.preparing').exists():
    raise SystemExit('Test copy already exists; use its prepared.json rather than overwriting it.')
target.mkdir(parents=True, exist_ok=True)
(target/'.preparing').write_text('loading-window-local-copy', encoding='utf-8')
complete = root / '.local/complete-client-qa/全新整包安装'
audit = root.parent / '_voidwayfarer4-deploy/r2-client-audit/中文客户端'
sources = {'m3e': complete/'instances/m3e', 'dc2': complete/'instances/dc2', 'mb': complete/'instances/mb', 'vw': audit/'instances/vw'}
caches = [complete/'cache', root/'.local/vw-r1-install-qa/cache', root/'.local/source-cache', root/'.local/complete-client-qa/r3到r4实际更新/cache']
report = {}
for instance, source in sources.items():
    manifest = json.loads((root/f'artifacts/native/{instance}-manifest.json').read_text(encoding='utf-8-sig'))
    previous_file = source/'.hub/installed.json'
    previous = json.loads(previous_file.read_text(encoding='utf-8-sig'))['manifest'] if previous_file.exists() else {'files': []}
    known = {row['path']: row['sha256'] for row in previous['files']}
    instance_root = target/'instances'/instance
    instance_root.mkdir(parents=True, exist_ok=True)
    resolved_root = instance_root.resolve()
    def copy(row):
        relative, sha = row['path'], row['sha256']
        if Path(relative).is_absolute() or '..' in Path(relative).parts or Path(relative).drive:
            raise ValueError('Invalid relative manifest path')
        destination = target/'instances'/instance/relative
        destination.parent.mkdir(parents=True, exist_ok=True)
        resolved_destination = destination.resolve()
        if not resolved_destination.is_relative_to(resolved_root):
            raise ValueError('Path escapes test instance: ' + str(resolved_destination) + ' vs ' + str(resolved_root))
        candidates = [source/relative, *(cache/sha for cache in caches), root/'.local/engines'/instance/relative]
        for candidate in candidates:
            if not candidate.is_file() or candidate.stat().st_size != row['size']:
                continue
            # Existing accepted managed inventory is sufficient for unchanged source files.
            if candidate != source/relative or known.get(relative) != sha:
                if hashlib.sha256(candidate.read_bytes()).hexdigest() != sha:
                    continue
            shutil.copyfile(candidate, destination)
            return 0
        # Small changed objects only: the exact immutable object from our unified service.
        address = 'https://launcher-direct.boshan.uk:21708/objects/sha256/' + sha
        with urllib.request.urlopen(address, timeout=30) as response:
            data = response.read(row['size'] + 1)
        if len(data) != row['size'] or hashlib.sha256(data).hexdigest() != sha:
            raise ValueError('Unified object mismatch: ' + relative)
        destination.write_bytes(data)
        return len(data)
    with concurrent.futures.ThreadPoolExecutor(max_workers=8) as pool:
        downloaded = sum(pool.map(copy, manifest['files']))
    java = complete/'runtimes'/manifest['runtime']['archive']['sha256']/manifest['runtime']['javaPath']
    if not java.is_file():
        raise FileNotFoundError('Prepared runtime missing: ' + str(java))
    report[instance] = {'manifest': str(root/f'artifacts/native/{instance}-manifest.json'), 'java': str(java),
                        'version': manifest['version'], 'files': len(manifest['files']), 'downloadedBytes': downloaded}
    (target/'prepared.json').write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding='utf-8')
    print(instance, report[instance], flush=True)
(target/'.preparing').unlink()
