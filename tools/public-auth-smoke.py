"""Exercise the deployed HTTPS account service with a disposable, uniquely named account."""
import json
import argparse
from pathlib import Path
import re
import secrets
import shlex
import subprocess
import sys
import urllib.error
import urllib.request

ROOT=Path(__file__).resolve().parents[1]
SSH=ROOT.parent/'tools/ssh124.py'
checks=[]
parser=argparse.ArgumentParser(description=__doc__)
parser.add_argument('--native',action='store_true',help='Also exercise the compiled Windows account manager without opening a window.')
options=parser.parse_args()
tag=secrets.token_hex(5)
name='HubQA_'+tag
password=secrets.token_hex(20)
invite_id=None
user_id=None

def ssh(command):
    result=subprocess.run([sys.executable,str(SSH),'--user','Agent2','--sudo','--timeout','25',command],
        capture_output=True,text=True,encoding='utf-8',timeout=35)
    if result.returncode:raise RuntimeError('Remote smoke-test operation failed.')
    return result.stdout

def api(path,body=None,token=None):
    headers={'Content-Type':'application/json','User-Agent':'MojinDashuai/0.1.1'}
    if token:headers['Authorization']='Bearer '+token
    request=urllib.request.Request('https://launcher.boshan.uk'+path,
        data=None if body is None else json.dumps(body).encode(),headers=headers)
    try:
        with urllib.request.urlopen(request,timeout=20) as response:
            data=response.read();return response.status,json.loads(data) if data else None
    except urllib.error.HTTPError as error:
        data=error.read()
        try:result=json.loads(data) if data else None
        except ValueError:result=None
        return error.code,result

def check(condition,label):
    if not condition:raise RuntimeError(label)
    checks.append(label)

def cleanup():
    # Only the identifiers created by this run are used. Player accounts and protected names are untouched.
    if not invite_id:return
    if not re.fullmatch(r'[a-f0-9-]{36}',invite_id):raise RuntimeError('Unexpected test invitation identifier.')
    statements=['BEGIN;']
    if user_id:
        if not re.fullmatch(r'[a-f0-9-]{36}',user_id):raise RuntimeError('Unexpected test account identifier.')
        for table in ['InviteUses','Sessions','ResetGrants']:
            statements.append(f'DELETE FROM "{table}" WHERE "UserId"=\'{user_id}\';')
        statements.append(f'DELETE FROM "AspNetUsers" WHERE "Id"=\'{user_id}\' AND "UserName"=\'{name}\';')
    statements.append(f'DELETE FROM "Invitations" WHERE "Id"=\'{invite_id}\' AND "BoundGameName"=\'{name}\';')
    statements.append('COMMIT;')
    ssh('docker exec mc-client-hub-postgres-1 psql -U hub -d hub -v ON_ERROR_STOP=1 -c '+shlex.quote('\n'.join(statements)))

try:
    check(api('/health')==(200,{'status':'ok'}),'HTTPS health and TLS verification')
    output=ssh('docker exec mc-client-hub-hub-api-1 dotnet Hub.Api.dll admin invite-create single '+name+' 1')
    invite_id=re.search(r'Invitation ID: ([a-f0-9-]+)',output)[1]
    invitation=re.search(r'Code \(shown once\): ([a-f0-9]+)',output)[1]
    status,registered=api('/v1/auth/register',{'loginName':name,'gameName':name,'password':password,'invitation':invitation})
    check(status==200,'Register through Cloudflare Tunnel')
    user_id=registered['profile']['id']
    check(registered['profile']['gameName']==name and bool(registered['recoveryCode']),'Preserve exact game name and issue recovery code')
    check(api('/v1/account/me',token=registered['accessToken'])[0]==200,'Access-token authenticated profile')
    check(api('/v1/auth/logout',{},registered['accessToken'])[0]==204,'Logout through HTTPS')
    check(api('/v1/account/me',token=registered['accessToken'])[0]==401,'Logout revokes old access token')
    status,logged_in=api('/v1/auth/login',{'loginName':name,'password':password})
    check(status==200 and logged_in['profile']['id']==user_id,'Password login returns same account')
    status,refreshed=api('/v1/auth/refresh',{'refreshToken':logged_in['refreshToken']})
    check(status==200 and refreshed['refreshToken']!=logged_in['refreshToken'],'Refresh rotates credential through HTTPS')
    check(api('/v1/account/me',token=refreshed['accessToken'])[0]==200,'Refreshed access token is usable')
    check(api('/v1/auth/logout',{},refreshed['accessToken'])[0]==204,'End the smoke-test session')
    if options.native:
        assembly=ROOT/'tests/NativeAccountSmoke/bin/Release/net10.0-windows/NativeAccountSmoke.dll'
        if not assembly.is_file():raise RuntimeError('Build NativeAccountSmoke before running with --native.')
        native=subprocess.run([str(ROOT.parent/'.tools/dotnet10/dotnet.exe'),str(assembly)],
            input=json.dumps({'loginName':name,'password':password}),capture_output=True,text=True,encoding='utf-8',timeout=45)
        check(native.returncode==0,'Native account verification process')
        result=json.loads(native.stdout)
        checks.extend(result['checks'])
finally:
    try:cleanup()
    except Exception:sys.exit('Smoke-test cleanup failed; inspect only this test account before retrying.')

report={'passed':len(checks),'checks':checks,'temporaryAccountRemoved':True,'endpoint':'https://launcher.boshan.uk'}
(ROOT/'.local/public-auth-smoke.json').write_text(json.dumps(report,indent=2),encoding='utf-8')
print(json.dumps(report,indent=2))
