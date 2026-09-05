"""Run on 124 after certbot DNS-01 issuance. Preserve legacy HTTP file downloads."""
import datetime,pathlib,shutil,subprocess,time,urllib.request
root=pathlib.Path('/var/apps/mc-client-hub')
tls=pathlib.Path('/vol1/mc-client-hub/tls')
if not (tls/'live/launcher-direct.boshan.uk/fullchain.pem').exists():raise SystemExit('TLS certificate missing')
def run(*args):return subprocess.run(args,check=True,capture_output=True,text=True,cwd=root).stdout
config=root/'compose.yml';nginx=root/'nginx.conf';original=config.read_text();original_nginx=nginx.read_text()
snapshot=root/'backups'/('direct-tls-'+datetime.datetime.now(datetime.timezone.utc).strftime('%Y%m%dT%H%M%SZ'))
snapshot.mkdir(parents=True);shutil.copy2(config,snapshot/'compose.yml');shutil.copy2(nginx,snapshot/'nginx.conf')
updated=original
mount='      - /vol1/mc-client-hub/tls:/etc/mojin-tls:ro'
if mount not in updated:
    anchor='      - ./nginx.conf:/etc/nginx/conf.d/default.conf:ro'
    if updated.count(anchor)!=1:raise SystemExit('Unexpected nginx mount')
    updated=updated.replace(anchor,anchor+'\n'+mount)
run('docker','run','--rm','--network','mc-client-hub_edge','-v','/tmp/mojin-direct-nginx.conf:/etc/nginx/conf.d/default.conf:ro','-v',str(tls)+':/etc/mojin-tls:ro','nginx:1.28-alpine','nginx','-t')
try:
    config.write_text(updated);shutil.copy2('/tmp/mojin-direct-nginx.conf',nginx)
    run('docker','compose','up','-d','--no-deps','--no-build','downloads')
    for _ in range(30):
        try:
            with urllib.request.urlopen('http://127.0.0.1:18080/health',timeout=2) as r:
                if r.status==200:break
        except Exception:time.sleep(.5)
    else:raise RuntimeError('Legacy HTTP health failed')
    run('curl','--silent','--show-error','--fail','--max-time','15','--resolve','launcher-direct.boshan.uk:18080:127.0.0.1','https://launcher-direct.boshan.uk:18080/v1/catalog','-o','/tmp/mojin-direct-catalog.json')
    for name in ['mc-client-hub-tls.service','mc-client-hub-tls.timer']:shutil.copy2('/tmp/'+name,'/etc/systemd/system/'+name)
    run('systemctl','daemon-reload');run('systemctl','enable','--now','mc-client-hub-tls.timer')
except Exception:
    config.write_text(original);nginx.write_text(original_nginx)
    run('docker','compose','up','-d','--no-deps','--no-build','downloads')
    raise
print('Direct TLS active; legacy HTTP files preserved; renewal timer enabled.')
