"""Run on NAS with candidate image. Uses a disposable DB; no production players or rewards."""
import concurrent.futures, datetime as dt, hashlib, json, os, pathlib, re, secrets, subprocess, time, urllib.request, urllib.error, uuid

def run(*args,input=None):return subprocess.run(args,input=input,check=True,capture_output=True,text=True).stdout
stamp=secrets.token_hex(4);database='hub_activity_'+stamp;container='boshan-activity-test-'+stamp
assert re.fullmatch(r'hub_activity_[a-f0-9]{8}',database)
image=os.environ.get('HUB_API_TEST_IMAGE','boshan/hub-api:activities-candidate')
keys={w:secrets.token_hex(32) for w in ['m3e','dc2','mb','vw']}
env=pathlib.Path('/var/apps/mc-client-hub/secrets')/('activity-test-'+stamp+'.env')
password=pathlib.Path('/var/apps/mc-client-hub/secrets/db-password').read_text().strip()
env.write_text('ConnectionStrings__Hub=Host=mc-client-hub-postgres-1;Database='+database+';Username=hub;Password='+password+'\nInitializeDatabase=true\nASPNETCORE_URLS=http://+:8080\nActivities__Enabled=true\nJoinAuth__InternalNetworks=0.0.0.0/0\n'+''.join('Activities__ServerKeys__'+w+'='+k+'\n' for w,k in keys.items()));env.chmod(0o600)
checks=[]
def check(ok,name):
 if not ok:raise AssertionError(name)
 checks.append(name)
def api(path,data=None,token=None):
 h={'Content-Type':'application/json'}
 if token:h['Authorization']='Bearer '+token
 req=urllib.request.Request('http://127.0.0.1:18086'+path,None if data is None else json.dumps(data).encode(),h)
 try:
  with urllib.request.urlopen(req,timeout=12) as r:return r.status,json.loads(r.read())
 except urllib.error.HTTPError as e:return e.code,json.loads(e.read())
def sql(q):return run('docker','exec','-i','mc-client-hub-postgres-1','psql','-U','hub','-d',database,'-At','-v','ON_ERROR_STOP=1',input=q)
def admin(*args):return run('docker','exec',container,'dotnet','Hub.Api.dll','admin',*args)
def command(world,action='view',**extra):return api('/v1/activities',dict(instance=world,action=action,operationId=str(uuid.uuid4()),**extra),token)
def event(world,kind,key,count=1,**extra):
 e=dict(eventId=str(uuid.uuid4()),gameUuid=game_uuid,occurredAt=dt.datetime.now(dt.timezone.utc).isoformat(),kind=kind,key=key,count=count);e.update(extra)
 return e,api('/internal/v1/activities/'+world+'/events',e,keys[world])
