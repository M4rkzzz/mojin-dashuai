"""NAS activities rollout. Never restart a game with players online. No credentials on stdout."""
import argparse,datetime as dt,importlib.util,json,os,pathlib,re,secrets,shutil,subprocess,time,urllib.request
ROOT=pathlib.Path('/var/apps/mc-client-hub');STAGE=ROOT/'staging/activities-1.0.3'
HOST=pathlib.Path('/var/apps/docker-gsmanager/shares/gsmanager/home/steam/games');INNER=pathlib.Path('/home/steam/games')
WORLDS={'m3e':('M3E66','run.sh','26e0ee30-5b71-49a5-8932-41f86b407373',25575),'dc2':('DeceasedCraft-2','run.sh','3cd98756-9d98-4448-b97c-cca1785af034',25576),'mb':('MeatballCraft-0.18.6.4','ServerStart.sh','f60bfc16-9907-425a-81e3-643ce636e47f',25577),'vw':('VoidWayfarer-4','start.sh','23295b5f-b303-4fb6-939f-cc3d03f9f1dc',25578)}
def run(*args,input=None,timeout=30):return subprocess.run(args,input=input,check=True,capture_output=True,text=True,timeout=timeout).stdout
def atomic(p,text,uid=0,gid=0,mode=0o600):
 p.parent.mkdir(parents=True,exist_ok=True);t=p.with_name(p.name+'.new');t.write_text(text);os.chown(t,uid,gid);os.chmod(t,mode);os.replace(t,p)
def java_processes(w):
 result=[]
 for p in pathlib.Path('/proc').glob('[0-9]*'):
  try:
   if (p/'comm').read_text().strip()=='java' and os.readlink(p/'cwd')==str(INNER/WORLDS[w][0]):result.append(p.name)
  except OSError:pass
 return result
def gsm(w,action):return run('docker','exec','-w','/root/server','gsmanager','node','gsmanager-api.mjs',action,WORLDS[w][2])
def online(w,stop=False):
 code=r'''import asyncio,bridge,re,json,sys
async def main():
 c=next(c for c in bridge.load_server_configs() if c.rcon_port==int(sys.argv[1]))
 client=bridge.RconClient(c.rcon_host,c.rcon_port,bridge.load_rcon_password(c.password_file,c.rcon_password))
 async def count():
  text=re.sub(r'\u00a7.','',str(await asyncio.wait_for(client.command('list'),12)))
  m=re.search(r'There (?:are|is) (\d+)(?: of a max of| out of|/| players? online)',text)
  if not m:raise RuntimeError('Cannot determine player count; restart deferred')
  return int(m.group(1))
 n=await count()
 if sys.argv[2]=='stop' and n==0:
  await asyncio.wait_for(client.command('save-all'),45)
  n=await count()
  if n==0:
   try:await asyncio.wait_for(client.command('stop'),15)
   except Exception:pass
 print(json.dumps({'online':n,'stopRequested':sys.argv[2]=='stop' and n==0}))
asyncio.run(main())
'''
 return json.loads(run('docker','exec','-i','mcqq-bridge','python','-',str(WORLDS[w][3]),'stop' if stop else 'list',input=code,timeout=85))
