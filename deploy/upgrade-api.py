"""Run on 124 after the new image passes the isolated account API acceptance suite."""
import datetime, json, pathlib, re, shutil, subprocess, sys, time, urllib.request

version=sys.argv[1]
if not re.fullmatch(r'\d+\.\d+\.\d+',version): raise SystemExit('Invalid release version')
root=pathlib.Path('/var/apps/mc-client-hub')
release=root/'releases'/('api-'+version)
if not (release/'api/Hub.Api.dll').is_file(): raise SystemExit('Prepared release is missing')

def run(*args): return subprocess.run(args,cwd=root,check=True,capture_output=True,text=True).stdout
def game_state():
    ids=run('docker','ps','-q').split()
    containers=json.loads(run('docker','inspect',*ids)) if ids else []
    return {c['Name']:c['State']['StartedAt'] for c in containers if not c['Name'].startswith(('/mc-client-hub-','/boshan-acceptance-'))}

before=game_state()
run('docker','image','inspect','boshan/hub-api:'+version)
run('sh','backup.sh')
stamp=datetime.datetime.now(datetime.timezone.utc).strftime('%Y%m%dT%H%M%SZ')
snapshots=pathlib.Path('/vol1/mc-client-hub/backups/upgrades')/('api-'+version+'-'+stamp)
snapshots.mkdir(parents=True,mode=0o700)
for pattern in ('hub-*.dump','skins-*.tar.gz'):
    backups=sorted(snapshots.parent.parent.glob(pattern))
    if backups: shutil.copy2(backups[-1],snapshots/backups[-1].name)
config=root/'compose.yml'
original=config.read_text()
updated,count=re.subn(r'image: boshan/hub-api:\d+\.\d+\.\d+','image: boshan/hub-api:'+version,original)
if count!=1: raise SystemExit('Unexpected API image configuration')
if 'SkinPath:' not in updated: updated=updated.replace('      DataProtectionPath: /data/keys','      DataProtectionPath: /data/keys\n      SkinPath: /data/skins')
shutil.copy2(config,snapshots/'compose.yml')
run('install','-d','-o','1654','-g','1654','-m','755','/vol1/mc-client-hub/api/skins')
staged=root/('api-staged-'+stamp)
shutil.copytree(release/'api',staged)
old_api=root/'api'
previous=root/'releases'/('api-before-'+stamp)
old_api.rename(previous)
staged.rename(old_api)
config.write_text(updated)
try:
    run('docker','compose','up','-d','--no-deps','--no-build','hub-api')
    healthy=False
    for _ in range(30):
        try:
            with urllib.request.urlopen('http://127.0.0.1:18081/health',timeout=2) as response:
                healthy=response.status==200
            if healthy: break
        except Exception: pass
        time.sleep(.5)
    if not healthy: raise RuntimeError('API health check failed')
except Exception:
    config.write_text(original)
    old_api.rename(root/'releases'/('api-failed-'+stamp))
    previous.rename(old_api)
    run('docker','compose','up','-d','--no-deps','--no-build','hub-api')
    raise
if before!=game_state(): raise RuntimeError('A non-hub container changed during deployment; check concurrent operations')
print(json.dumps({'apiVersion':version,'healthy':True,'preUpgradeBackup':str(snapshots),'otherContainerStartTimesUnchanged':True}))
