"""Read-only NAS audit of every known legacy role and account binding. No credentials."""
import collections
import datetime
import hashlib
import gzip
import json
import pathlib
import re
import subprocess
import uuid

ROOT = pathlib.Path('/var/apps/docker-gsmanager/shares/gsmanager/home/steam/games')
SERVERS = {'m3e': 'M3E66', 'dc2': 'DeceasedCraft-2', 'mb': 'MeatballCraft-0.18.6.4', 'vw': 'VoidWayfarer-4'}

def query(table, columns):
    sql = 'SELECT COALESCE(json_agg(x),\'[]\'::json) FROM (SELECT ' + columns + ' FROM "' + table + '") x;'
    result = subprocess.run(['docker', 'exec', 'mc-client-hub-postgres-1', 'psql', '-U', 'hub', '-d', 'hub', '-v', 'ON_ERROR_STOP=1', '-Atc', sql], check=True, capture_output=True, text=True)
    return json.loads(result.stdout)

def offline(name):
    return str(uuid.UUID(bytes=hashlib.md5(('OfflinePlayer:' + name).encode()).digest(), version=3))

protected = {r['Key']: r for r in query('ProtectedNames', '"Key","ExactName"')}
users = {r['GameNameKey']: r for r in query('AspNetUsers', '"Id","GameName","GameNameKey","Disabled"')}
identities = {r['GameNameKey']: r for r in query('JoinIdentities', '"HubUserId","MinecraftProfileId","GameName","GameNameKey","GameUuid","Disabled"')}
names = collections.defaultdict(set)
sources = collections.defaultdict(set)
cache_uuids = collections.defaultdict(set)
server_counts = {}
errors = []
for instance, folder in SERVERS.items():
    base = ROOT / folder
    if not base.is_dir():
        errors.append(instance + ': missing server directory')
        continue
    known_ids = set()
    for filename in ('usercache.json', 'usernamecache.json', 'whitelist.json', 'ops.json'):
        path = base / filename
        if not path.is_file():
            continue
        try:
            data = json.loads(path.read_text(encoding='utf-8-sig'))
            entries = [{'uuid': k, 'name': v} for k, v in data.items()] if isinstance(data, dict) else data
            for entry in entries:
                name = entry.get('name', '')
                if not isinstance(name, str) or not re.fullmatch(r'[A-Za-z0-9_]{3,16}', name):
                    continue
                key = name.upper()
                names[key].add(name)
                sources[key].add(instance)
                try:
                    game_id = str(uuid.UUID(entry.get('uuid', '')))
                    known_ids.add(game_id)
                    cache_uuids[name].add(game_id)
                except (ValueError, TypeError, AttributeError):
                    pass
        except Exception as error:
            errors.append(instance + '/' + filename + ': ' + type(error).__name__)
    properties = (base / 'server.properties').read_text(encoding='utf-8-sig')
    level = next((line.split('=', 1)[1] for line in properties.splitlines() if line.startswith('level-name=')), 'world')
    world = (base / level).resolve()
    if not world.is_relative_to(base.resolve()):
        errors.append(instance + ': unexpected world path')
        continue
    player_ids = {p.stem for p in (world / 'playerdata').glob('*.dat')}
    from_player_data = set()
    # M3E stores its historical player_name in NBT. Only accept a name whose
    # deterministic offline UUID matches this file, excluding names on items.
    marker = b'\x08\x00\x0bplayer_name'
    for path in sorted((world / 'playerdata').glob('*.dat')):
        if path.stem in known_ids:
            continue
        try:
            data = gzip.decompress(path.read_bytes())
            offset = 0
            while (offset := data.find(marker, offset)) >= 0:
                start = offset + len(marker)
                length = int.from_bytes(data[start:start+2], 'big')
                raw = data[start+2:start+2+length]
                offset = start + 2
                if not 3 <= length <= 16 or not re.fullmatch(rb'[A-Za-z0-9_]{3,16}', raw):
                    continue
                name = raw.decode('ascii')
                if offline(name) == path.stem:
                    names[name.upper()].add(name)
                    sources[name.upper()].add(instance)
                    known_ids.add(path.stem)
                    from_player_data.add(name)
        except Exception as error:
            errors.append(instance + '/playerdata/' + path.name + ': ' + type(error).__name__)
    # Recover evicted cache entries from historical login records, retaining names only.
    recovered = set()
    pattern = re.compile(r'UUID of player ([A-Za-z0-9_]{3,16}) is|\]: ([A-Za-z0-9_]{3,16})\[/[^\]]+\] logged in')
    for path in sorted((base / 'logs').glob('*')):
        if not path.name.endswith(('.log', '.log.gz')):
            continue
        try:
            opener = gzip.open if path.suffix == '.gz' else open
            with opener(path, 'rt', encoding='utf-8', errors='replace') as stream:
                for line in stream:
                    match = pattern.search(line)
                    if match:
                        name = next(value for value in match.groups() if value)
                        game_id = offline(name)
                        if game_id in player_ids:
                            names[name.upper()].add(name)
                            sources[name.upper()].add(instance)
                            known_ids.add(game_id)
                            recovered.add(name)
        except Exception as error:
            errors.append(instance + '/' + path.name + ': ' + type(error).__name__)
    server_counts[instance] = {'knownNames': sum(instance in value for value in sources.values()), 'namesRecoveredFromPlayerData': sorted(from_player_data), 'namesRecoveredFromLogs': sorted(recovered), 'playerDataFiles': len(player_ids), 'playerDataWithoutKnownName': sorted(player_ids - known_ids)}

