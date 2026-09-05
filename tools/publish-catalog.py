"""Sign and publish accepted pack manifests, then atomically activate the catalog."""
import argparse,base64,datetime,hashlib,json,pathlib,subprocess,sys,tarfile,urllib.request,uuid

ROOT=pathlib.Path(__file__).resolve().parents[1]
parser=argparse.ArgumentParser(description=__doc__)
parser.add_argument('--sequence',type=int,required=True)
parser.add_argument('--beta',action='store_true')
parser.add_argument('--dotnet',default='dotnet')
parser.add_argument('--publisher',type=pathlib.Path,default=ROOT/'src/Publisher/bin/Release/net10.0/Publisher.dll')
parser.add_argument('--key',type=pathlib.Path,required=True)
args=parser.parse_args()
if args.sequence<=0:parser.error('sequence must be positive')
subprocess.run([sys.executable,str(ROOT/'tools/check-client-release.py'),*(['--beta'] if args.beta else [])],check=True,cwd=ROOT)
config=json.loads((ROOT/'packs/distributions.json').read_text(encoding='utf-8'))
stage=ROOT/'artifacts'/('catalog-'+('beta-' if args.beta else 'stable-')+str(args.sequence));stage.mkdir(parents=True,exist_ok=True)
def publish(*arguments):subprocess.run([args.dotnet,str(args.publisher),*map(str,arguments)],check=True,cwd=ROOT)
servers=[];files={}
for instance,spec in config['instances'].items():
    source=ROOT/f'artifacts/native/{instance}-manifest.json'
    manifest=json.loads(source.read_text(encoding='utf-8-sig'))
    relative=f'manifests/{instance}/{manifest["sequence"]}.signed.json'
    signed=stage/(instance+'.signed.json')
    if not signed.exists():publish('sign-beta' if args.beta else 'sign',source,args.key,signed)
    envelope=json.loads(signed.read_text(encoding='utf-8-sig'))
    if json.loads(base64.b64decode(envelope['payload']))!=manifest:raise ValueError('Staged signed manifest differs from accepted content')
    files[relative]=signed
    servers.append({'id':instance,'name':spec['name'],'routes':[r['host'] for r in spec['routes']],
        'release':{'version':manifest['version'],'sequence':manifest['sequence'],'manifestUrl':config['publicBase']+'/v1/'+relative.removesuffix('.signed.json'),
        'sha256':hashlib.sha256(signed.read_bytes()).hexdigest(),'compatibility':manifest['compatibility']},'rollbacks':[]})
catalog=stage/'catalog.json';signed_catalog=stage/'catalog.signed.json'
if not catalog.exists():
    catalog.write_text(json.dumps({'sequence':args.sequence,'minimumLauncher':'0.1.2','expiresAt':(datetime.datetime.now(datetime.timezone.utc)+datetime.timedelta(days=180)).isoformat(),'servers':servers},ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
elif json.loads(catalog.read_text(encoding='utf-8'))['servers']!=servers:raise ValueError('Existing catalog candidate differs')
if not signed_catalog.exists():publish('sign-catalog',catalog,args.key,signed_catalog)
files['catalog.signed.json']=signed_catalog
run=uuid.uuid4().hex;archive=stage/(run+'.tar');inventory=stage/(run+'.json')
with tarfile.open(archive,'w') as tar:
    for relative,path in files.items():tar.add(path,arcname=relative,recursive=False)
inventory.write_text(json.dumps({k:{'size':p.stat().st_size,'sha256':hashlib.sha256(p.read_bytes()).hexdigest()} for k,p in files.items()}),encoding='utf-8')
remote='/tmp/mojin-catalog-'+run
script=stage/(run+'.py')
script.write_text('''import base64,datetime,hashlib,json,pathlib,shutil,tarfile
root=pathlib.Path('/vol1/mc-client-hub/public').resolve()
expected=json.loads(pathlib.Path(BASE+'.json').read_text())
staged={}
with tarfile.open(BASE+'.tar') as archive:
 for entry in archive:
  if not entry.isfile() or entry.name not in expected or entry.size!=expected[entry.name]['size']:raise ValueError('Unexpected metadata entry')
  target=root/entry.name
  if target.is_symlink() or not target.resolve().is_relative_to(root):raise ValueError('Unsafe publication path')
  data=archive.extractfile(entry).read()
  if hashlib.sha256(data).hexdigest()!=expected[entry.name]['sha256']:raise ValueError('Metadata hash mismatch')
  staged[entry.name]=data
if set(staged)!=set(expected):raise ValueError('Missing metadata')
pointer=root/'catalog.signed.json'
new=json.loads(base64.b64decode(json.loads(staged['catalog.signed.json'])['payload']))
if pointer.exists():
 old=json.loads(base64.b64decode(json.loads(pointer.read_bytes())['payload']))
 if old['sequence']>new['sequence'] or old['sequence']==new['sequence'] and pointer.read_bytes()!=staged['catalog.signed.json']:raise ValueError('Catalog sequence must increase')
for name,data in staged.items():
 if name=='catalog.signed.json':continue
 target=root/name
 if target.exists() and target.read_bytes()!=data:raise ValueError('Existing immutable manifest differs')
for name,data in staged.items():
 if name=='catalog.signed.json':continue
 target=root/name;target.parent.mkdir(parents=True,exist_ok=True)
 if not target.exists():
  temp=target.with_name(target.name+'.stage-'+RUN);temp.write_bytes(data);temp.chmod(0o644);temp.replace(target)
if pointer.exists():
 backups=pathlib.Path('/vol1/mc-client-hub/backups/catalog');backups.mkdir(parents=True,exist_ok=True)
 shutil.copyfile(pointer,backups/(datetime.datetime.now(datetime.timezone.utc).strftime('%Y%m%dT%H%M%SZ')+'.signed.json'))
temp=pointer.with_name('catalog.stage-'+RUN);temp.write_bytes(staged['catalog.signed.json']);temp.chmod(0o644);temp.replace(pointer)
pathlib.Path(BASE+'.tar').unlink()
print(json.dumps({'catalogActivated':True,'sequence':new['sequence'],'manifests':len(staged)-1}))
'''.replace('BASE',repr(remote)).replace('RUN',repr(run)),encoding='utf-8')
def ssh(*parameters):
    r=subprocess.run([sys.executable,str(ROOT.parent/'tools/ssh124.py'),'--user','Agent2',*parameters],capture_output=True,timeout=600)
    if r.returncode:raise RuntimeError('Catalog publication failed; staged artifacts are preserved')
    return r.stdout.decode('utf-8',errors='replace').strip()
for path,suffix in [(archive,'.tar'),(inventory,'.json'),(script,'.py')]:ssh('--send',str(path),remote+suffix)
print(ssh('--sudo','--timeout','120','python3 '+remote+'.py'))
for server in servers:
    with urllib.request.urlopen(server['release']['manifestUrl'],timeout=90) as response:data=response.read()
    if hashlib.sha256(data).hexdigest()!=server['release']['sha256']:raise ValueError('Public manifest differs')
with urllib.request.urlopen(config['publicBase']+'/v1/catalog',timeout=30) as response:
    if response.read()!=signed_catalog.read_bytes():raise ValueError('Public catalog differs')
report={'channel':'beta' if args.beta else 'stable','catalogSequence':args.sequence,'catalogActivated':True,'publicManifestsVerified':len(servers),'cleanWindows':False if args.beta else True}
(stage/'publication.json').write_text(json.dumps(report,indent=2)+'\n',encoding='utf-8')
print(json.dumps(report))
