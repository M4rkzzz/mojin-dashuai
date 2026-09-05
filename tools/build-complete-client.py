"""Build an unsigned, complete native-client candidate in a NEW output directory.

Only explicit object roots/inventory references are read; no client directory is
scanned. Nothing is signed, uploaded, activated or changed at an existing origin.
Python 3.11+, standard library only (plus the adjacent pack_distribution module).
"""
from __future__ import annotations

import argparse
import contextlib
import copy
import fnmatch
import hashlib
import json
import os
from pathlib import Path
import re
import stat
import sys
import tempfile
import urllib.error
import urllib.parse
import urllib.request
import zipfile

from pack_distribution import SECRET, private_path, public_url, safe_path

RUNTIME_ENTRY = '__runtime/runtime.zip'
CHUNK = 1024 * 1024
SEED_DEFAULTS = {'options.txt', 'servers.dat'}
TEXT_SUFFIXES = {'.json', '.cfg', '.toml', '.properties', '.txt', '.ini', '.yml', '.yaml', '.xml'}
PERSONAL_PARTS = {'playerdata', 'playerstats', 'advancements', 'stats', 'itemfavorites', 'local', 'waypoints'}
PERSONAL_NAMES = {'accounts.json', 'credentials.json', 'tokens.json', 'refresh_tokens.json'}
OFFICIAL_HOSTS = {'cdn.modrinth.com', 'mediafilez.forgecdn.net', 'edge.forgecdn.net'}


class BuildError(ValueError):
    """A reviewable failure; messages must never contain content or credentials."""


def json_bytes(value):
    return (json.dumps(value, ensure_ascii=False, indent=2) + '\n').encode('utf-8')


def read_json(path):
    def unique_keys(pairs):
        result = {}
        for key, value in pairs:
            if key in result:
                raise BuildError('Duplicate JSON property')
            result[key] = value
        return result
    try:
        return json.loads(Path(path).read_text(encoding='utf-8-sig'), object_pairs_hook=unique_keys)
    except (UnicodeError, json.JSONDecodeError) as error:
        raise BuildError('Invalid UTF-8 JSON input') from error


def sha256(path):
    with Path(path).open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()


def digest_value(value):
    if not isinstance(value, str) or not re.fullmatch('[0-9a-fA-F]{64}', value):
        raise BuildError('Invalid SHA256')
    return value.lower()


def relative_path(value):
    try:
        safe_path(value)
    except ValueError as error:
        raise BuildError('Unsafe manifest or archive path') from error
    # Windows also reserves device names with whitespace before an extension.
    reserved = {'CON', 'PRN', 'AUX', 'NUL', 'CONIN$', 'CONOUT$',
                *('COM' + i for i in '123456789¹²³'), *('LPT' + i for i in '123456789¹²³')}
    if any(part.split('.')[0].rstrip().upper() in reserved for part in value.split('/')):
        raise BuildError('Windows device path is not allowed')
    return value


def anonymous_url(value):
    try:
        if not isinstance(value, str):
            raise ValueError()
        return public_url(value)
    except ValueError as error:
        raise BuildError('Only anonymous HTTPS URLs without credentials or query strings are accepted') from error


def validate_record(row):
    if not isinstance(row, dict):
        raise BuildError('Content record must be an object')
    relative_path(row.get('path'))
    digest_value(row.get('sha256'))
    if type(row.get('size')) is not int or row['size'] < 0:
        raise BuildError('Content size must be a nonnegative integer')
    if row.get('policy') not in ('managed', 'seed', 'preserve'):
        raise BuildError('Unknown content file policy')
    if not isinstance(row.get('distributionBasis'), str) or not row['distributionBasis'].strip():
        raise BuildError('Content record requires a distribution basis')
    if not isinstance(row.get('sources'), list) or not row['sources']:
        raise BuildError('Content record must retain its download sources')
    for url in row['sources']:
        anonymous_url(url)
    if type(row.get('officialOnly', False)) is not bool:
        raise BuildError('officialOnly must be a boolean')
    if row.get('officialOnly'):
        uri = urllib.parse.urlsplit(row['sources'][0])
        try:
            supported = uri.port in (None, 443) and uri.hostname in OFFICIAL_HOSTS and uri.path not in ('', '/')
        except ValueError:
            supported = False
        if len(row['sources']) != 1 or not supported:
            raise BuildError('officialOnly requires one fixed supported official CDN URL')


