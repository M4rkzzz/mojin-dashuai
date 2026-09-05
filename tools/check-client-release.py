"""Stop a player-facing release while authentication or required game content is unconfigured."""
import argparse,json
from pathlib import Path
from urllib.parse import urlsplit
import uuid
from release_config import auth_fingerprint

root=Path(__file__).resolve().parents[1]
parser=argparse.ArgumentParser()
parser.add_argument('--beta',action='store_true',help='Use the explicitly approved beta acceptance without claiming clean Windows passed')
args=parser.parse_args()
errors=[]
if args.beta:
    decision=json.loads((root/'packs/beta-authorization.json').read_text(encoding='utf-8'))
    if decision.get('approved') is not True or decision.get('deferredChecks')!=['cleanWindows']:
        errors.append('Beta publication must record the user-approved clean Windows deferral.')
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
    missing=[row['path'] for row in audit['files'] if not (row.get('fallback',{}).get('publishedVerified') or
             (row.get('downloadVerification',{}).get('verified') and (row.get('verifiedSources') or row.get('sources'))))]
    if missing:
        errors.append(instance+': '+str(len(missing))+' files still need automatic fallback publication.')
    if args.beta:
        sequence=audit.get('betaSequence')
        if not isinstance(sequence,int) or sequence<=0:
            errors.append(instance+': beta sequence is missing.')
            continue
        evidence=json.loads((root/f'packs/acceptance/{instance}-{sequence}.json').read_text(encoding='utf-8'))
        if evidence.get('manifestSha256')!=audit.get('betaManifestSha256'):
            errors.append(instance+': beta acceptance does not match its content audit.')
        if audit.get('betaReady') is not True or evidence.get('channel')!='beta' or not all(evidence.get(k) is True for k in ['passed','joinedServer','allSourcesAutomated']) or evidence.get('javaMajor')!={'m3e':8,'dc2':17,'mb':25}[instance]:
            errors.append(instance+': beta acceptance is not complete.')
        if instance=='mb' and (evidence.get('loader')!='cleanroom' or not all(evidence.get(k) is True for k in ['map','quests','machines'])):
            errors.append('MeatballCraft game acceptance is not complete.')
    elif audit.get('releaseReady') is not True:
        errors.append(instance+': native content release and game acceptance are not complete.')
if errors:
    print('Player release blocked:')
    for message in errors:print('- '+message)
    raise SystemExit(1)
print(('Beta' if args.beta else 'Stable')+' release configuration is present. Signed pack acceptance remains mandatory.')
