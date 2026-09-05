"""Deterministic standard pack export, with a separate operator download inventory.

Drafts may describe unhosted fallback objects. Only checked builds are candidates
for publication; neither kind constitutes game/clean-Windows acceptance.
"""
import fnmatch
import hashlib
import json
from pathlib import Path, PurePosixPath
import re
import struct
import urllib.parse
import zipfile
from datetime import date

ROOT = Path(__file__).resolve().parents[1]
FORBIDDEN = {'pcl', 'saves', 'screenshots', 'logs', 'crash-reports', 'backups', 'journeymap',
             'xaeroworldmap', 'xaerowaypoints', 'profilecache', 'accounts', '.hub'}
PRIVATE_NAMES = {'usercache.json', 'usernamecache.json', 'servers.dat', 'servers.dat_old', '.reauth.cfg',
                 'launcher_accounts.json', 'launcher_profiles.json', 'knownkeys.txt', 'variables.dat',
                 'customskinapiplus-clientid', 'options.txt', 'optionsof.txt', 'optionsnf.txt'}
SECRET = re.compile(rb'(?i)(?:access[_-]?token|refresh[_-]?token|client[_-]?secret|password|api[_-]?key)\s*["\']?\s*[:=]\s*["\']?([A-Za-z0-9_./+\-]{16,})')


