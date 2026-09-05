"""Run on 124 against a disposable database and isolated API container. Never print credentials."""
import concurrent.futures, json, os, pathlib, re, secrets, subprocess, time, urllib.request, urllib.error
import base64, struct, zlib, tempfile, hashlib

def run(*args):
    result=subprocess.run(args,check=True,capture_output=True,text=True)
    return result.stdout

stamp=secrets.token_hex(4)
database='hub_acceptance_'+stamp
container='boshan-acceptance-'+stamp
image=os.environ.get('HUB_API_TEST_IMAGE','boshan/hub-api:0.1.1')
assert re.fullmatch(r'hub_acceptance_[a-f0-9]{8}',database)
password=pathlib.Path('/var/apps/mc-client-hub/secrets/db-password').read_text().strip()
env_file=pathlib.Path('/var/apps/mc-client-hub/secrets/acceptance.env')
public_fixture=tempfile.TemporaryDirectory(prefix='mojin-api-metadata-')
public_path=pathlib.Path(public_fixture.name)
public_path.chmod(0o755)
env_file.write_text('ConnectionStrings__Hub=Host=mc-client-hub-postgres-1;Database='+database+';Username=hub;Password='+password+'\nInitializeDatabase=true\nASPNETCORE_URLS=http://+:8080\nTrustCloudflareTunnel=true\nSkinPath=/tmp/acceptance-skins\nPublicPath=/public-test\n')
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

def skin_png(height=64):
    def chunk(name,data):
        return struct.pack('>I',len(data))+name+data+struct.pack('>I',zlib.crc32(name+data))
    return b'\x89PNG\r\n\x1a\n'+chunk(b'IHDR',struct.pack('>IIBBBBB',64,height,8,6,0,0,0))+chunk(b'IDAT',zlib.compress(bytes(257*min(height,64))))+chunk(b'IEND',b'')

def public_skin(name):
    try:
        with urllib.request.urlopen('http://127.0.0.1:18082/v1/skins/'+name,timeout=10) as response:
            return response.status,response.read(),response.headers
    except urllib.error.HTTPError as error: return error.code,error.read(),error.headers
