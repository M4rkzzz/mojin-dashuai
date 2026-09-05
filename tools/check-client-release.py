"""Stop a player-facing release while authentication or required game content is unconfigured."""
import json
from pathlib import Path
from urllib.parse import urlsplit
import uuid

root=Path(__file__).resolve().parents[1]
errors=[]
config=json.loads((root/'src/Launcher.Desktop/launcher.json').read_text(encoding='utf-8-sig'))
try:
    client=uuid.UUID(config.get('microsoftClientId',''))
    if not client.int:raise ValueError()
except ValueError:
    errors.append('Microsoft application ID is missing. A player release cannot depend on a developer environment variable.')
endpoint=urlsplit(config.get('api',''))
if endpoint.scheme!='https' or not endpoint.hostname or endpoint.username or endpoint.password:
    errors.append('The account API must have a configured HTTPS endpoint.')
if not config.get('publicKeys'):
    errors.append('No release-signing public keys are embedded.')
for instance in ['m3e','dc2','mb']:
    audit=json.loads((root/f'packs/{instance}-source-audit.json').read_text(encoding='utf-8-sig'))
    if audit.get('releaseReady') is not True:
        errors.append(instance+': automated download sources and distribution audit are not complete.')
if errors:
    print('Player release blocked:')
    for message in errors:print('- '+message)
    raise SystemExit(1)
print('Required release configuration is present. Signed pack acceptance remains mandatory.')