def validate_manifest(manifest, version, sequence, *, initial_release=False):
    if not isinstance(manifest, dict) or any(k in manifest for k in ('signature', 'payload', 'keyId')):
        raise BuildError('Supply a native manifest JSON, not a signed envelope')
    for key in ('instance', 'version', 'minecraft', 'loader', 'loaderVersion', 'launchVersion', 'compatibility'):
        if not isinstance(manifest.get(key), str) or not manifest[key]:
            raise BuildError('Native manifest identity is incomplete')
    if not isinstance(version, str) or not version or any(ord(c) < 32 for c in version):
        raise BuildError('An explicit new candidate version is required')
    if initial_release:
        if (type(sequence) is not int or type(manifest.get('sequence')) is not int
                or sequence != 1 or manifest['sequence'] != 1 or version != manifest['version']
                or manifest.get('bundles') or manifest.get('validationEvidence')):
            raise BuildError('Initial release requires matching version and sequence 1, without bundles or acceptance evidence')
    elif version == manifest['version']:
        raise BuildError('An explicit new candidate version is required')
    elif type(manifest.get('sequence')) is not int or manifest['sequence'] < 1 or type(sequence) is not int or sequence <= manifest['sequence']:
        raise BuildError('Candidate sequence must be greater than the input sequence')
    if not isinstance(manifest.get('files'), list) or not manifest['files']:
        raise BuildError('Native manifest must declare every required file')
    paths, sizes = set(), {}
    for row in manifest['files']:
        validate_record(row)
        path = row['path'].casefold()
        if path == '__runtime' or path.startswith('__runtime/'):
            raise BuildError('Manifest Files cannot occupy the reserved runtime directory')
        if path in paths:
            raise BuildError('Duplicate manifest destination: ' + row['path'])
        paths.add(path)
        if (private_path(path) and not (path in SEED_DEFAULTS and row['policy'] == 'seed')) or any(p in PERSONAL_PARTS for p in path.split('/')) or path.split('/')[-1] in PERSONAL_NAMES:
            raise BuildError('Player/private state is forbidden: ' + row['path'])
    for path in paths:
        if any('/'.join(path.split('/')[:i]) in paths for i in range(1, len(path.split('/')))):
            raise BuildError('Manifest contains a file/directory collision')
    runtime = manifest.get('runtime')
    if not isinstance(runtime, dict) or not isinstance(runtime.get('archive'), dict):
        raise BuildError('Complete client requires Runtime.Archive')
    relative_path(runtime.get('javaPath'))
    relative_path(runtime.get('id'))
    validate_record(runtime['archive'])
    if runtime['archive'].get('officialOnly'):
        raise BuildError('Runtime.Archive cannot be an officialOnly exception')
    if type(runtime.get('expandedSize')) is not int or runtime['expandedSize'] <= 0:
        raise BuildError('Runtime expanded size is invalid')
    for row in [*manifest['files'], runtime['archive']]:
        digest = digest_value(row['sha256'])
        if digest in sizes and sizes[digest] != row['size']:
            raise BuildError('Same SHA256 has conflicting sizes')
        sizes[digest] = row['size']
    official_hashes = {digest_value(row['sha256']) for row in manifest['files'] if row.get('officialOnly')}
    if official_hashes.intersection(digest_value(row['sha256']) for row in archive_rows(manifest).values()):
        raise BuildError('officialOnly bytes cannot be redistributed under another manifest path')
    return {digest_value(row['sha256']): row['size'] for row in archive_rows(manifest).values()}