def prepare(agent):
 STAGE.mkdir(parents=True,exist_ok=True);os.chmod(STAGE,0o700)
 if not pathlib.Path(agent).is_file():raise ValueError('Agent missing')
 shutil.copy2(agent,STAGE/'mojin-activities-server-agent.jar')
 env=ROOT/'secrets/api.env';text=env.read_text();original=text
 for w,(directory,script,_,_) in WORLDS.items():
  root=HOST/directory;target=STAGE/w;target.mkdir(exist_ok=True);os.chmod(target,0o700)
  p=target/'secret';key=p.read_text().strip() if p.exists() else secrets.token_hex(32)
  if not p.exists():atomic(p,key)
  name='Activities__ServerKeys__'+w
  found=re.search(r'^'+name+'=(.*)$',text,re.M)
  if found and found[1]!=key:raise RuntimeError('Activity key already exists with different staging state')
  if not found:text=text.rstrip()+'\n'+name+'='+key+'\n'
  props=dict(l.split('=',1) for l in (root/'server.properties').read_text().splitlines() if '=' in l and not l.startswith('#'));level=props.get('level-name','world')
  data=(root/level/'playerdata').resolve()
  if not data.is_relative_to(root.resolve()) or not data.is_dir():raise RuntimeError('Player directory not confirmed: '+w)
  private=INNER/directory/'.activities';config='instance='+w+'\nsecret='+key+'\nbaseUrl=http://hub-api:8080/internal/v1/activities/'+w+'\nallowLocalContainerHttp=true\nspoolDirectory='+str(private/'spool')+'\nplayerDataDirectory='+str(INNER/directory/level/'playerdata')+'\n'
  atomic(target/'server.properties',config)
 if 'Activities__Enabled=' not in text:text+='Activities__Enabled=false\n'
 if text!=original:
  if not (STAGE/'api.env.before').exists():shutil.copy2(env,STAGE/'api.env.before')
  atomic(env,text)
 print(json.dumps({'prepared':list(WORLDS),'publicEnabled':False}))
def activate(w):
 root=HOST/WORLDS[w][0];script=root/WORLDS[w][1];target=STAGE/w;private=root/'.activities'
 if not (target/'server.properties').is_file():raise RuntimeError('Run prepare first')
 if (target/'active.json').exists():print(json.dumps({'world':w,'alreadyActive':True}));return
 current=script.read_text();marker='-Dmojin.activities.config='
 if marker in current:raise RuntimeError('Activity script changed without deployment record; inspect first')
 candidates=[l for l in current.splitlines() if '-Dmojin.join.server.config=' in l and not re.search(r'\b(echo|write_to_log)\b',l)]
 if len(candidates)!=1:raise RuntimeError('Exact Java invocation not found')
 line=candidates[0];needle=re.search(r'-javaagent:\S+\.jar',line)
 if not needle:raise RuntimeError('Existing join agent invocation missing')
 prefix='-javaagent:'+str(INNER/WORLDS[w][0]/'.activities/mojin-activities-server-agent.jar')+' -Dmojin.activities.config='+str(INNER/WORLDS[w][0]/'.activities/server.properties')+' '
 modified=current.replace(line,line[:needle.start()]+prefix+line[needle.start():],1)
 initial=online(w)
 if initial['online']!=0:print(json.dumps({'world':w,'deferred':True,**initial}));return
 # The second check happens after save-all and immediately before the graceful stop.
 result=online(w,stop=True)
 if not result['stopRequested']:print(json.dumps({'world':w,'deferred':True,**result}));return
 deadline=time.monotonic()+300
 while java_processes(w) and time.monotonic()<deadline:time.sleep(1)
 if java_processes(w):raise RuntimeError('Game did not exit normally; no forced kill')
 if script.read_text()!=current:raise RuntimeError('Start script changed during stop; not overwriting concurrent work')
 try:gsm(w,'close-terminal')
 except subprocess.CalledProcessError:pass
 uid,gid=root.stat().st_uid,root.stat().st_gid
 private.mkdir(exist_ok=True);os.chown(private,uid,gid);os.chmod(private,0o700)
 shutil.copy2(script,target/'start-script.before');shutil.copy2(STAGE/'mojin-activities-server-agent.jar',private/'mojin-activities-server-agent.jar');os.chown(private/'mojin-activities-server-agent.jar',uid,gid)
 atomic(private/'server.properties',(target/'server.properties').read_text(),uid,gid)
 atomic(script,modified,script.stat().st_uid,script.stat().st_gid,script.stat().st_mode&0o777)
 gsm(w,'start');deadline=time.monotonic()+360;ready=False
 while time.monotonic()<deadline:
  try:
   log=(root/'logs/latest.log').read_text(errors='replace')
   if 'Done (' in log and java_processes(w) and online(w)['online']>=0:ready=True;break
  except Exception:pass
  time.sleep(3)
 if not ready:raise RuntimeError('Server readiness check failed; inspect before opening activities')
 # Verify the actual Java has the new observer argument; do not rely on an old log.
 if not any(b'mojin.activities.config=' in pathlib.Path('/proc',p,'cmdline').read_bytes() for p in java_processes(w)):raise RuntimeError('Server restarted without activity observer')
 state={'world':w,'activatedAt':dt.datetime.now(dt.timezone.utc).isoformat(),'ready':True,'online':online(w)['online']};atomic(target/'active.json',json.dumps(state));print(json.dumps(state))
