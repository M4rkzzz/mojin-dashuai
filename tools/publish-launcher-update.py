"""Publish a legacy ZIP and immutable per-file update objects; activate only with --activate."""
import argparse,base64,hashlib,json,pathlib,subprocess,sys,time,urllib.error,urllib.parse,urllib.request,uuid

ROOT=pathlib.Path(__file__).resolve().parents[1]
parser=argparse.ArgumentParser(description=__doc__)
parser.add_argument('bundle',type=pathlib.Path)
parser.add_argument('--dotnet',default='dotnet')
parser.add_argument('--publisher',type=pathlib.Path,default=ROOT/'src/Publisher/bin/Release/net10.0/Publisher.dll')
parser.add_argument('--activate',action='store_true')
parser.add_argument('--beta',action='store_true',help='Use approved beta acceptance; clean Windows stays unverified')
args=parser.parse_args()
def public_open(request,timeout=25):
    # Match the player's direct route; transient handshakes retry the same URL.
    for attempt in range(3):
        try:return urllib.request.build_opener(urllib.request.ProxyHandler({})).open(request,timeout=timeout)
        except urllib.error.HTTPError:raise
        except (urllib.error.URLError,TimeoutError):
            if attempt==2:raise
            time.sleep(0.5*(attempt+1))
bundle=args.bundle.resolve()
signed=bundle/'launcher.signed.json'
subprocess.run([args.dotnet,str(args.publisher),'verify-launcher',str(signed),str(ROOT/'src/Launcher.Desktop/launcher.json')],check=True,cwd=ROOT)
envelope=json.loads(signed.read_text(encoding='utf-8-sig'))
release=json.loads(base64.b64decode(envelope['payload']))
archive=bundle/'MojinDashuai-windows-x64.zip'
with archive.open('rb') as f:digest=hashlib.file_digest(f,'sha256').hexdigest()
if digest!=release['archive']['sha256'].lower() or archive.stat().st_size!=release['archive']['size']:raise ValueError('Archive does not match signed launcher release')
if release['archive']['path']!='objects/sha256/'+digest:raise ValueError('Launcher object path must use its SHA256')
if args.beta and '-beta.' not in release['version']:raise ValueError('Beta publication requires a beta launcher version')
if args.activate:subprocess.run([sys.executable,str(ROOT/'tools/check-client-release.py'),*(['--beta'] if args.beta else [])],check=True,cwd=ROOT)
config=json.loads((ROOT/'packs/distributions.json').read_text(encoding='utf-8'))
expected=config['frpBase'].rstrip('/')+'/objects/sha256/'+digest
if expected not in release['archive']['sources']:raise ValueError('Launcher ZIP must include the configured direct download route')
if release.get('differential',False):
    for item in release['files']:
        object_url=config['frpBase'].rstrip('/')+'/objects/sha256/'+item['sha256'].lower()
        if object_url not in item['sources']:raise ValueError('Launcher file is missing the configured direct route')
run=uuid.uuid4().hex
stage=ROOT/'.local/publication';stage.mkdir(parents=True,exist_ok=True)
remote='/tmp/mojin-launcher-'+run
helper=ROOT.parent/'tools/ssh124.py'
def ssh(*parameters):
    result=subprocess.run([sys.executable,str(helper),'--user','Agent2',*parameters],capture_output=True,timeout=600)
    if result.returncode:raise RuntimeError('Launcher publication failed; no credentials were printed')
    return result.stdout.decode('utf-8',errors='replace').strip()