def update_source_status():
    path = ROOT / 'packs/standard-distribution-status.json'
    status = json.loads(path.read_text(encoding='utf-8')) if path.is_file() else {}
    entries = []
    for instance in ('m3e', 'dc2', 'mb'):
        audit = json.loads((ROOT / f'packs/{instance}-source-audit.json').read_text(encoding='utf-8'))
        rows = audit['files']
        original = lambda row: bool(row.get('downloadVerification', {}).get('verified') and (row.get('verifiedSources') or row.get('sources')))
        hosted = lambda row: bool(row.get('fallback', {}).get('publishedVerified'))
        entries.append({'instance': instance, 'files': len(rows), 'verifiedAuthorDownloads': sum(map(original, rows)),
                        'publishedFallbackFiles': sum(map(hosted, rows)),
                        'pendingFiles': [row['path'] for row in rows if not (original(row) or hosted(row))], 'releaseReady': bool(audit.get('releaseReady'))})
    status.update(asOf=date.today().isoformat(), instances=entries)
    path.write_text(json.dumps(status, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')


def safe_path(value):
    if not isinstance(value, str) or not value or '\\' in value or ':' in value or '\x00' in value:
        raise ValueError('Invalid distribution path')
    parts = value.split('/')
    if any(p in ('', '.', '..') or p[-1:] in (' ', '.') or any(ord(c) < 32 or c in '<>"|?*' for c in p)
           or p.split('.')[0].upper() in {'CON', 'PRN', 'AUX', 'NUL', *('COM'+str(i) for i in range(1, 10)), *('LPT'+str(i) for i in range(1, 10))}
           for p in parts):
        raise ValueError('Unsafe distribution path: ' + value)
    return value


def public_url(value):
    uri = urllib.parse.urlsplit(value)
    if uri.scheme != 'https' or not uri.hostname or uri.username or uri.password or uri.fragment or uri.query:
        raise ValueError('Only anonymous HTTPS file URLs are accepted')
    if any(ord(c) <= 32 for c in value):
        raise ValueError('URL must be encoded')
    return value


def private_path(value):
    parts = value.lower().split('/')
    return (any(p in FORBIDDEN or p.startswith('.') for p in parts)
            or parts[-1] in PRIVATE_NAMES or parts[-1].endswith(('.log', '.bak', '.old'))
            or parts[-1].startswith(('frpc', 'frps')))


def digest(data):
    return {name: hashlib.new(name, data).hexdigest() for name in ('sha1', 'sha256', 'sha512')}


def encode_json(value):
    return (json.dumps(value, ensure_ascii=False, sort_keys=True, indent=2) + '\n').encode('utf-8')


def write_zip(target, entries):
    target.parent.mkdir(parents=True, exist_ok=True)
    temp = target.with_suffix(target.suffix + '.tmp')
    seen = set()
    with zipfile.ZipFile(temp, 'w', compression=zipfile.ZIP_DEFLATED, compresslevel=6) as archive:
        for path, data in sorted(entries.items()):
            safe_path(path)
            if path.casefold() in seen:
                raise ValueError('Duplicate ZIP destination')
            seen.add(path.casefold())
            info = zipfile.ZipInfo(path, (2026, 9, 5, 0, 0, 0))
            info.compress_type = zipfile.ZIP_DEFLATED
            info.external_attr = 0o100644 << 16
            archive.writestr(info, data)
    temp.replace(target)


def servers_dat(routes):
    def string(s):
        b = s.encode('utf-8')
        return struct.pack('>H', len(b)) + b
    result = b'\x0a\x00\x00\x09' + string('servers') + b'\x0a' + struct.pack('>i', len(routes))
    for route in routes:
        result += b'\x08' + string('name') + string(route['name'])
        result += b'\x08' + string('ip') + string(route['host']) + b'\x00'
    return result + b'\x00'


def validate_spec(instance, spec):
    expected = {'m3e': ('1.7.10', 'forge', 8), 'dc2': ('1.20.1', 'forge', 17), 'mb': ('1.12.2', 'cleanroom', 25)}
    if (spec['minecraft'], spec['loader'], spec['javaMajor']) != expected[instance]:
        raise ValueError('Instance loader/runtime policy violation')
    if instance == 'mb' and spec['format'] != 'multimc-packwiz':
        raise ValueError('Cleanroom must retain its custom MMC component patches')


def override_files(source, spec):
    entries = {}
    excluded = []
    for include in spec['include']:
        safe_path(include)
        directory = source / include
        if not directory.exists():
            continue
        for path in sorted(directory.rglob('*')):
            if not path.is_file():
                continue
            relative = safe_path(path.relative_to(source).as_posix())
            if not path.resolve().is_relative_to(source.resolve()) or path.is_symlink():
                raise ValueError('Linked file in supplied overrides')
            if private_path(relative) or any(fnmatch.fnmatchcase(relative.lower(), p.lower()) for p in spec['exclude']):
                excluded.append(relative)
                continue
            data = path.read_bytes()
            if path.suffix.lower() in {'.json', '.cfg', '.toml', '.properties', '.txt', '.ini', '.yml', '.yaml'} and SECRET.search(data):
                raise ValueError('Possible credential requires local review: ' + relative)
            entries[relative] = data
    # Seed defaults contain no previous account, server IP, world or personal key bindings.
    entries['options.txt'] = ('lang:' + ('zh_CN' if spec['minecraft'] == '1.7.10' else 'zh_cn') + '\nfullscreen:false\n').encode()
    entries['servers.dat'] = servers_dat(spec['routes'])
    return entries, excluded


def mrpack_index(spec, files):
    if spec['loader'] != 'forge':
        raise ValueError('Cleanroom is not a Modrinth dependency ID')
    return {'formatVersion': 1, 'game': 'minecraft', 'versionId': spec['version'], 'name': spec['name'],
            'dependencies': {'minecraft': spec['minecraft'], 'forge': spec['loaderVersion']},
            'files': [{'path': safe_path(f['path']), 'hashes': {'sha1': f['sha1'], 'sha512': f['sha512']},
                       'downloads': [public_url(u) for u in f['sources']], 'fileSize': f['size'],
                       'env': {'client': 'required', 'server': 'unsupported'}} for f in files]}


def packwiz_files(spec, files, overrides):
    # All versions are fixed. No updater blocks that could silently upgrade individual mods.
    entries = dict(overrides)
    metadata = set()
    for row in files:
        path = PurePosixPath(row['path'])
        key = str(path.parent / (path.name + '.pw.toml'))
        if key in entries:
            raise ValueError('Packwiz metadata collision')
        q = lambda s: json.dumps(s, ensure_ascii=False)
        entries[key] = (f'name = {q(path.name)}\nfilename = {q(path.name)}\nside = "client"\n\n[download]\n'
                        f'url = {q(public_url(row["sources"][0]))}\nhash-format = "sha256"\nhash = "{row["sha256"]}"\n').encode('utf-8')
        metadata.add(key)
    index = 'hash-format = "sha256"\n'
    for name, data in sorted(entries.items()):
        safe_path(name)
        index += f'\n[[files]]\nfile = {json.dumps(name, ensure_ascii=False)}\nhash = "{hashlib.sha256(data).hexdigest()}"\n'
        if name in metadata:
            index += 'metafile = true\n'
        if name in ('options.txt', 'servers.dat'):
            index += 'preserve = true\n'
    entries['index.toml'] = index.encode('utf-8')
    versions = f'minecraft = "{spec["minecraft"]}"\n'
    if spec['loader'] == 'forge':
        versions += f'forge = "{spec["loaderVersion"]}"\n'
    # packwiz installs content, not Cleanroom. The official MMC patches supply that loader.
    entries['pack.toml'] = (f'name = {json.dumps(spec["name"], ensure_ascii=False)}\nversion = "{spec["version"]}"\n'
        'pack-format = "packwiz:1.1.0"\n\n[index]\nfile = "index.toml"\nhash-format = "sha256"\n'
        f'hash = "{hashlib.sha256(entries["index.toml"]).hexdigest()}"\n\n[versions]\n{versions}').encode('utf-8')
    return entries


def cleanroom_entries(spec, pack_url, official_zip, bootstrap):
    if hashlib.sha256(official_zip.read_bytes()).hexdigest() != 'efbd745faa8a97b0e6552793e1a30851162f02e0257d74e408ec28ebaf0bd17b':
        raise ValueError('Cleanroom reference archive hash mismatch')
    if hashlib.sha256(bootstrap.read_bytes()).hexdigest() != 'a8fbb24dc604278e97f4688e82d3d91a318b98efc08d5dbfcbcbcab6443d116c':
        raise ValueError('Pinned packwiz bootstrap hash mismatch')
    installer = bootstrap.with_name('packwiz-installer.jar')
    if hashlib.sha256(installer.read_bytes()).hexdigest() != 'c9f646908d340d84773948a9a7d98bc1dae250d35e1016dc6e2b8459760b5598':
        raise ValueError('Pinned packwiz installer hash mismatch')
    with zipfile.ZipFile(official_zip) as archive:
        entries = {name: archive.read(name) for name in archive.namelist()
                   if name in ('mmc-pack.json', 'cleanroom.png') or name.startswith('patches/') and name.endswith('.json')}
    mc = json.loads(entries['patches/net.minecraft.json'])
    mc['compatibleJavaMajors'] = [25]
    entries['patches/net.minecraft.json'] = encode_json(mc)
    loader = json.loads(entries['patches/net.minecraftforge.json'])
    if loader['name'] != 'Cleanroom' or loader['version'] != spec['loaderVersion']:
        raise ValueError('Cleanroom component identity mismatch')
    # net.minecraftforge is the upstream Cleanroom component UID; retaining the
    # custom patch is essential. PCL's MMC importer discards it and is unsupported.
    cfg = ('InstanceType=OneSix\nname=' + spec['name'] + '\niconKey=cleanroom\n'
           'OverrideMemory=true\nMinMemAlloc=2048\nMaxMemAlloc=8736\n'
           'IgnoreJavaCompatibility=false\nOverrideCommands=true\n'
           'PreLaunchCommand="$INST_JAVA" -Djava.awt.headless=true -jar packwiz-installer-bootstrap.jar --bootstrap-no-update -g ' + public_url(pack_url) + '\n')
    entries['instance.cfg'] = cfg.encode('utf-8')
    entries['.minecraft/packwiz-installer-bootstrap.jar'] = bootstrap.read_bytes()
    entries['.minecraft/packwiz-installer.jar'] = installer.read_bytes()
    for notice in (ROOT / 'packs/notices').glob('*-LICENSE.txt'):
        entries['licenses/' + notice.name] = notice.read_bytes()
    entries['licenses/SOURCES.txt'] = (
        'Cleanroom 0.5.17-alpha: https://github.com/CleanroomMC/Cleanroom/tree/0.5.17-alpha\n'
        'Official MMC patches retained, except compatibleJavaMajors narrowed to [25].\n'
        'packwiz-installer 0.5.14: https://github.com/packwiz/packwiz-installer/tree/v0.5.14\n'
        'packwiz-installer-bootstrap 0.0.3: https://github.com/packwiz/packwiz-installer-bootstrap/tree/v0.0.3\n').encode()
    return entries


def build(instance, config, destination, draft=False):
    spec = config['instances'][instance]
    validate_spec(instance, spec)
    source = Path(spec['source'])
    if not source.is_absolute():
        source = (ROOT / source).resolve()
    audit = json.loads((ROOT / f'packs/{instance}-source-audit.json').read_text(encoding='utf-8'))
    overrides, excluded = override_files(source, spec)
    seen = set()
    records = []
    blockers = []
    planned = []
    public = destination / 'public'
    for row in audit['files']:
        path = safe_path(row['path'])
        if path.casefold() in seen or private_path(path):
            raise ValueError('Duplicate or private pack file: ' + path)
        seen.add(path.casefold())
        local = source / path
        if instance == 'dc2':
            local = ROOT / '.local/source-cache' / (row.get('sha256', 'missing') + '.jar')
        if not local.is_file():
            blockers.append({'path': path, 'reason': 'exact file not available for hashing'})
            continue
        data = local.read_bytes()
        hashes = digest(data)
        if len(data) != row['size'] or hashes['sha1'] != row['sha1'] or row.get('sha256', hashes['sha256']) != hashes['sha256']:
            raise ValueError('Baseline hash mismatch: ' + path)
        sources = row.get('verifiedSources', [])
        if not sources and row.get('downloadVerification', {}).get('verified'):
            sources = row['sources'][:1]
        # Missing upstream files use the operator's supplied bytes through the same download service.
        fallback = row.get('fallback', {})
        if fallback.get('publishedVerified') and fallback.get('distributionBasis'):
            sources = [*sources, public_url(fallback['url'])]
        if not sources:
            url = config['publicBase'] + '/objects/' + hashes['sha256'] + '.jar'
            planned.append({'path': path, 'sha256': hashes['sha256'], 'size': len(data), 'url': url,
                            'distributionBasis': fallback.get('distributionBasis'), 'publishedVerified': False})
            blockers.append({'path': path, 'reason': 'fallback not uploaded; run with --publish-missing'})
            if not draft:
                continue
            sources = [url]
        basis = row.get('distributionBasis') or ''
        if fallback.get('publishedVerified'):
            basis += ('; ' if basis else '') + 'Self-hosted fallback: ' + fallback['distributionBasis']
        records.append({'path': path, 'size': len(data), **hashes, 'sources': list(dict.fromkeys(sources)),
                        'policy': 'managed', 'distributionBasis': basis or None,
                        'projectId': row.get('projectId'), 'fileId': row.get('fileId')})
        if path in overrides:
            # Two copies of a resource pack must not make the portable export large.
            if digest(overrides[path])['sha256'] != hashes['sha256']:
                raise ValueError('Manifest and overrides disagree: ' + path)
            del overrides[path]
    if instance != 'dc2':
        # Legacy 1.12 Forge downloader caches and extracted historical core JARs
        # are not modpack inputs. Cleanroom's pinned components supply its libraries.
        actual = {p.relative_to(source).as_posix().casefold() for p in (source / 'mods').rglob('*.jar')
                  if not any(fnmatch.fnmatchcase(p.relative_to(source).as_posix().lower(), rule.lower())
                             for rule in spec.get('modCacheExcludes', []))}
        if actual != seen:
            raise ValueError('Nested required mods are missing from the pinned source inventory')
    # A draft with unavailable files must not silently describe a reduced modpack.
    complete = len(records) == len(audit['files'])
    report = {'instance': instance, 'format': spec['format'], 'candidate': not blockers,
              'releaseReady': False, 'declaredFiles': len(audit['files']), 'resolvedFiles': len(records),
              'overrideFiles': len(overrides), 'excludedPrivateOrDisabledFiles': len(excluded),
              'blockers': blockers, 'fallbackObjects': planned, 'portableArtifact': None}
    destination.mkdir(parents=True, exist_ok=True)
    (destination / f'{instance}-report.json').write_bytes(encode_json(report))
    if not complete or (blockers and not draft):
        return report
    basename = f'{instance}-{spec["version"]}' + ('-draft' if draft else '')
    version_base = 'distributions/' + instance + '/' + spec['version']
    packwiz = packwiz_files(spec, records, overrides)
    for name, data in packwiz.items():
        target = public / version_base / safe_path(name)
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_bytes(data)
    if spec['format'] == 'modrinth':
        entries = {'modrinth.index.json': encode_json(mrpack_index(spec, records))}
        entries.update({'overrides/' + name: data for name, data in overrides.items()})
        artifact = destination / (basename + '.mrpack')
    else:
        entries = cleanroom_entries(spec, config['publicBase'] + '/' + version_base + '/pack.toml',
            ROOT / '.local/cleanroom-0.5.17-alpha-mmc.zip', ROOT / '.local/packwiz-installer-bootstrap.jar')
        artifact = destination / (basename + '.zip')
    write_zip(artifact, entries)
    # Separate portable-format metadata from our signed native distribution/Java policy.
    inventory = {'instance': instance, 'version': spec['version'], 'minecraft': spec['minecraft'],
                 'loader': spec['loader'], 'loaderVersion': spec['loaderVersion'],
                 'runtime': json.loads((ROOT / f'packs/java-{spec["javaMajor"]}-source.json').read_text()),
                 'javaMajor': spec['javaMajor'], 'routes': spec['routes'], 'files': records,
                 'releaseReady': False, 'candidate': not blockers}
    (destination / f'{instance}-content.json').write_bytes(encode_json(inventory))
    report.update({'portableArtifact': artifact.name, 'artifactBytes': artifact.stat().st_size,
                   'artifactSha256': digest(artifact.read_bytes())['sha256']})
    (destination / f'{instance}-report.json').write_bytes(encode_json(report))
    return report