def redistribution_rules(policy_path=None, denied=()):
    rules = [{'pathPattern': pattern, 'reason': 'Explicit operator redistribution block'} for pattern in denied]
    if policy_path:
        policy = read_json(policy_path)
        if not isinstance(policy, dict) or policy.get('schema') != 1 or not isinstance(policy.get('blocked'), list):
            raise BuildError('Redistribution policy requires schema:1 and blocked:[]')
        rules.extend(policy['blocked'])
    for rule in rules:
        if not isinstance(rule, dict) or bool(rule.get('pathPattern')) == bool(rule.get('sha256')):
            raise BuildError('Each redistribution block requires exactly one pathPattern or sha256')
        if rule.get('sha256'):
            digest_value(rule['sha256'])
        if rule.get('pathPattern') and (not isinstance(rule['pathPattern'], str) or '\\' in rule['pathPattern'] or rule['pathPattern'].startswith('/') or '..' in rule['pathPattern'].split('/')):
            raise BuildError('Invalid redistribution path pattern')
        if not isinstance(rule.get('reason'), str) or not rule['reason'].strip():
            raise BuildError('Redistribution blocks require a reason')
    return rules


def redistribution_blockers(manifest, rules):
    blocked = []
    for row in [*manifest['files'], manifest['runtime']['archive']]:
        matches = [i for i, rule in enumerate(rules) if
                   (rule.get('sha256') and digest_value(rule['sha256']) == digest_value(row['sha256'])) or
                   (rule.get('pathPattern') and fnmatch.fnmatchcase(row['path'].casefold(), rule['pathPattern'].casefold()))]
        if matches and not row.get('officialOnly'):
            # Policy prose is not echoed; administrators can identify the exact rule by index.
            blocked.append({'path': row['path'], 'sha256': row['sha256'], 'reason': 'redistribution-prohibited', 'ruleIndexes': matches})
    return blocked


def plain_path(path):
    """Reject symlinks and Windows junctions in every existing ancestor."""
    path = Path(os.path.abspath(path))
    for node in [path, *path.parents]:
        if not node.exists() and not node.is_symlink():
            continue
        info = node.lstat()
        if stat.S_ISLNK(info.st_mode) or getattr(info, 'st_file_attributes', 0) & getattr(stat, 'FILE_ATTRIBUTE_REPARSE_POINT', 0x400):
            raise BuildError('Source/output path contains a link or reparse point')
    return path


