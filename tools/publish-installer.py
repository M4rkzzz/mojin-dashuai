"""Publish a verified, immutable player installer through the existing download service."""
import argparse,hashlib,json,pathlib,re,subprocess,sys,urllib.request,uuid

ROOT=pathlib.Path(__file__).resolve().parents[1]
parser=argparse.ArgumentParser(description=__doc__)
parser.add_argument('directory',type=pathlib.Path)
parser.add_argument('--revision',default='',help='Immutable subdirectory for a user-requested same-version rebuild')
args=parser.parse_args();directory=args.directory.resolve()
hold=directory/'HOLD.json'
if hold.exists() and json.loads(hold.read_text(encoding='utf-8-sig')).get('publishAllowed') is not True:
    raise ValueError('This installer candidate is on hold; rebuild with the fourth server before publication.')
if args.revision and not re.fullmatch(r'[a-z0-9][a-z0-9-]{0,39}',args.revision):parser.error('Invalid revision')
record=json.loads((directory/'installer.json').read_text(encoding='utf-8-sig'))
version=record['version'];name=record['fileName']
if record.get('acceptanceFixture') or name!=f'MojinDashuai-Setup-{version}-x64.exe' or any(c not in '0123456789abcdefghijklmnopqrstuvwxyz.-' for c in version):raise ValueError('Invalid public installer')
source=directory/name
if not source.is_file() or source.is_symlink():raise ValueError('Installer not found')
with source.open('rb') as stream:digest=hashlib.file_digest(stream,'sha256').hexdigest()
if digest!=record['sha256'] or source.stat().st_size!=record['bytes']:raise ValueError('Installer hash mismatch')
acceptance=json.loads((ROOT/'packs/installer-acceptance.json').read_text(encoding='utf-8-sig'))
if not acceptance.get('passed') or acceptance['version']!=version:raise ValueError('Installer acceptance has not passed')
run=uuid.uuid4().hex;remote='/tmp/mojin-installer-'+run
stage=ROOT/'.local/publication';stage.mkdir(parents=True,exist_ok=True)
script=stage/(run+'.py')
script.write_text('''import hashlib,json,os,pathlib,shutil
source=pathlib.Path(SOURCE);root=pathlib.Path('/vol1/mc-client-hub/public').resolve()
with source.open('rb') as stream:digest=hashlib.file_digest(stream,'sha256').hexdigest()
if digest!=SHA or source.stat().st_size!=SIZE:raise ValueError('Uploaded installer differs')
target=root/'objects/sha256'/digest
named=root/'launcher'/VERSION/REVISION/NAME
for path in (target,named):
 if path.is_symlink() or not path.resolve().is_relative_to(root):raise ValueError('Unsafe public path')
 if path.exists():
  with path.open('rb') as stream:existing=hashlib.file_digest(stream,'sha256').hexdigest()
  if existing!=digest:raise ValueError('Published installer bytes are immutable')
target.parent.mkdir(parents=True,exist_ok=True)
if not target.exists():
 temp=target.with_name(digest+'.stage-'+RUN);shutil.copyfile(source,temp);temp.chmod(0o644);temp.replace(target)
named.parent.mkdir(parents=True,exist_ok=True)
if not named.exists():os.link(target,named)
source.unlink();print(json.dumps({'published':True,'version':VERSION}))
'''.replace('SOURCE',repr(remote+'.exe')).replace('SHA',repr(digest)).replace('SIZE',str(record['bytes'])).replace('VERSION',repr(version)).replace('REVISION',repr(args.revision)).replace('NAME',repr(name)).replace('RUN',repr(run)),encoding='utf-8')
def ssh(*parameters):
 result=subprocess.run([sys.executable,str(ROOT.parent/'tools/ssh124.py'),'--user','Agent2',*parameters],capture_output=True,timeout=180)
 if result.returncode:raise RuntimeError('Installer publication failed; credentials were not printed')
 return result.stdout.decode(errors='replace').strip()
ssh('--send',str(source),remote+'.exe');ssh('--send',str(script),remote+'.py')
print(ssh('--sudo','--timeout','90','python3 '+remote+'.py'))
config=json.loads((ROOT/'packs/distributions.json').read_text(encoding='utf-8'))
url=config['frpBase'].rstrip('/')+'/launcher/'+version+('/'+args.revision if args.revision else '')+'/'+name
# Read the actual player download back, rather than trusting a HEAD response.
with urllib.request.urlopen(url,timeout=45) as response:
 size=0;actual=hashlib.sha256()
 while block:=response.read(1024*1024):size+=len(block);actual.update(block)
if actual.hexdigest()!=digest or size!=record['bytes']:raise ValueError('Public installer download differs')
record.update(downloadUrl=url,publicDownloadVerified=True,publicArchiveRoundTripVerified=True,revision=args.revision)
(directory/'publication.json').write_text(json.dumps(record,indent=2)+'\n',encoding='utf-8')
print(json.dumps(record))