try:
    run('docker','create','--name',container,'--network','mc-client-hub_database','--env-file',str(env_file),'-v',str(public_path)+':/public-test:ro','-p','127.0.0.1:18082:8080',image)
    run('docker','network','connect','mc-client-hub_edge',container)
    run('docker','start',container)
    for _ in range(50):
        try:
            if api('/health')[0]==200: break
        except Exception: pass
        time.sleep(.5)
    check(api('/health')[0]==200,'isolated API healthy')
    check(api('/v1/catalog')[0]==503,'unpublished catalog stays unavailable')
    check(api('/v1/launcher')[0]==404,'unpublished launcher update returns not found')
    check(api('/v1/manifests/mb/1')[0]==404,'missing manifest returns not found')
    envelope={'keyId':'isolated-fixture','payload':'e30=','signature':'test-only'}
    manifest_path=public_path/'manifests'/'mb'/'1.signed.json'
    manifest_path.parent.mkdir(parents=True)
    manifest_path.write_text(json.dumps(envelope))
    (public_path/'catalog.signed.json').write_text(json.dumps(envelope))
    (public_path/'launcher.signed.json').write_text(json.dumps(envelope))
    check(api('/v1/launcher')==(200,envelope),'launcher update serves only the public signed envelope')
    check(api('/v1/catalog')==(200,envelope),'public catalog serves exact envelope without credentials')
    check(api('/v1/manifests/mb/1')==(200,envelope),'public manifest serves exact envelope without credentials')
    check(api('/v1/manifests/unknown/1')[0]==404 and api('/v1/manifests/mb/0')[0]==404,'manifest route restricts instance and positive release number')
    super_id,super_code=invite('super')
    code,alice=register('alice','Alice',super_code);check(code==200,'super invite first registration')
    code,bob=register('bobby','Bobby',super_code);check(code==200,'super invite reusable registration')
    check(alice['profile']['gameName']=='Alice','game name retains exact case')
    check('password' not in alice and 'invitation' not in alice,'secrets absent from profile response')
    skin={'pngBase64':base64.b64encode(skin_png()).decode(),'model':'slim'}
    check(api('/v1/account/skin',skin,ip='198.51.100.70')[0]==401,'skin upload requires a valid account')
    code,saved=api('/v1/account/skin',skin,token=alice['accessToken'],ip='198.51.100.70')
    check(code==200 and saved['model']=='slim','skin upload stores validated skin and model')
    code,png,headers=public_skin('Alice')
    check(code==200 and png.startswith(b'\x89PNG') and headers['Content-Type']=='image/png','public skin downloads work without account credentials')
    check(headers['X-Skin-Model']=='slim' and headers['X-Content-Type-Options']=='nosniff','skin response has model and explicit image type')
    code,profile=api('/v1/skins/csl/Alice.json')
    texture='Alice/'+hashlib.sha256(png).hexdigest()+'.png'
    check(code==200 and profile=={'username':'Alice','skins':{'slim':texture}},'CustomSkinAPI exposes exact slim model and content hash without account identifiers')
    with urllib.request.urlopen('http://127.0.0.1:18082/v1/skins/csl/textures/'+texture,timeout=10) as response:
        check(response.read()==png and response.headers['Content-Type']=='image/png','CustomSkinLoader texture URL returns exact uploaded PNG anonymously')
    check(api('/v1/skins/csl/Bobby.json')[0]==404,'missing group skin allows loader to continue to official skin')
    check(api('/v1/skins/csl/textures/Alice/'+('0'*64)+'.png')[0]==404,'wrong texture hash cannot return another skin')
    classic={**skin,'model':'classic'}
    check(api('/v1/account/skin',classic,token=alice['accessToken'],ip='198.51.100.70')[0]==200 and api('/v1/skins/csl/Alice.json')[1]['skins']=={'default':texture},'classic skin maps to CustomSkinLoader default model')
    check(public_skin('Bobby')[0]==404,'skin upload cannot change another account skin')
    invalid={'pngBase64':base64.b64encode(skin_png(8192)).decode(),'model':'classic'}
    check(api('/v1/account/skin',invalid,token=alice['accessToken'],ip='198.51.100.70')[0]==400,'oversized skin dimensions rejected')
    check(public_skin('Alice')[1]==png,'failed skin update preserves previous skin')
    admin('protect','OldPlayer')
    check(register('returning','OldPlayer',super_code)[0]==200,'super invite accepts an unbound protected game name')
    check(register('duplicate','oldplayer',super_code)[0]==400,'super invite cannot take an already bound game name')
    admin('protect','BoundPlayer')
    _,bound=invite('single','BoundPlayer')
    check(register('boundplayer','boundplayer',bound)[0]==400,'bound invite enforces exact case')
    check(register('boundplayer','BoundPlayer',bound)[0]==200,'bound invite claims protected original name')
    _,conflict_bound=invite('single','CaseHero')
    admin('protect-conflict','CaseHero','casehero')
    check(register('conflict1','CaseHero',super_code,ip='198.51.100.60')[0]==200,'super invite accepts unbound legacy case reservation')
    check(register('conflict2','casehero',super_code,ip='198.51.100.60')[0]==400,'case-insensitive account uniqueness still prevents duplicate claim')
    check(register('conflict3','CaseHero',conflict_bound,ip='198.51.100.60')[0]==400,'old bound invite cannot bypass unresolved name conflict')
    admin('protect','CaseHero')
    check(register('conflict4','CaseHero',super_code,ip='198.51.100.60')[0]==400,'protection changes cannot overwrite an existing account binding')
    try:
        invite('single','CaseHero')
        raise AssertionError('bound invite must not be created for unresolved conflict')
    except subprocess.CalledProcessError:
        checks.append('new bound invite rejected for unresolved name conflict')
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
    public_fixture.cleanup()