def disable(w):
 root=HOST/WORLDS[w][0];script=root/WORLDS[w][1];target=STAGE/w
 current=script.read_text();prefix='-javaagent:'+str(INNER/WORLDS[w][0]/'.activities/mojin-activities-server-agent.jar')+' -Dmojin.activities.config='+str(INNER/WORLDS[w][0]/'.activities/server.properties')+' '
 if current.count(prefix)!=1:raise RuntimeError('Exact activity invocation not found; inspect before rollback')
 if online(w)['online']!=0:print(json.dumps({'world':w,'deferred':True}));return
 if not online(w,stop=True)['stopRequested']:print(json.dumps({'world':w,'deferred':True}));return
 deadline=time.monotonic()+300
 while java_processes(w) and time.monotonic()<deadline:time.sleep(1)
 if java_processes(w):raise RuntimeError('Game did not exit normally; no forced kill')
 if script.read_text()!=current:raise RuntimeError('Start script changed during stop')
 try:gsm(w,'close-terminal')
 except subprocess.CalledProcessError:pass
 atomic(script,current.replace(prefix,'',1),script.stat().st_uid,script.stat().st_gid,script.stat().st_mode&0o777)
 if (target/'active.json').exists():(target/'active.json').rename(target/('disabled-'+dt.datetime.now(dt.timezone.utc).strftime('%Y%m%dT%H%M%SZ')+'.json'))
 gsm(w,'start');print(json.dumps({'world':w,'observerDisabled':True,'dataPreserved':True}))

def enable():
 for w in WORLDS:
  if not (STAGE/w/'active.json').exists():raise RuntimeError('Server observer not yet deployed: '+w)
  if online(w)['online']<0:raise RuntimeError('Server not responding: '+w)
  if not any(b'mojin.activities.config=' in pathlib.Path('/proc',p,'cmdline').read_bytes() for p in java_processes(w)):raise RuntimeError('Observer not active: '+w)
 env=ROOT/'secrets/api.env';before=env.read_text()
 after,n=re.subn(r'^Activities__Enabled=.*$', 'Activities__Enabled=true',before,flags=re.M)
 if n!=1:raise RuntimeError('Activity switch configuration missing or duplicated')
 if after==before:print(json.dumps({'enabled':True,'alreadyEnabled':True}));return
 atomic(env,after)
 try:
  run('docker','compose','--project-directory',str(ROOT),'-f',str(ROOT/'compose.yml'),'up','-d','--no-deps','--no-build','hub-api',timeout=60)
  healthy=False
  for _ in range(40):
   try:
    with urllib.request.urlopen('http://127.0.0.1:18081/health',timeout=2) as response:healthy=response.status==200
    if healthy:break
   except Exception:pass
   time.sleep(.5)
  if not healthy:raise RuntimeError('API health failed')
 except Exception:
  atomic(env,before);run('docker','compose','--project-directory',str(ROOT),'-f',str(ROOT/'compose.yml'),'up','-d','--no-deps','--no-build','hub-api',timeout=60);raise
 print(json.dumps({'enabled':True,'allFourObserversVerified':True}))

