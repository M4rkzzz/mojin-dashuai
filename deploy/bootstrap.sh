#!/bin/sh
set -eu
umask 077
mkdir -p /var/apps/mc-client-hub /vol1/mc-client-hub/postgres /vol1/mc-client-hub/api/keys /vol1/mc-client-hub/public /vol1/mc-client-hub/backups
cd /var/apps/mc-client-hub
tar -xzf /tmp/boshan-hub-deploy.tar.gz -C /var/apps/mc-client-hub
mkdir -p secrets
python3 - <<'PY'
from pathlib import Path
import secrets,os
root=Path('/var/apps/mc-client-hub/secrets')
password=root/'db-password'
if not password.exists(): password.write_text(secrets.token_hex(32))
env=root/'api.env'
if not env.exists(): env.write_text('ConnectionStrings__Hub=Host=postgres;Database=hub;Username=hub;Password='+password.read_text().strip()+'\n')
for p in (password,env): os.chmod(p,0o600)
PY
chown -R 1654:1654 /vol1/mc-client-hub/api
docker compose up -d --build postgres hub-api downloads
install -m 644 mc-client-hub-backup.service /etc/systemd/system/mc-client-hub-backup.service
install -m 644 mc-client-hub-backup.timer /etc/systemd/system/mc-client-hub-backup.timer
systemctl daemon-reload
systemctl enable --now mc-client-hub-backup.timer
docker compose ps
