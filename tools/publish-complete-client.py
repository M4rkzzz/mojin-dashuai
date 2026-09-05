"""Publish an already verified private complete archive and missing hash objects, without activating a catalog."""
import argparse
import hashlib
import json
from pathlib import Path
import subprocess
import uuid

ROOT = Path(__file__).resolve().parents[1]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--candidate', type=Path, required=True)
    parser.add_argument('--remote-archive', required=True)
    args = parser.parse_args()
    candidate = args.candidate.resolve()
    manifest_path = candidate / 'manifest.candidate.json'
    manifest = json.loads(manifest_path.read_text(encoding='utf-8-sig'))
    report = json.loads((candidate / 'report.json').read_text(encoding='utf-8-sig'))
    bundles = manifest.get('bundles', [])
    if len(bundles) != 1 or not bundles[0].get('complete') or bundles[0]['prefix']:
        raise ValueError('A single complete root archive is required')
    archive = bundles[0]['archive']
    # Revalidate bytes and every entry remotely; the build report remains a separate receipt.
    if not report.get('candidate'):
        raise ValueError('Candidate build did not pass')
    run = uuid.uuid4().hex
    stage = ROOT / '.local/complete-publication' / run
    stage.mkdir(parents=True)
    remote = '/tmp/mojin-complete-publish-' + run
    spec = {'manifest': manifest, 'source': args.remote_archive,
            'manifestSha256': hashlib.sha256(manifest_path.read_bytes()).hexdigest()}
    (stage / 'input.json').write_text(json.dumps(spec), encoding='utf-8')
    script = r'''import hashlib,json,os,pathlib,shutil,zipfile
spec=json.loads(pathlib.Path(BASE+'.json').read_text())
manifest=spec['manifest'];bundle=manifest['bundles'][0]
private=pathlib.Path('/vol1/mc-client-hub/staging/complete-client-20260905').resolve()
source=pathlib.Path(spec['source'])
if source.is_symlink() or not source.resolve().is_relative_to(private) or not source.is_file():raise ValueError('Archive outside private staging')
root=pathlib.Path('/vol1/mc-client-hub/public').resolve()
objects=root/'objects/sha256'
if objects.is_symlink() or not objects.resolve().is_relative_to(root):raise ValueError('Unsafe object root')
def digest(path):
 with path.open('rb') as stream:return hashlib.file_digest(stream,'sha256').hexdigest()
verified_existing=set()
def target(file):
 value=file['sha256']
 if len(value)!=64 or any(c not in '0123456789abcdef' for c in value):raise ValueError('Invalid hash')
 result=objects/value
 if result.is_symlink() or not result.resolve().is_relative_to(root):raise ValueError('Unsafe object path')
 if result.exists() and (not result.is_file() or result.stat().st_size!=file['size']):raise ValueError('Existing object differs')
 if result.exists() and value not in verified_existing:
  if digest(result)!=value:raise ValueError('Existing object hash differs')
  verified_existing.add(value)
 return result
archive=bundle['archive']
if source.stat().st_size!=archive['size'] or digest(source)!=archive['sha256']:raise ValueError('Archive hash mismatch')
expected={f['path']:f for f in manifest['files'] if not f.get('officialOnly')}
expected['__runtime/runtime.zip']=manifest['runtime']['archive']
official={f['sha256'] for f in manifest['files'] if f.get('officialOnly')}
if any(f['sha256'] in official for f in expected.values()):raise ValueError('Official-only alias in archive')
published=0
with zipfile.ZipFile(source) as archive_file:
 entries=archive_file.infolist()
 if len(entries)!=len(expected) or {e.filename for e in entries}!=set(expected):raise ValueError('Archive inventory differs')
 for entry in entries:
  file=expected[entry.filename]
  if entry.is_dir() or ((entry.external_attr>>16)&0xF000)==0xA000 or entry.file_size!=file['size']:raise ValueError('Invalid archive entry')
  destination=target(file);temporary=destination.with_name(destination.name+'.stage-'+RUN)
  hasher=hashlib.sha256()
  try:
   output=None if destination.exists() else temporary.open('xb')
   try:
    with archive_file.open(entry) as incoming:
     while chunk:=incoming.read(1048576):
      hasher.update(chunk)
      if output:output.write(chunk)
   finally:
    if output:output.close()
   if hasher.hexdigest()!=file['sha256']:raise ValueError('Archive entry hash mismatch')
   if output:
    temporary.chmod(0o644);temporary.replace(destination);published+=1
  finally:
   if temporary.exists():temporary.unlink()
destination=target(archive)
if destination.exists():
 if digest(destination)!=archive['sha256']:raise ValueError('Existing archive hash mismatch')
else:
 temporary=destination.with_name(destination.name+'.stage-'+RUN)
 try:
  shutil.copyfile(source,temporary)
  if digest(temporary)!=archive['sha256']:raise ValueError('Copied archive hash mismatch')
  temporary.chmod(0o644);temporary.replace(destination);published+=1
 finally:
  if temporary.exists():temporary.unlink()
print(json.dumps({'instance':manifest['instance'],'version':manifest['version'],'archiveSha256':archive['sha256'],'archiveBytes':archive['size'],'verifiedEntries':len(expected),'newObjects':published,'officialOnlyFiles':[f['path'] for f in manifest['files'] if f.get('officialOnly')],'catalogActivated':False,'gameServersTouched':False}))
'''.replace('BASE', repr(remote)).replace('RUN', repr(run))
    (stage / 'publish.py').write_text(script, encoding='utf-8')

    def ssh(*parameters):
        result = subprocess.run(['python', str(ROOT.parent / 'tools/ssh124.py'), '--user', 'Agent2', *parameters],
                                capture_output=True, timeout=600)
        if result.returncode:
            raise RuntimeError('Complete archive publication failed; isolated receipt retained at ' + str(stage))
        return result.stdout.decode('utf-8', errors='replace').strip()

    ssh('--send', str(stage / 'input.json'), remote + '.json')
    ssh('--send', str(stage / 'publish.py'), remote + '.py')
    output = ssh('--sudo', '--timeout', '540', 'python3 ' + remote + '.py')
    (stage / 'receipt.txt').write_text(output + '\n', encoding='utf-8')
    print(output)


if __name__ == '__main__':
    main()
