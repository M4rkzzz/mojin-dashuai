"""Publish one reviewed, exact pack file to the existing download service.

The public object name is its SHA256. Account secrets never accompany downloads.
File-level redistribution evidence and the accompanying notice are required.
"""
import argparse
import hashlib
import io
import json
from pathlib import Path
import subprocess
import tarfile
import urllib.request
import uuid

ROOT = Path(__file__).resolve().parents[1]
parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('instance', choices=['m3e', 'dc2', 'mb'])
parser.add_argument('path', help='Exact relative path in the source audit')
parser.add_argument('--file', type=Path, required=True)
parser.add_argument('--basis', required=True, help='Specific license or permission evidence; availability is not permission')
parser.add_argument('--notice', type=Path, required=True, help='License/attribution text to accompany this object')
parser.add_argument('--publish', action='store_true')
args = parser.parse_args()
audit_path = ROOT / f'packs/{args.instance}-source-audit.json'
audit = json.loads(audit_path.read_text(encoding='utf-8'))
matches = [r for r in audit['files'] if r['path'] == args.path]
if len(matches) != 1:
    raise SystemExit('Expected one pinned audit entry')
row = matches[0]
data = args.file.read_bytes()
sha = hashlib.sha256(data).hexdigest()
if row.get('sha256') != sha or row['size'] != len(data) or row['sha1'] != hashlib.sha1(data).hexdigest():
    raise SystemExit('Fallback differs from the pinned file')
notice = args.notice.read_bytes()
if not 20 <= len(notice) <= 512000 or not args.basis.strip():
    raise SystemExit('A meaningful distribution notice is required')
notice.decode('utf-8-sig')
stage = ROOT / '.local/fallback-publication'
stage.mkdir(parents=True, exist_ok=True)
run = uuid.uuid4().hex
archive = stage / (run + '.tar')
with tarfile.open(archive, 'w') as tar:
    for name, payload in [('objects/' + sha + '.jar', data), ('objects/' + sha + '.LICENSE.txt', notice)]:
        info = tarfile.TarInfo(name)
        info.size = len(payload)
        info.mode = 0o644
        tar.addfile(info, io.BytesIO(payload))
if not args.publish:
    print(json.dumps({'staged': str(archive), 'sha256': sha, 'published': False}))
    raise SystemExit()
helper = ROOT.parent / 'tools/ssh124.py'
remote = '/tmp/mojin-object-' + run + '.tar'
script = stage / (run + '.py')
script.write_text('''import hashlib, pathlib, tarfile
root=pathlib.Path('/vol1/mc-client-hub/public').resolve()
archive=pathlib.Path(ARCHIVE)
expected={'objects/'+SHA+'.jar','objects/'+SHA+'.LICENSE.txt'}
with tarfile.open(archive) as tar:
 members=tar.getmembers()
 if len(members)!=2 or {m.name for m in members}!=expected or any(not m.isfile() for m in members):raise ValueError('Invalid object publication')
 payloads={m.name:tar.extractfile(m).read() for m in members}
 if hashlib.sha256(payloads['objects/'+SHA+'.jar']).hexdigest()!=SHA:raise ValueError('Object hash mismatch')
 for name,data in payloads.items():
  target=root/name
  if not target.resolve().is_relative_to(root) or target.is_symlink():raise ValueError('Unsafe target')
  target.parent.mkdir(parents=True,exist_ok=True)
  if target.exists():
   if target.read_bytes()!=data:raise ValueError('Immutable object conflict')
   continue
  temp=target.with_suffix(target.suffix+'.stage-'+RUN)
  with temp.open('xb') as stream:stream.write(data)
  temp.chmod(0o644)
  temp.replace(target)
archive.unlink()
print('Immutable object published')
'''.replace('ARCHIVE', repr(remote)).replace('SHA', repr(sha)).replace('RUN', repr(run)), encoding='utf-8')

def ssh(*arguments):
    subprocess.run(['python', str(helper), '--user', 'Agent2', *arguments], check=True,
                   stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)

ssh('--send', str(archive), remote)
ssh('--send', str(script), '/tmp/mojin-publish-object-' + run + '.py')
ssh('--sudo', '--timeout', '25', 'python3 /tmp/mojin-publish-object-' + run + '.py')
base = 'https://launcher.boshan.uk/objects/' + sha
with urllib.request.urlopen(urllib.request.Request(base + '.jar', headers={'User-Agent': 'MojinDashuai-PublicVerifier/0.1'}), timeout=30) as response:
    downloaded = response.read(len(data) + 1)
if hashlib.sha256(downloaded).hexdigest() != sha or len(downloaded) != len(data):
    raise SystemExit('Public download did not match; audit unchanged')
with urllib.request.urlopen(urllib.request.Request(base + '.jar', headers={'Range': 'bytes=0-31'}), timeout=30) as response:
    if response.status != 206 or response.read(33) != data[:32]:
        raise SystemExit('Public Range check failed; audit unchanged')
with urllib.request.urlopen(base + '.LICENSE.txt', timeout=30) as response:
    if response.read(len(notice) + 1) != notice:
        raise SystemExit('Public notice differs; audit unchanged')
# Re-read so an independent completed source resolver is not overwritten.
audit = json.loads(audit_path.read_text(encoding='utf-8'))
row = next(r for r in audit['files'] if r['path'] == args.path and r['sha256'] == sha)
row['fallback'] = {'url': base + '.jar', 'frpUrl': 'http://103.40.14.100:21708/objects/' + sha + '.jar',
                   'noticeUrl': base + '.LICENSE.txt', 'distributionBasis': args.basis,
                   'publishedVerified': True, 'rangeVerified': True}
(stage / (sha + '.json')).write_text(json.dumps(row['fallback'], indent=2) + '\n', encoding='utf-8')
audit['releaseReady'] = False
audit_path.write_text(json.dumps(audit, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
print(json.dumps({'instance': args.instance, 'sha256': sha, 'published': True, 'httpsHashVerified': True, 'rangeVerified': True}))
