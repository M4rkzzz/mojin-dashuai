"""Publish exact operator-provided files when the upstream source is unavailable."""
import hashlib
import io
import json
from pathlib import Path
import subprocess
import tarfile
import urllib.request
import uuid
import zipfile
from pack_distribution import ROOT, safe_path

OPERATOR_BASIS = 'Operator-provided client baseline; operator requested automatic self-hosted fallback on 2026-09-05. Original bytes and embedded notices retained.'


def embedded_notice(data, filename):
    parts = [f'File: {filename}\nSource: operator-provided client baseline\n\n'.encode('utf-8')]
    try:
        with zipfile.ZipFile(io.BytesIO(data)) as jar:
            for entry in jar.infolist():
                if entry.filename.rsplit('/', 1)[-1].lower() in ('license', 'license.txt', 'notice', 'notice.txt', 'copying') and entry.file_size <= 256000:
                    parts.extend([('\n' + entry.filename + '\n').encode('utf-8'), jar.read(entry), b'\n'])
    except zipfile.BadZipFile:
        pass
    return b''.join(parts)


def publish(items):
    """One transfer, server-side SHA256, and public availability checks; no account tokens."""
    if not items:
        print(json.dumps({'publishedFallbackFiles': 0, 'missingFiles': 0}), flush=True)
        return []
    stage = ROOT / '.local/fallback-publication'
    stage.mkdir(parents=True, exist_ok=True)
    run = uuid.uuid4().hex
    archive = stage / (run + '.tar')
    objects = {}
    for item in items:
        row = item['row']
        data = item['file'].read_bytes()
        sha = hashlib.sha256(data).hexdigest()
        if sha != row['sha256'] or len(data) != row['size']:
            raise ValueError('Fallback differs from the pinned file: ' + row['path'])
        objects['objects/' + sha + '.jar'] = data
        objects['objects/' + sha + '.SOURCE.txt'] = item.get('notice') or embedded_notice(data, row['path'].rsplit('/', 1)[-1])
    with tarfile.open(archive, 'w') as tar:
        for name, data in objects.items():
            entry = tarfile.TarInfo(name)
            entry.size, entry.mode = len(data), 0o644
            tar.addfile(entry, io.BytesIO(data))
    expected = {name: {'size': len(data), 'sha256': hashlib.sha256(data).hexdigest()} for name, data in objects.items()}
    remote = '/tmp/mojin-fallback-' + run
    script = stage / (run + '.py')
    script.write_text('''import pathlib,tarfile,hashlib,json
root=pathlib.Path('/vol1/mc-client-hub/public').resolve()
expected=json.loads(EXPECTED)
with tarfile.open(ARCHIVE) as tar:
 members=tar.getmembers()
 if len(members)!=len(expected) or {m.name for m in members}!=set(expected) or any(not m.isfile() for m in members):raise ValueError('Unexpected publication entry')
 for member in members:
  info=expected[member.name]
  if member.size!=info['size']:raise ValueError('Unexpected size')
  data=tar.extractfile(member).read()
  if hashlib.sha256(data).hexdigest()!=info['sha256']:raise ValueError('Object hash mismatch')
  target=root/member.name
  if target.is_symlink() or not target.resolve().is_relative_to(root):raise ValueError('Unsafe target')
  target.parent.mkdir(parents=True,exist_ok=True)
  if target.exists():
   if hashlib.sha256(target.read_bytes()).hexdigest()!=info['sha256']:raise ValueError('Immutable object conflict')
   continue
  temp=target.with_name(target.name+'.stage-'+RUN)
  with temp.open('xb') as output:output.write(data)
  temp.chmod(0o644)
  temp.replace(target)
pathlib.Path(ARCHIVE).unlink()
print('Published verified objects')
'''.replace('EXPECTED', repr(json.dumps(expected))).replace('ARCHIVE', repr(remote + '.tar')).replace('RUN', repr(run)), encoding='utf-8')
    helper = ROOT.parent / 'tools/ssh124.py'

    def ssh(*arguments):
        result = subprocess.run(['python', str(helper), '--user', 'Agent2', *arguments], stdout=subprocess.PIPE, stderr=subprocess.PIPE, timeout=180)
        if result.returncode:
            raise RuntimeError('Fallback upload did not complete; staged files remain available')

    print(json.dumps({'uploadFiles': len(items), 'uploadBytes': archive.stat().st_size}), flush=True)
    ssh('--send', str(archive), remote + '.tar')
    ssh('--send', str(script), remote + '.py')
    ssh('--sudo', '--timeout', '60', 'python3 ' + remote + '.py')
    completed = []
    for item in items:
        row = item['row']
        sha = row['sha256']
        base = 'https://launcher.boshan.uk/objects/' + sha
        request = urllib.request.Request(base + '.jar', method='HEAD', headers={'User-Agent': 'MojinDashuai-Release/0.1'})
        with urllib.request.urlopen(request, timeout=25) as response:
            if response.status != 200 or int(response.headers['Content-Length']) != row['size']:
                raise ValueError('Published object unavailable: ' + row['path'])
        fallback = {'url': base + '.jar', 'frpUrl': 'http://103.40.14.100:21708/objects/' + sha + '.jar',
                    'noticeUrl': base + '.SOURCE.txt', 'distributionBasis': item.get('basis') or OPERATOR_BASIS,
                    'publishedVerified': True, 'serverSha256Verified': True, 'publicSizeVerified': True}
        (stage / (sha + '.json')).write_text(json.dumps(fallback, indent=2) + '\n', encoding='utf-8')
        audit_path = ROOT / f'packs/{item["instance"]}-source-audit.json'
        audit = json.loads(audit_path.read_text(encoding='utf-8'))
        current = next(r for r in audit['files'] if r['path'] == row['path'] and r['sha256'] == sha)
        current['fallback'] = fallback
        current['status'] = 'operator-fallback-available'
        audit_path.write_text(json.dumps(audit, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
        completed.append({'instance': item['instance'], 'path': row['path'], 'url': fallback['url']})
    print(json.dumps({'publishedFallbackFiles': len(completed)}), flush=True)
    return completed


def publish_missing(config, instances):
    items = []
    for instance in instances:
        spec = config['instances'][instance]
        source = Path(spec['source'])
        if not source.is_absolute():
            source = (ROOT / source).resolve()
        audit = json.loads((ROOT / f'packs/{instance}-source-audit.json').read_text(encoding='utf-8'))
        for row in audit['files']:
            if row.get('fallback', {}).get('publishedVerified') or (row.get('sources') and row.get('downloadVerification', {}).get('verified')):
                continue
            relative = safe_path(row['path'])
            local = source / relative
            if instance == 'dc2' or not local.is_file():
                local = ROOT / '.local/source-cache' / (row['sha256'] + '.jar')
            items.append({'instance': instance, 'row': row, 'file': local})
    return publish(items)
