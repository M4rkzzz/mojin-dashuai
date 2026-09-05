"""Install only the dedicated Tunnel credential; never accept the user's Global API Key."""
import datetime
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys

root=Path('/var/apps/mc-client-hub')

def run(*args):
    result=subprocess.run(args,cwd=root,check=True,capture_output=True,text=True,timeout=90)
    return result.stdout

def peers():
    ids=run('docker','ps','-q').split()
    data=json.loads(run('docker','inspect',*ids)) if ids else []
    return {item['Name']:item['State']['StartedAt'] for item in data if item['Name']!='/mc-client-hub-cloudflared-1'}

try:
    source=Path(sys.argv[1])
    if not re.fullmatch(r'/tmp/mojin-tunnel\.[A-Za-z0-9]{8}/tunnel-token',str(source)) or source.is_symlink() or source.resolve()!=source:
        raise SystemExit('Unexpected credential staging path; no changes made.')
    value=source.read_text().strip()
    # Tunnel run tokens are base64 payloads; a Global API Key must not be deployed here.
    if not re.fullmatch(r'[A-Za-z0-9+/=_-]{100,4096}',value) or value.startswith('cfk_'):
        raise SystemExit('The file is not a dedicated Tunnel token; no changes made.')
    image_user=run('docker','image','inspect','cloudflare/cloudflared:2026.8.3','--format','{{.Config.User}}').strip()
    if image_user not in ('65532','65532:65532'):
        raise SystemExit('Cloudflared image user changed; inspect permissions before deploying.')
    before=peers()
    destination=root/'secrets/tunnel-token'
    if destination.exists():
        backup=root/'backups'/('tunnel-'+datetime.datetime.now(datetime.timezone.utc).strftime('%Y%m%dT%H%M%SZ'))
        backup.mkdir(parents=True,mode=0o700)
        shutil.copy2(destination,backup/'tunnel-token')
    staged=destination.with_name('tunnel-token.new')
    with staged.open('x',encoding='utf-8') as stream:
        os.chmod(staged,0o400);os.chown(staged,65532,65532)
        stream.write(value);stream.flush();os.fsync(stream.fileno())
    os.replace(staged,destination)
    run('docker','compose','--profile','tunnel','config','--quiet')
    run('docker','compose','--profile','tunnel','up','-d','--no-deps','--no-build','cloudflared')
    source.unlink();source.parent.rmdir()
    after=peers()
    changed=[name for name,started in before.items() if after.get(name)!=started]
    state=run('docker','inspect','mc-client-hub-cloudflared-1','--format','{{.State.Status}}').strip()
    print(json.dumps({'cloudflared':state,'otherContainersUnchanged':not changed,'changedOtherContainers':changed}))
    if changed or state!='running':raise SystemExit('Deployment needs inspection before continuing.')
except SystemExit:
    raise
except Exception:
    raise SystemExit('Tunnel deployment did not complete; no credentials or container environment displayed.') from None
