"""Run on 124 against a disposable database and isolated API container. Never print credentials."""
import concurrent.futures, json, os, pathlib, re, secrets, subprocess, time, urllib.request, urllib.error

def run(*args):
    result=subprocess.run(args,check=True,capture_output=True,text=True)
    return result.stdout

stamp=secrets.token_hex(4)
database='hub_acceptance_'+stamp
container='boshan-acceptance-'+stamp
assert re.fullmatch(r'hub_acceptance_[a-f0-9]{8}',database)
password=pathlib.Path('/var/apps/mc-client-hub/secrets/db-password').read_text().strip()
env_file=pathlib.Path('/var/apps/mc-client-hub/secrets/acceptance.env')
env_file.write_text('ConnectionStrings__Hub=Host=mc-client-hub-postgres-1;Database='+database+';Username=hub;Password='+password+'\nInitializeDatabase=true\nASPNETCORE_URLS=http://+:8080\nTrustCloudflareTunnel=true\n')
env_file.chmod(0o600)
run('docker','exec','mc-client-hub-postgres-1','createdb','-U','hub',database)
checks=[]
def check(condition,name):
    if not condition: raise AssertionError(name)
    checks.append(name)
def api(path,data=None,token=None,ip='198.51.100.12'):
    headers={'Content-Type':'application/json','CF-Connecting-IP':ip}
    if token: headers['Authorization']='Bearer '+token
    request=urllib.request.Request('http://127.0.0.1:18082'+path,data=None if data is None else json.dumps(data).encode(),headers=headers)
    try:
        with urllib.request.urlopen(request,timeout=20) as response:
            raw=response.read();return response.status,json.loads(raw) if raw else None
    except urllib.error.HTTPError as error:
        raw=error.read();return error.code,json.loads(raw) if raw else None
def admin(*args): return run('docker','exec',container,'dotnet','Hub.Api.dll','admin',*args)
def invite(kind,*args):
    output=admin('invite-create',kind,*args)
    return re.search(r'Invitation ID: ([a-f0-9-]+)',output)[1],re.search(r'Code \(shown once\): ([a-f0-9]+)',output)[1]
def register(login,game,code,ip='198.51.100.12'):
    return api('/v1/auth/register',{'loginName':login,'gameName':game,'password':'acceptance-pass-'+stamp,'invitation':code},ip=ip)
try:
    run('docker','create','--name',container,'--network','mc-client-hub_database','--env-file',str(env_file),'-p','127.0.0.1:18082:8080','boshan/hub-api:0.1.0')
    run('docker','network','connect','mc-client-hub_edge',container)
    run('docker','start',container)
    for _ in range(50):
        try:
            if api('/health')[0]==200: break
        except Exception: pass
        time.sleep(.5)
    check(api('/health')[0]==200,'isolated API healthy')
    super_id,super_code=invite('super')
    code,alice=register('alice','Alice',super_code);check(code==200,'super invite first registration')
    code,bob=register('bobby','Bobby',super_code);check(code==200,'super invite reusable registration')
    check(alice['profile']['gameName']=='Alice','game name retains exact case')
    check('password' not in alice and 'invitation' not in alice,'secrets absent from profile response')
    admin('protect','OldPlayer')
    check(register('hijack','OldPlayer',super_code)[0]==400,'super invite cannot claim protected name')
    _,bound=invite('single','OldPlayer')
    check(register('oldplayer','oldplayer',bound)[0]==400,'bound invite enforces exact case')
    check(register('oldplayer','OldPlayer',bound)[0]==200,'bound invite claims protected original name')
    _,single=invite('single')
    with concurrent.futures.ThreadPoolExecutor(2) as pool:
        outcomes=list(pool.map(lambda i:register('race'+str(i),'Race'+str(i),single,ip='198.51.100.'+str(20+i))[0],[1,2]))
    check(outcomes.count(200)==1,'single invite consumed once under concurrent registration')
    admin('invite-revoke',super_id)
    check(register('lateuser','LateUser',super_code)[0]==400,'revoked super invite rejects new registration')
    check(api('/v1/account/me',token=alice['accessToken'])[0]==200,'revocation preserves existing account')
    code,rotated=api('/v1/auth/refresh',{'refreshToken':alice['refreshToken']},ip='198.51.100.30');check(code==200,'refresh rotates credential')
    check(api('/v1/auth/refresh',{'refreshToken':alice['refreshToken']},ip='198.51.100.30')[0]==401,'old refresh rejects replay')
    check(api('/v1/account/me',token=rotated['accessToken'])[0]==401,'refresh replay revokes token family')
    recover={'loginName':'bobby','code':bob['recoveryCode'],'newPassword':'new-acceptance-password'}
    code,recovered=api('/v1/auth/recover',recover,ip='198.51.100.40');check(code==200 and recovered['recoveryCode']!=bob['recoveryCode'],'recovery rotates one-time code')
    check(api('/v1/auth/recover',recover,ip='198.51.100.40')[0]==400,'recovery code cannot be reused')
    check(api('/v1/account/me',token=bob['accessToken'])[0]==401,'password reset revokes sessions')
    check(api('/v1/auth/login',{'loginName':'bobby','password':'new-acceptance-password'},ip='198.51.100.40')[0]==200,'new password works')
    admin('disable','bobby')
    check(api('/v1/auth/login',{'loginName':'bobby','password':'new-acceptance-password'},ip='198.51.100.40')[0]==401,'disabled account cannot log in')
    rate=[api('/v1/auth/login',{'loginName':'nobody','password':'not-a-password'},ip='198.51.100.50')[0] for _ in range(14)]
    check(429 in rate,'authentication rate limit enforced')
    dump=run('docker','exec','mc-client-hub-postgres-1','pg_dump','-U','hub','--data-only',database)
    check(super_code not in dump and bob['recoveryCode'] not in dump and bob['refreshToken'] not in dump,'database stores hashes instead of credential plaintext')
    logs=run('docker','logs',container)
    check(super_code not in logs and bob['accessToken'] not in logs and bob['recoveryCode'] not in logs,'logs contain no credentials')
    result={'passed':len(checks),'checks':checks,'at':time.strftime('%Y-%m-%dT%H:%M:%SZ',time.gmtime())}
    print(json.dumps(result,ensure_ascii=False,indent=2))
finally:
    subprocess.run(['docker','rm','-f',container],stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL)
    subprocess.run(['docker','exec','mc-client-hub-postgres-1','dropdb','-U','hub',database],stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL)
    env_file.unlink(missing_ok=True)