class Sources:
    def __init__(self, roots, inventory_path, required, download_base, staging):
        self.roots = [plain_path(root) for root in roots]
        if any(not root.is_dir() for root in self.roots):
            raise BuildError('Every explicit source root must be an existing regular directory')
        self.matches, self.downloaded = {}, {}
        self.download_base = anonymous_url(download_base).rstrip('/') if download_base else None
        self.staging = staging
        if inventory_path:
            inventory = read_json(inventory_path)
            if not isinstance(inventory, dict) or not isinstance(inventory.get('objects'), list):
                raise BuildError('Inventory requires an objects array')
            for row in inventory['objects']:
                digest = digest_value(row.get('sha256'))
                if digest not in required:
                    continue
                if digest in self.matches or row.get('size') != required[digest]:
                    raise BuildError('Duplicate inventory object or manifest size mismatch')
                self.matches[digest] = list(row.get('localMatches', [])) + [{'kind': 'file', 'path': p} for p in row.get('remotePaths', [])]

    def allowed(self, path):
        if not isinstance(path, (str, Path)) or not Path(path).is_absolute():
            return None
        path = Path(os.path.abspath(path))
        if not any(path.is_relative_to(root) for root in self.roots):
            return None
        if not path.is_file():
            return None
        path = plain_path(path)
        return path

    def candidates(self, digest):
        # Bounded exact-name probes, never recursive discovery or filename logging.
        for root in self.roots:
            for directory in ('', 'sha256', 'objects', 'objects/sha256'):
                for suffix in ('', '.bin', '.jar', '.zip', '.mrpack'):
                    yield {'kind': 'file', 'path': root / directory / (digest + suffix)}
        yield from self.matches.get(digest, [])

    @contextlib.contextmanager
    def open(self, row):
        digest, size = digest_value(row['sha256']), row['size']
        for match in self.candidates(digest):
            path = self.allowed(match.get('path'))
            if path is None:
                continue
            if match.get('kind') == 'file':
                if path.stat().st_size != size:
                    raise BuildError('Object size mismatch for SHA256 ' + digest)
                with path.open('rb') as stream:
                    yield stream, 'local-file'
                return
            if match.get('kind') == 'archive-entry':
                entry = relative_path(match.get('entry'))
                with zipfile.ZipFile(path) as archive:
                    members = [i for i in archive.infolist() if i.filename == entry]
                    if len(members) != 1 or members[0].is_dir() or members[0].file_size != size or stat.S_ISLNK(members[0].external_attr >> 16):
                        raise BuildError('Inventory archive entry is missing, duplicated, linked or has the wrong size')
                    with archive.open(members[0]) as stream:
                        yield stream, 'local-archive-entry'
                return
            raise BuildError('Unknown inventory source kind')
        if not self.download_base:
            raise BuildError('Required object unavailable in explicit roots: ' + digest)
        path = self.downloaded.get(digest)
        if path is None:
            path = self.staging / digest
            self.download(row, path)
            self.downloaded[digest] = path
        with path.open('rb') as stream:
            yield stream, 'download'

    def download(self, row, destination):
        class AnonymousRedirect(urllib.request.HTTPRedirectHandler):
            def redirect_request(self, req, fp, code, msg, headers, newurl):
                anonymous_url(newurl)
                return super().redirect_request(req, fp, code, msg, headers, newurl)
        digest = digest_value(row['sha256'])
        request = urllib.request.Request(self.download_base + '/' + digest, headers={'User-Agent': 'CompleteClientBuilder/1'})
        try:
            opener = urllib.request.build_opener(AnonymousRedirect())
            with opener.open(request, timeout=60) as source, destination.open('xb') as target:
                copy_verified(source, target, row, scan_text=False)
        except (urllib.error.URLError, OSError) as error:
            # urllib exceptions can include URLs; keep reports and console anonymous.
            raise BuildError('Object download failed for SHA256 ' + digest) from error


def copy_verified(source, target, row, scan_text=True):
    digest, size, tail = hashlib.sha256(), 0, b''
    scan_text = scan_text and Path(row['path']).suffix.lower() in TEXT_SUFFIXES
    while block := source.read(CHUNK):
        size += len(block)
        if size > row['size']:
            raise BuildError('Object exceeds its declared size: ' + row['path'])
        if scan_text:
            if SECRET.search(tail + block):
                raise BuildError('Possible credential in manifest content: ' + row['path'])
            tail = block[-1024:]
        digest.update(block)
        target.write(block)
    if size != row['size'] or digest.hexdigest() != digest_value(row['sha256']):
        raise BuildError('Object SHA256/size mismatch: ' + row['path'])


def archive_rows(manifest):
    rows = {row['path']: row for row in manifest['files'] if not row.get('officialOnly')}
    rows[RUNTIME_ENTRY] = manifest['runtime']['archive']
    return rows


def verify_bundle(path, manifest):
    expected = archive_rows(manifest)
    with zipfile.ZipFile(path) as archive:
        seen = set()
        for info in archive.infolist():
            relative_path(info.filename)
            if info.filename.casefold() in seen or info.filename not in expected or info.is_dir() or stat.S_ISLNK(info.external_attr >> 16):
                raise BuildError('ZIP contains an unexpected, duplicate, directory or linked entry')
            seen.add(info.filename.casefold())
            row = expected[info.filename]
            if info.file_size != row['size']:
                raise BuildError('ZIP entry size differs from manifest')
            with archive.open(info) as stream:
                if hashlib.file_digest(stream, 'sha256').hexdigest() != digest_value(row['sha256']):
                    raise BuildError('ZIP entry SHA256 differs from manifest')
        if len(seen) != len(expected):
            raise BuildError('ZIP is missing a manifest file or runtime archive')


