"""Publish staged standard packages and native dependencies without changing the signed catalog."""
import argparse
import hashlib
import json
from pathlib import Path
import subprocess
import tarfile
import urllib.request
import uuid
from pack_distribution import ROOT, safe_path

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--standard', action='store_true')
parser.add_argument('--native', action='store_true')
parser.add_argument('--only', nargs='+', help='Publish only these staged public paths (for small content corrections)')
args = parser.parse_args()
if not (args.standard or args.native): parser.error('select --standard and/or --native')
config = json.loads((ROOT / 'packs/distributions.json').read_text(encoding='utf-8'))
files, probes = {}, []
for selected, base in [(args.standard, ROOT / 'artifacts/distributions/public'), (args.native, ROOT / 'artifacts/native/public')]:
    if selected:
        for path in base.rglob('*'):
            if path.is_file(): files[safe_path(path.relative_to(base).as_posix())] = path
if args.standard:
    for instance, spec in config['instances'].items():
        report = json.loads((ROOT / f'artifacts/distributions/{instance}-report.json').read_text(encoding='utf-8'))
        if not report['candidate']: raise ValueError(instance + ' still has missing files')
        relative = f'distributions/{instance}/{spec["version"]}/{report["portableArtifact"]}'
        files[safe_path(relative)] = ROOT / 'artifacts/distributions' / report['portableArtifact']
        probes += [relative, f'distributions/{instance}/{spec["version"]}/pack.toml']
if args.only:
    selected_paths = {safe_path(path) for path in args.only}
    if selected_paths - files.keys(): raise ValueError('Requested publication path is not staged')
    files = {path: files[path] for path in selected_paths}
    probes = [path for path in probes if path in files]
if not files: raise ValueError('No staged content')
stage = ROOT / '.local/publication'; stage.mkdir(parents=True, exist_ok=True)
run = uuid.uuid4().hex
archive, metadata, script = [stage / (run + suffix) for suffix in ('.tar', '.json', '.py')]
inventory = {}
with tarfile.open(archive, 'w') as tar:
    for relative, path in sorted(files.items()):
        if path.is_symlink() or not relative.startswith(('objects/', 'distributions/')): raise ValueError('Unsafe publication input')
        with path.open('rb') as source: sha = hashlib.file_digest(source, 'sha256').hexdigest()
        inventory[relative] = {'size': path.stat().st_size, 'sha256': sha}
        tar.add(path, arcname=relative, recursive=False)
metadata.write_text(json.dumps(inventory), encoding='utf-8')
remote = '/tmp/mojin-content-' + run
script.write_text('''import pathlib,tarfile,json,hashlib
root=pathlib.Path('/vol1/mc-client-hub/public').resolve()
expected=json.loads(pathlib.Path(BASE+'.json').read_text())
count=0
with tarfile.open(BASE+'.tar','r|') as archive:
 for entry in archive:
  if not entry.isfile() or entry.name not in expected:raise ValueError('Unexpected archive entry')
  info=expected.pop(entry.name)
  if entry.size!=info['size']:raise ValueError('File size mismatch')
  target=root/entry.name
  if target.is_symlink() or not target.resolve().is_relative_to(root):raise ValueError('Unsafe publication path')
  target.parent.mkdir(parents=True,exist_ok=True)
  temp=target.with_name(target.name+'.stage-'+RUN)
  digest=hashlib.sha256()
  with archive.extractfile(entry) as source,temp.open('xb') as output:
   while chunk:=source.read(1048576):digest.update(chunk);output.write(chunk)
  if digest.hexdigest()!=info['sha256']:temp.unlink();raise ValueError('File hash mismatch')
  if target.exists():
   with target.open('rb') as source:old=hashlib.file_digest(source,'sha256').hexdigest()
   if old!=info['sha256']:temp.unlink();raise ValueError('Existing immutable release differs: '+entry.name)
   temp.unlink()
  else:temp.chmod(0o644);temp.replace(target)
  count+=1
if expected:raise ValueError('Archive incomplete')
pathlib.Path(BASE+'.tar').unlink()
print(json.dumps({'publishedFiles':count}))
'''.replace('BASE', repr(remote)).replace('RUN', repr(run)), encoding='utf-8')
helper = ROOT.parent / 'tools/ssh124.py'
def ssh(*arguments):
    result = subprocess.run(['python', str(helper), '--user', 'Agent2', *arguments], capture_output=True, timeout=900)
    if result.returncode: raise RuntimeError('Content publication failed; inspect staged publication files')
    return result.stdout.decode('utf-8', errors='replace').strip()
print(json.dumps({'phase': 'upload', 'files': len(files), 'bytes': archive.stat().st_size}), flush=True)
for path, suffix in [(archive, '.tar'), (metadata, '.json'), (script, '.py')]: ssh('--send', str(path), remote + suffix)
print(ssh('--sudo', '--timeout', '600', 'python3 ' + remote + '.py'), flush=True)
if args.native:
    probes.extend(list(relative for relative in files if relative.startswith('objects/'))[:3])
for relative in probes:
    request = urllib.request.Request(config['publicBase'] + '/' + relative, method='HEAD')
    with urllib.request.urlopen(request, timeout=25) as response:
        if response.status != 200 or int(response.headers['Content-Length']) != files[relative].stat().st_size: raise ValueError('Public download unavailable')
report = {'publishedFiles': len(files), 'publicSampleChecks': len(probes), 'standard': args.standard, 'native': args.native, 'partial': bool(args.only), 'catalogChanged': False}
history_path=ROOT / 'packs/content-publication.json'
history=json.loads(history_path.read_text(encoding='utf-8')) if history_path.is_file() else {}
history.setdefault('publications',[]).append(report)
history['standardPublished']=(args.standard and not args.only) or history.get('standardPublished',False) or history.get('standard',False)
history['nativeDependenciesPublished']=(args.native and not args.only) or history.get('nativeDependenciesPublished',False) or history.get('native',False)
history['catalogChanged']=False
history_path.write_text(json.dumps(history, indent=2) + '\n', encoding='utf-8')
print(json.dumps(report), flush=True)
