#!/bin/sh
set -eu
cd /var/apps/mc-client-hub
umask 077
mkdir -p /vol1/mc-client-hub/backups
stamp=$(date -u +%Y%m%dT%H%M%SZ)
target=/vol1/mc-client-hub/backups/hub-$stamp.dump
docker compose exec -T postgres pg_dump -U hub -d hub -Fc > "$target.tmp"
test -s "$target.tmp"
mv "$target.tmp" "$target"
python3 - <<'PY'
from pathlib import Path
root=Path('/vol1/mc-client-hub/backups').resolve()
for path in sorted(root.glob('hub-*.dump'),reverse=True)[7:]:
    if path.resolve().parent==root: path.unlink()
PY