for path,suffix in [(archive,'.zip'),(signed,'.json')]:ssh('--send',str(path),remote+suffix)
script=stage/(run+'.py')
script.write_text('''import base64,hashlib,json,pathlib,shutil,datetime,os,zipfile
root=pathlib.Path('/vol1/mc-client-hub/public').resolve()
source=pathlib.Path(BASE+'.zip')
envelope=json.loads(pathlib.Path(BASE+'.json').read_text())
release=json.loads(base64.b64decode(envelope['payload']))
with source.open('rb') as f:sha=hashlib.file_digest(f,'sha256').hexdigest()
if sha!=HASH or source.stat().st_size!=SIZE:raise ValueError('Uploaded ZIP mismatch')
target=root/'objects/sha256'/sha
if target.is_symlink() or not target.resolve().is_relative_to(root):raise ValueError('Unsafe object path')
target.parent.mkdir(parents=True,exist_ok=True)
if target.exists():
 with target.open('rb') as f:old=hashlib.file_digest(f,'sha256').hexdigest()
 if old!=sha:raise ValueError('Existing immutable object differs')
else:
 staged=target.with_name(sha+'.stage-'+RUN);shutil.copyfile(source,staged);staged.chmod(0o644);staged.replace(target)
object_count=0
if release.get('differential',False):
 expected={item['path']:item for item in release['files']}
 seen=set()
 with zipfile.ZipFile(source) as archive:
  for entry in archive.infolist():
   if entry.is_dir():continue
   item=expected.get(entry.filename)
   if item is None or entry.filename in seen or entry.file_size!=item['size'] or (entry.external_attr>>16)&0o170000==0o120000:raise ValueError('Launcher ZIP inventory differs')
   seen.add(entry.filename)
   file_hash=item['sha256'].lower()
   destination=root/'objects/sha256'/file_hash
   if destination.is_symlink() or not destination.resolve().is_relative_to(root):raise ValueError('Unsafe file object path')
   if destination.exists():
    with destination.open('rb') as content:existing=hashlib.file_digest(content,'sha256').hexdigest()
    if existing!=file_hash or destination.stat().st_size!=item['size']:raise ValueError('Existing file object differs')
   else:
    temporary=destination.with_name(file_hash+'.stage-'+RUN)
    try:
     digest_file=hashlib.sha256();written=0
     with archive.open(entry) as content,temporary.open('xb') as output:
      while chunk:=content.read(131072):
       written+=len(chunk)
       if written>item['size']:raise ValueError('File object exceeds signed size')
       output.write(chunk);digest_file.update(chunk)
     if written!=item['size'] or digest_file.hexdigest()!=file_hash:raise ValueError('File object verification failed')
     temporary.chmod(0o644);temporary.replace(destination)
    finally:
     if temporary.exists():temporary.unlink()
   object_count+=1
 if seen!=set(expected):raise ValueError('Launcher ZIP missing signed files')
download=root/'launcher'/release['version']/'MojinDashuai-windows-x64.zip'
if download.is_symlink() or not download.resolve().is_relative_to(root):raise ValueError('Unsafe download alias')
download.parent.mkdir(parents=True,exist_ok=True)
if download.exists():
 with download.open('rb') as f:existing=hashlib.file_digest(f,'sha256').hexdigest()
 if existing!=sha:raise ValueError('Published launcher version already has different bytes')
else:os.link(target,download)
metadata=root/'launcher.signed.json'
if ACTIVATE:
 if metadata.exists():
  previous=json.loads(metadata.read_text());old=json.loads(base64.b64decode(previous['payload']))
  if old['sequence']>release['sequence'] or old['sequence']==release['sequence'] and old!=release:raise ValueError('Launcher release sequence must increase')
  backups=pathlib.Path('/vol1/mc-client-hub/backups/launcher');backups.mkdir(parents=True,exist_ok=True)
  shutil.copyfile(metadata,backups/(datetime.datetime.now(datetime.timezone.utc).strftime('%Y%m%dT%H%M%SZ')+'.signed.json'))
 staged=root/('launcher-'+RUN+'.stage');staged.write_text(json.dumps(envelope));staged.chmod(0o644);staged.replace(metadata)
source.unlink()
print(json.dumps({'uploaded':True,'activated':ACTIVATE,'sequence':release['sequence'],'fileObjects':object_count}))
'''.replace('BASE',repr(remote)).replace('HASH',repr(digest)).replace('SIZE',str(archive.stat().st_size)).replace('RUN',repr(run)).replace('ACTIVATE',repr(args.activate)),encoding='utf-8')
ssh('--send',str(script),remote+'.py')
print(ssh('--sudo','--timeout','120','python3 '+remote+'.py'))
with public_open(urllib.request.Request(expected,method='HEAD')) as response:
    if response.status!=200 or int(response.headers['Content-Length'])!=archive.stat().st_size:raise ValueError('Public launcher ZIP unavailable')
if release.get('differential',False):
    # All contents were checked against the signature on the origin. Check one
    # public object for each distinct hash as well before declaring publication.
    from concurrent.futures import ThreadPoolExecutor
    def check_object(item):
        object_url=config['frpBase'].rstrip('/')+'/objects/sha256/'+item['sha256'].lower()
        if object_url not in item['sources']:raise ValueError('Launcher file is missing the configured direct route')
        with public_open(urllib.request.Request(object_url,method='HEAD')) as response:
            if response.status!=200 or int(response.headers['Content-Length'])!=item['size']:raise ValueError('Public launcher file object unavailable')
    unique_objects={item['sha256'].lower():item for item in release['files']}
    with ThreadPoolExecutor(max_workers=4) as pool:list(pool.map(check_object,unique_objects.values()))
if args.activate:
    with public_open(config['publicBase'].rstrip('/')+'/v1/launcher') as response:
        if json.load(response)!=envelope:raise ValueError('Public launcher metadata differs')
download=config['frpBase'].rstrip('/')+'/launcher/'+urllib.parse.quote(release['version'],safe='.-')+'/MojinDashuai-windows-x64.zip'
with public_open(urllib.request.Request(download,method='HEAD')) as response:
    if response.status!=200 or int(response.headers['Content-Length'])!=archive.stat().st_size:raise ValueError('Named download unavailable')
report={'version':release['version'],'sequence':release['sequence'],'sha256':digest,'bytes':archive.stat().st_size,'downloadUrl':download,'publicDownloadVerified':True,'differential':release.get('differential',False),'fileObjects':len({f['sha256'].lower() for f in release['files']}) if release.get('differential',False) else 0,'activated':args.activate}
(bundle/'publication.json').write_text(json.dumps(report,indent=2)+'\n',encoding='utf-8')
print(json.dumps(report))