def status():
 values=[]
 for w in WORLDS:
  state=json.loads((STAGE/w/'active.json').read_text()) if (STAGE/w/'active.json').exists() else {'world':w,'ready':False}
  try:state.update(online(w))
  except Exception:state['online']=None
  values.append(state)
 print(json.dumps(values))

def upgrade(w,agent,version):
 if not re.fullmatch(r'\d+\.\d+\.\d+',version):raise ValueError('Invalid observer version')
 root=HOST/WORLDS[w][0];script=root/WORLDS[w][1];private=root/'.activities'
 target=ROOT/'staging'/('activities-'+version)/w;target.mkdir(parents=True,exist_ok=True);os.chmod(target,0o700)
 jar=private/'mojin-activities-server-agent.jar';source=pathlib.Path(agent)
 if not source.is_file() or not jar.is_file() or not (private/'server.properties').is_file():raise RuntimeError('Existing observer or candidate missing')
 if (target/'active.json').exists() and java_processes(w):
  try:
   runtime=json.loads((private/'spool/status.json').read_text())
   if runtime['version']==version and (dt.datetime.now(dt.timezone.utc)-dt.datetime.fromisoformat(runtime['rulesLoadedAt'].replace('Z','+00:00'))).total_seconds()<150:
    print(json.dumps({'world':w,'alreadyActive':True,'version':version,**online(w)}),flush=True);return
  except (OSError,ValueError,KeyError):pass
 if '-Dmojin.activities.config=' not in script.read_text():raise RuntimeError('Observer not enabled in existing startup script')
 # Confirm the process controller works before stopping any game.
 run('docker','exec','gsmanager','test','-f','/root/server/gsmanager-api.mjs')
 current=online(w)
 if current['online']!=0:print(json.dumps({'world':w,'deferred':True,**current}),flush=True);return
 result=online(w,stop=True)
 if not result['stopRequested']:print(json.dumps({'world':w,'deferred':True,**result}),flush=True);return
 print(json.dumps({'world':w,'stoppingEmptyServer':True}),flush=True)
 deadline=time.monotonic()+300
 while java_processes(w) and time.monotonic()<deadline:time.sleep(1)
 if java_processes(w):raise RuntimeError('Game did not exit normally; no forced kill')
 try:gsm(w,'close-terminal')
 except subprocess.CalledProcessError:pass
 backup=target/'observer.before.jar'
 if not backup.exists():shutil.copy2(jar,backup)
 temporary=jar.with_suffix('.new');shutil.copy2(source,temporary);os.chown(temporary,jar.stat().st_uid,jar.stat().st_gid);os.replace(temporary,jar)
 started=dt.datetime.now(dt.timezone.utc);gsm(w,'start');deadline=time.monotonic()+420;ready=False
 while time.monotonic()<deadline:
  try:
   state=json.loads((private/'spool/status.json').read_text())
   fresh=dt.datetime.fromisoformat(state['rulesLoadedAt'].replace('Z','+00:00'))>=started
   if state['version']==version and fresh and 'Done (' in (root/'logs/latest.log').read_text(errors='replace') and java_processes(w) and online(w)['online']>=0:ready=True;break
  except Exception:pass
  time.sleep(3)
 if not ready:raise RuntimeError('Observer readiness failed; previous JAR saved at '+str(backup))
 state.update(world=w,ready=True,online=online(w)['online'],activatedAt=started.isoformat())
 atomic(target/'active.json',json.dumps(state));print(json.dumps(state),flush=True)

parser=argparse.ArgumentParser();parser.add_argument('action',choices=['prepare','activate','upgrade','disable','status','enable']);parser.add_argument('value',nargs='?');parser.add_argument('--agent');parser.add_argument('--version',default='1.0.4');args=parser.parse_args()
if args.action=='prepare':prepare(args.value)
elif args.action=='activate':activate(args.value)
elif args.action=='upgrade':upgrade(args.value,args.agent,args.version)
elif args.action=='disable':disable(args.value)
elif args.action=='enable':enable()
else:status()