for key, value in protected.items():
    if value['ExactName']:
        names[key].add(value['ExactName'])
for table in (users, identities):
    for key, value in table.items():
        names[key].add(value['GameName'])

rows = []
for key in sorted(set(names) | set(protected) | set(users) | set(identities)):
    legacy, hub, identity = protected.get(key), users.get(key), identities.get(key)
    variants = sorted(names[key])
    issues = []
    if len(variants) > 1 or (legacy is not None and not legacy['ExactName']):
        issues.append('case_conflict')
    if identity:
        if identity['Disabled'] or (hub and hub['Disabled']):
            issues.append('disabled')
        if identity['GameUuid'] != offline(identity['GameName']):
            issues.append('nonstandard_uuid_preserve')
        if hub and (identity['HubUserId'] != hub['Id'] or identity['GameName'] != hub['GameName']):
            issues.append('hub_binding_conflict')
        if identity['HubUserId'] and not hub:
            issues.append('orphan_hub_binding')
        if not identity['HubUserId'] and not identity['MinecraftProfileId']:
            issues.append('identity_without_provider')
        status = 'linked_both' if identity['HubUserId'] and identity['MinecraftProfileId'] else 'linked_microsoft' if identity['MinecraftProfileId'] else 'linked_hub'
    elif hub:
        status = 'hub_identity_missing'
        issues.append(status)
    elif issues:
        status = 'needs_review'
    else:
        status = 'automatic_on_verified_microsoft_login'
    rows.append({'key': key, 'names': variants, 'servers': sorted(sources[key]), 'protected': legacy is not None, 'protectedExactName': legacy['ExactName'] if legacy else None, 'boundExactName': identity['GameName'] if identity else None, 'status': status, 'issues': issues, 'gameUuid': identity['GameUuid'] if identity else offline(variants[0]) if len(variants) == 1 else None})

report = {'at': datetime.datetime.now(datetime.timezone.utc).isoformat(), 'readOnly': True, 'roleCount': len(rows), 'protectedCount': len(protected), 'hubAccountCount': len(users), 'joinIdentityCount': len(identities), 'statusCounts': dict(collections.Counter(row['status'] for row in rows)), 'issueCounts': dict(collections.Counter(issue for row in rows for issue in row['issues'])), 'servers': server_counts, 'errors': errors, 'roles': rows}
print(json.dumps(report, ensure_ascii=False, indent=2))
