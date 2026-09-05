"""Stop a player-facing release while authentication or required game content is unconfigured."""
import json
from pathlib import Path
from urllib.parse import urlsplit
import uuid
from release_config import auth_fingerprint

root=Path(__file__).resolve().parents[1]
errors=[]
config=json.loads((root/'src/Launcher.Desktop/launcher.json').read_text(encoding='utf-8-sig'))
if config.get('microsoftClientId','').strip():
    try:
        client=uuid.UUID(config['microsoftClientId'])
        if not client.int:raise ValueError()
    except ValueError:
        errors.append('The optional Microsoft device-code application ID must be a valid GUID.')
acceptance_path=root/'packs/launcher-acceptance.json'
acceptance=json.loads(acceptance_path.read_text()) if acceptance_path.is_file() else {}
if not all(acceptance.get(key) is True for key in ['liveMicrosoftLoginVerified','encryptedSessionRestored','silentAuthenticationPassed','gameSessionVerified']):
    errors.append('Real Microsoft authorization, encrypted session restore, silent authentication and game-session acceptance are not complete.')
elif acceptance.get('authFingerprint')!=auth_fingerprint():
    errors.append('Microsoft acceptance does not match the current authentication implementation and configuration.')
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