def build(manifest_path, output, version, sequence, *, object_roots=(), inventory=None,
          download_base=None, public_base=None, redistribution_policy=None, denied=(), initial_release=False):
    manifest_path, output = plain_path(manifest_path), plain_path(output)
    if output == manifest_path or output.exists():
        raise BuildError('Output must be a new directory; existing files are never overwritten')
    # Exclusive creation reserves the destination; failures retain only report.json.
    output.mkdir(parents=True, exist_ok=False)
    report = {'schema': 1, 'candidate': False, 'signed': False, 'uploaded': False, 'activated': False,
              'gameAcceptance': False, 'inputSignatureVerified': False, 'blockers': [], 'verifiedFiles': 0,
              'initialRelease': initial_release}
    linked = []
    try:
        manifest = read_json(manifest_path)
        report['inputManifestSha256'] = sha256(manifest_path)
        required = validate_manifest(manifest, version, sequence, initial_release=initial_release)
        if not public_base:
            raise BuildError('An explicit anonymous --public-object-base is required for candidate bundle metadata')
        public_base = anonymous_url(public_base).rstrip('/')
        rules = redistribution_rules(redistribution_policy, denied)
        exceptions = [{'path': row['path'], 'sha256': row['sha256'], 'size': row['size'], 'source': row['sources'][0]}
                      for row in manifest['files'] if row.get('officialOnly')]
        report.update(instance=manifest['instance'], inputVersion=manifest['version'], inputSequence=manifest['sequence'],
                      version=version, sequence=sequence, declaredFiles=len(manifest['files']),
                      bundledFiles=len(manifest['files']) - len(exceptions),
                      expectedZipEntries=len(archive_rows(manifest)), uniqueObjects=len(required),
                      uniqueObjectBytes=sum(required.values()), redistributionRuleCount=len(rules),
                      officialOnlyFiles=exceptions, officialOnlyBytes=sum(row['size'] for row in exceptions),
                      officialOnlyDownloadsVerified=False if exceptions else None,
                      allManifestFilesInZip=not exceptions,
                      unpackedBundleBytes=sum(row['size'] for row in archive_rows(manifest).values()))
        report['blockers'] = redistribution_blockers(manifest, rules)
        if report['blockers']:
            raise BuildError('Redistribution policy blocks files without an explicit officialOnly exception')
        counts = {'local-file': 0, 'local-archive-entry': 0, 'download': 0}
        with tempfile.TemporaryDirectory(prefix='.build-', dir=output) as temporary:
            staging = Path(temporary)
            sources = Sources(object_roots, inventory, required, download_base, staging)
            archive_path = staging / 'complete-client.zip'
            with zipfile.ZipFile(archive_path, 'w', compression=zipfile.ZIP_DEFLATED, compresslevel=3, allowZip64=True) as archive:
                for name, row in sorted(archive_rows(manifest).items()):
                    info = zipfile.ZipInfo(name, (2026, 1, 1, 0, 0, 0))
                    info.compress_type = zipfile.ZIP_DEFLATED
                    info.create_system = 3
                    info.external_attr = 0o100644 << 16
                    with sources.open(row) as (source, kind), archive.open(info, 'w', force_zip64=True) as target:
                        copy_verified(source, target, row)
                    counts[kind] += 1
                    report['verifiedFiles'] += 1
            verify_bundle(archive_path, manifest)
            bundle_hash = sha256(archive_path)
            bundle = {'archive': {'path': 'complete-client.zip', 'size': archive_path.stat().st_size,
                                 'sha256': bundle_hash, 'sources': [public_base + '/' + bundle_hash],
                                 'policy': 'managed', 'distributionBasis': 'Complete native client assembled from pinned manifest objects'},
                      'prefix': '', 'complete': True}
            candidate = copy.deepcopy(manifest)
            candidate.update(version=version, sequence=sequence, bundles=[bundle], validationEvidence=[])
            (staging / 'manifest.candidate.json').write_bytes(json_bytes(candidate))
            (staging / 'bundle.json').write_bytes(json_bytes(bundle))
            report.update(candidate=True, archive='complete-client.zip', bundleSha256=bundle_hash,
                          bundleBytes=bundle['archive']['size'], verifiedZipEntries=report['expectedZipEntries'],
                          sourceReferences=counts, fileRecordsUnchanged=candidate['files'] == manifest['files'],
                          runtimeRecordUnchanged=candidate['runtime'] == manifest['runtime'],
                          candidateManifestSha256=sha256(staging / 'manifest.candidate.json'))
            for name in ('complete-client.zip', 'manifest.candidate.json', 'bundle.json'):
                # Hard-link publication is exclusive, including on Windows. A concurrent
                # writer cannot replace an artifact between existence check and rename.
                os.link(staging / name, output / name)
                metadata = (staging / name).stat()
                linked.append((output / name, metadata.st_dev, metadata.st_ino))
        (output / 'report.json').write_bytes(json_bytes(report))
        return report
    except Exception as error:
        for path, device, inode in linked:
            if path.exists():
                metadata = path.lstat()
                if (metadata.st_dev, metadata.st_ino) == (device, inode):
                    path.unlink()
        report['candidate'] = False
        report['error'] = str(error) if isinstance(error, BuildError) else 'Build failed: ' + type(error).__name__
        (output / 'report.json').write_bytes(json_bytes(report))
        raise BuildError(report['error']) from error


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--manifest', required=True, type=Path, help='Trusted native manifest; signed envelopes must first be verified by Publisher')
    parser.add_argument('--output', required=True, type=Path, help='NEW directory; never overwrite a manifest or previous build')
    parser.add_argument('--version', required=True, help='New candidate version')
    parser.add_argument('--sequence', required=True, type=int, help='Greater than the input manifest sequence')
    parser.add_argument('--initial-release', action='store_true', help='Explicit first release: matching version and sequence 1, no input bundles or acceptance evidence')
    parser.add_argument('--object-root', action='append', type=Path, default=[], help='Explicit allowed object/source root; repeatable, never recursively scanned')
    parser.add_argument('--inventory', type=Path, help='Optional audit-single-origin inventory; only matching hashes under allowed roots are used')
    parser.add_argument('--download-object-base', help='Opt in to missing downloads from HTTPS base/{sha256}; no original-source fallback')
    parser.add_argument('--public-object-base', required=True, help='Future public HTTPS base/{sha256}; metadata only, no upload')
    parser.add_argument('--redistribution-policy', type=Path, help='JSON {schema:1, blocked:[{pathPattern or sha256, reason}]}')
    parser.add_argument('--deny-redistribution', action='append', default=[], help='Block matching files unless explicitly officialOnly in the input manifest; repeatable')
    args = parser.parse_args(argv)
    try:
        report = build(args.manifest, args.output, args.version, args.sequence, object_roots=args.object_root,
                       inventory=args.inventory, download_base=args.download_object_base, public_base=args.public_object_base,
                       redistribution_policy=args.redistribution_policy, denied=args.deny_redistribution,
                       initial_release=args.initial_release)
    except (BuildError, OSError) as error:
        message = str(error) if isinstance(error, BuildError) else type(error).__name__
        print(json.dumps({'candidate': False, 'error': message}, ensure_ascii=True))
        return 2
    print(json.dumps({'candidate': True, 'files': report['declaredFiles'], 'bundledFiles': report['bundledFiles'],
                      'officialOnlyFiles': len(report['officialOnlyFiles']), 'bundleBytes': report['bundleBytes'],
                      'bundleSha256': report['bundleSha256'], 'signed': False, 'uploaded': False}, ensure_ascii=True))
    return 0


if __name__ == '__main__':
    sys.exit(main())
