"""Install the approved 64x64 server icon on 124 without restarting game services."""
import argparse,datetime,hashlib,json,pathlib,struct,subprocess,sys,uuid

ROOT=pathlib.Path(__file__).resolve().parents[1]
parser=argparse.ArgumentParser(description=__doc__)
parser.add_argument('--source',type=pathlib.Path,default=ROOT/'ui/public/brand/server-icon.png')
args=parser.parse_args()
source=args.source.resolve();data=source.read_bytes()
if data[:8]!=b'\x89PNG\r\n\x1a\n' or struct.unpack('>II',data[16:24])!=(64,64):raise ValueError('Server icon must be a 64x64 PNG')
sha=hashlib.sha256(data).hexdigest();run=uuid.uuid4().hex
stage=ROOT/'.local/branding';stage.mkdir(parents=True,exist_ok=True)
remote='/tmp/mojin-server-icon-'+run
script=stage/(run+'.py')
script.write_text('''import datetime,hashlib,json,os,pathlib,struct
source=pathlib.Path(SOURCE)
data=source.read_bytes()
if hashlib.sha256(data).hexdigest()!=EXPECTED or data[:8]!=b'\\x89PNG\\r\\n\\x1a\\n' or struct.unpack('>II',data[16:24])!=(64,64):raise ValueError('Uploaded icon mismatch')
base=pathlib.Path('/var/apps/docker-gsmanager/shares/gsmanager/home/steam/games').resolve()
names={'m3e':'M3E66','dc2':'DeceasedCraft-2','mb':'MeatballCraft-0.18.6.4'}
backups=pathlib.Path('/vol1/mc-client-hub/backups/server-icons')/RUN
rows=[]
for key,name in names.items():
 directory=base/name
 target=directory/'server-icon.png'
 if not directory.is_dir() or target.is_symlink() or not target.resolve().is_relative_to(base):raise ValueError('Unexpected game icon path')
for key,name in names.items():
 directory=base/name;target=directory/'server-icon.png'
 old=target.read_bytes() if target.is_file() else None
 backup=None
 if old is not None and old!=data:
  backups.mkdir(parents=True,exist_ok=True);backup=backups/(key+'.png');backup.write_bytes(old)
 if old!=data:
  temp=target.with_name('.server-icon.stage-'+RUN)
  temp.write_bytes(data);temp.chmod(0o644)
  owner=directory.stat();os.chown(temp,owner.st_uid,owner.st_gid);temp.replace(target)
 rows.append({'instance':key,'path':str(target),'sha256':hashlib.sha256(target.read_bytes()).hexdigest(),'previousIconExisted':old is not None,'backup':str(backup) if backup else None})
source.unlink()
print(json.dumps({'installed':rows,'gameServicesRestarted':False,'checkedAt':datetime.datetime.now(datetime.timezone.utc).isoformat()}))
'''.replace('SOURCE',repr(remote+'.png')).replace('EXPECTED',repr(sha)).replace('RUN',repr(run)),encoding='utf-8')
def ssh(*parameters):
    result=subprocess.run([sys.executable,str(ROOT.parent/'tools/ssh124.py'),'--user','Agent2',*parameters],capture_output=True,timeout=120)
    if result.returncode:raise RuntimeError('Server icon deployment failed; no game restart was requested')
    return result.stdout.decode('utf-8',errors='replace').strip()
ssh('--send',str(source),remote+'.png');ssh('--send',str(script),remote+'.py')
report=json.loads(ssh('--sudo','--timeout','60','python3 '+remote+'.py'))
report.update(source='ui/public/brand/server-icon.png',sha256=sha,gameMenusChanged=False,liveStatusIconVerified=False)
(ROOT/'packs/server-icons.json').write_text(json.dumps(report,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
print(json.dumps(report,ensure_ascii=False))