try:
 run('docker','exec','mc-client-hub-postgres-1','createdb','-U','hub',database)
 run('docker','create','--name',container,'--network','mc-client-hub_database','--env-file',str(env),'-p','127.0.0.1:18086:8080',image)
 run('docker','network','connect','mc-client-hub_edge',container);run('docker','start',container)
 for _ in range(60):
  try:
   if api('/health')[0]==200:break
  except Exception:pass
  time.sleep(.3)
 check(api('/health')[0]==200,'isolated API healthy')
 invitation=re.search(r'Code \(shown once\): ([a-f0-9]+)',admin('invite-create','super'))[1]
 code,account=api('/v1/auth/register',dict(loginName='activitytest',gameName='ActivityTest',password='acceptance-'+stamp,invitation=invitation))
 check(code==200,'existing account flow registers canonical activity identity');token=account['accessToken']
 row=json.loads(sql('SELECT row_to_json(i) FROM "JoinIdentities" i WHERE "GameName"=\'ActivityTest\';').strip());identity=row['Id'];game_uuid=row['GameUuid']
 check(api('/v1/activities',{'instance':'m3e'})[0]==401,'anonymous claims blocked')
 check(api('/internal/v1/activities/m3e/definition',token=keys['dc2'])[0]==403,'server key cannot observe another world')
 definitions={w:api('/internal/v1/activities/'+w+'/definition',token=keys[w])[1] for w in keys}
 for w in keys:
  e,result=event(w,'snapshot','',facts=['quest:'+definitions[w]['questIds'][0]])
  check(result[0]==200 and not command(w)[1]['dailyReady'],w+' old history unlocks without daily credit')
  e,result=event(w,'craft','minecraft:torch@0',4)
  check(result[0]==200,w+' real craft accepted')
  check(api('/internal/v1/activities/'+w+'/events',e,keys[w])[0]==200,w+' event replay idempotent')
  check(command(w)[1]['dailyReady'],w+' daily ready')
 with concurrent.futures.ThreadPoolExecutor(4) as pool:outcomes=list(pool.map(lambda w:command(w,'daily')[0],keys))
 check(outcomes.count(200)==1 and outcomes.count(409)==3,'parallel four-world daily claims give exactly one reward')
 winner=next(w for w in keys if command(w)[1]['tickets']==1)
 payload=dict(instance=winner,action='draw',operationId=str(uuid.uuid4()))
 with concurrent.futures.ThreadPoolExecutor(2) as pool:retries=list(pool.map(lambda _:api('/v1/activities',payload,token),range(2)))
 check(all(r[0]==200 for r in retries) and command(winner)[1]['tickets']==0,'simultaneous draw retry consumes one ticket')
 check(retries[0][1]['resultAwardId']==retries[1][1]['resultAwardId'] and retries[0][1]['resultAwardId'],'retried draw returns the same exact reward identity')
 check(api('/v1/activities',{**payload,'action':'buy','cosmetic':'title'},token)[0]==409,'operation ID cannot be reused for a different action')
 old=(dt.datetime.now(dt.timezone.utc)-dt.timedelta(days=40)).isoformat()
 event('m3e','craft','minecraft:torch@0',4,occurredAt=old)
 check(command('m3e','daily',period=(dt.datetime.fromisoformat(old)+dt.timedelta(hours=8)).strftime('%Y-%m-%d'))[0]==200,'unclaimed completed day survives 40 days')
 # Seed only this disposable account's wallet and miss count to exercise the exact boundary.
 raw=sql('SELECT "StateJson" FROM "ActivityAccounts" WHERE "IdentityId"=\''+identity+'\';').strip();state=json.loads(raw)
 state['worlds']['m3e']['tickets']=2;state['worlds']['m3e']['misses']=49
 sql('UPDATE "ActivityAccounts" SET "StateJson"=$state$'+json.dumps(state)+'$state$ WHERE "IdentityId"=\''+identity+'\';')
 c,v=command('m3e','draw');a=next(a for a in v['awards'] if a['source']=='抽奖')
 check(c==200 and a['tier']=='rare' and not a['choices'] and v['misses']==0,'50th guarantee retains rare when production prerequisites missing')
 reward=next(r for r in definitions['m3e']['rewards'] if r['tier']=='rare')
 event('m3e','snapshot','',facts=reward['requires']['all'])
 c,v=command('m3e','select',awardId=a['id'],rewardId=reward['id']);check(c==200,'rare selection requires reviewed unlocked goal and own production')
 c,deliveries=api('/internal/v1/activities/m3e/deliveries/'+game_uuid,token=keys['m3e']);check(c==200 and deliveries,'durable mailbox receives selected rewards')
 pending_count=len(deliveries);check(len(api('/internal/v1/activities/m3e/deliveries/'+game_uuid,token=keys['m3e'])[1])==pending_count,'polling without ack retains mailbox')
 target=deliveries[-1]['id'];ack='/internal/v1/activities/m3e/deliveries/'+game_uuid+'/'+target+'/ack'
 check(api(ack,{},keys['dc2'])[0]==403,'another server cannot ack mailbox')
 check(api(ack,{},keys['m3e'])[0]==200 and api(ack,{},keys['m3e'])[0]==200,'delivery ack safely retries')
 before=command('m3e')[1];run('docker','restart',container)
 for _ in range(60):
  try:
   if api('/health')[0]==200:break
  except Exception:pass
  time.sleep(.3)
 after=command('m3e')[1]
 check(after['tickets']==before['tickets'] and after['misses']==before['misses'] and after['awards']==before['awards'],'API restart retains wallet pity pending and delivered awards')
 check(command('m3e','showcase',text='这是测试隔离库内的产线说明，介绍输入输出与实际用途。')[0]==200,'showcase submission accepts plain text')
 pending=json.loads(admin('activities-review'));check(len(pending)==1,'moderation lists pending only')
 check(not command('m3e')[1]['showcases'],'unreviewed showcase not published')
 admin('activities-review',pending[0]['id'],'approve');check(len(command('m3e')[1]['showcases'])==1,'approved showcase visible')
 check(event('m3e','pickup','minecraft:torch@0',4096)[1][0]==400,'pickup spam cannot count as production')
 print(json.dumps({'passed':len(checks),'checks':checks},ensure_ascii=False))
except Exception:
 result=subprocess.run(['docker','logs','--tail','45',container],capture_output=True,text=True)
 log=result.stdout+result.stderr
 for secret in [password,*keys.values()]:log=log.replace(secret,'[redacted]')
 print(log[-7000:]);raise
finally:
 subprocess.run(['docker','rm','-f',container],capture_output=True)
 subprocess.run(['docker','exec','mc-client-hub-postgres-1','dropdb','-U','hub','--force',database],capture_output=True)
 env.unlink(missing_ok=True)
