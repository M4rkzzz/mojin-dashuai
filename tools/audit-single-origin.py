"""Read-only inventory of the three manifests and retained distribution inputs.

Supply the TSV produced by: find /vol1/mc-client-hub/public/objects -type f
                            -printf '%P\t%s\n'
No files are downloaded, uploaded, extracted or changed by this audit.
"""
import argparse
import collections
import concurrent.futures
import datetime
import hashlib
import json
import os
import pathlib
import re
import stat
import zipfile

ROOT = pathlib.Path(__file__).resolve().parents[1]
parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--remote-index', type=pathlib.Path, default=ROOT / '.local/single-origin-remote-objects.tsv')
parser.add_argument('--remote-distributions', type=pathlib.Path, default=ROOT / '.local/single-origin-remote-distributions.tsv')
parser.add_argument('--remote-hashes', type=pathlib.Path, default=ROOT / '.local/single-origin-remote-sha256.tsv')
parser.add_argument('--output', type=pathlib.Path, default=ROOT / '.local/single-origin-inventory.json')
args = parser.parse_args()
roots = [ROOT / path for path in ['.local/source-cache', '.local/provided-release', '.local/runtimes', '.local/engines', 'artifacts/distributions']]
required = {}
manifests = []
for instance in ['m3e', 'dc2', 'mb']:
    path = ROOT / 'artifacts/native' / (instance + '-manifest.json')
    data = json.loads(path.read_text(encoding='utf-8-sig'))
    references = [(item, 'file') for item in data['files']]
    references += [(data['runtime']['archive'], 'runtime')]
    references += [(item['archive'], 'bundle') for item in data.get('bundles') or []]
    for item, kind in references:
        digest = item['sha256'].lower()
        if not re.fullmatch('[0-9a-f]{64}', digest):
            raise ValueError('Invalid manifest SHA256')
        entry = required.setdefault(digest, {'sha256':digest, 'size':item['size'], 'references':[], 'remotePaths':[], 'localMatches':[]})
        if entry['size'] != item['size']:
            raise ValueError('The same hash has conflicting sizes')
        entry['references'].append({'instance':instance, 'kind':kind, 'path':item['path']})
    unique = {item['sha256'].lower():item['size'] for item, _ in references}
    manifests.append({'instance':instance, 'path':str(path), 'sha256':hashlib.sha256(path.read_bytes()).hexdigest(), 'version':data['version'], 'sequence':data['sequence'], 'references':len(references), 'uniqueObjects':len(unique), 'uniqueBytes':sum(unique.values())})

remote_count = 0
remote_bytes = 0
remote_mismatches = []
remote_sizes = {}
for line in args.remote_index.read_text(encoding='utf-8-sig').splitlines():
    if not line.strip():
        continue
    path, length = line.rsplit('\t', 1)
    length = int(length)
    remote_count += 1
    remote_bytes += length
    remote_sizes['/vol1/mc-client-hub/public/objects/' + path] = length
    match = re.fullmatch(r'([0-9a-fA-F]{64})(?:\.(?:bin|jar|zip|mrpack))?', pathlib.PurePosixPath(path).name)
    if match is None:
        continue
    entry = required.get(match[1].lower())
    if entry is None:
        continue
    if entry['size'] != length:
        remote_mismatches.append({'path':path, 'observedSize':length, 'expectedSize':entry['size']})
    else:
        entry['remotePaths'].append('/vol1/mc-client-hub/public/objects/' + path)

distribution_count = 0
if args.remote_distributions.exists():
    for line in args.remote_distributions.read_text(encoding='utf-8-sig').splitlines():
        path, length = line.rsplit('\t', 1)
        remote_sizes['/vol1/mc-client-hub/public/distributions/' + path] = int(length)
        distribution_count += 1
remote_hashes_verified = args.remote_hashes.exists()
remote_hash_paths = set()
if remote_hashes_verified:
    for entry in required.values():
        entry['remotePaths'] = []
    for line in args.remote_hashes.read_text(encoding='utf-8-sig').splitlines():
        digest, path = line.split(None, 1)
        digest = digest.lower()
        if not re.fullmatch('[0-9a-f]{64}', digest) or path not in remote_sizes:
            raise ValueError('Remote SHA256 evidence differs from the size inventory')
        remote_hash_paths.add(path)
        entry = required.get(digest)
        if entry is not None and entry['size'] == remote_sizes[path]:
            entry['remotePaths'].append(path)
    if remote_hash_paths != set(remote_sizes):
        raise ValueError('Remote hashing did not cover the complete retained file inventory')
    for entry in required.values():
        entry['remotePaths'].sort(key=lambda path:(not '/objects/sha256/' in path, not '/objects/' in path, path))

needed = {digest:entry for digest, entry in required.items() if not entry['remotePaths']}
needed_sizes = {entry['size'] for entry in needed.values()}
local_stats = []
candidates = []
archives = []
seen_inodes = {}
for folder in roots:
    summary = {'root':str(folder), 'exists':folder.is_dir(), 'files':0, 'bytes':0, 'hashCandidates':0}
    local_stats.append(summary)
    if not folder.is_dir():
        continue
    for parent, directories, names in os.walk(folder, followlinks=False):
        directories[:] = [name for name in directories if not getattr(pathlib.Path(parent, name).stat(follow_symlinks=False), 'st_file_attributes', 0) & stat.FILE_ATTRIBUTE_REPARSE_POINT]
        for name in names:
            path = pathlib.Path(parent, name)
            metadata = path.stat(follow_symlinks=False)
            if not stat.S_ISREG(metadata.st_mode) or getattr(metadata, 'st_file_attributes', 0) & stat.FILE_ATTRIBUTE_REPARSE_POINT:
                continue
            summary['files'] += 1
            summary['bytes'] += metadata.st_size
            if path.suffix.lower() in ['.zip', '.mrpack']:
                archives.append(path)
            if metadata.st_size not in needed_sizes:
                continue
            named_hash = re.fullmatch(r'([0-9a-fA-F]{64})(?:\.[^/]+)?', name)
            if named_hash is not None and named_hash[1].lower() not in needed:
                continue
            inode = (metadata.st_dev, metadata.st_ino)
            if inode in seen_inodes:
                seen_inodes[inode].append(str(path))
                continue
            aliases = [str(path)]
            seen_inodes[inode] = aliases
            candidates.append((path, metadata.st_size, aliases))
            summary['hashCandidates'] += 1

def hash_candidate(candidate):
    path, size, aliases = candidate
    try:
        with path.open('rb') as content:
            digest = hashlib.file_digest(content, 'sha256').hexdigest()
        if digest in needed and needed[digest]['size'] == size:
            return digest, [{'path':name, 'kind':'file', 'sha256Verified':True} for name in aliases[:5]]
    except OSError as error:
        return None, [{'path':str(path), 'error':type(error).__name__}]
    return None, []

local_errors = []
with concurrent.futures.ThreadPoolExecutor(max_workers=4) as pool:
    for digest, matches in pool.map(hash_candidate, candidates):
        if digest is not None:
            needed[digest]['localMatches'].extend(matches[:max(0, 5-len(needed[digest]['localMatches']))])
        elif matches:
            local_errors.extend(matches)

# A retained standard pack can supply loose overrides without downloading the
# pack again. Hash archive entries in place; do not extract files onto disk.
archive_count = 0
archive_entry_hashes = 0
for path in archives:
    unresolved = {digest:entry for digest, entry in needed.items() if not entry['localMatches']}
    if not unresolved:
        break
    sizes = {entry['size'] for entry in unresolved.values()}
    try:
        with zipfile.ZipFile(path) as archive:
            archive_count += 1
            for item in archive.infolist():
                if item.is_dir() or item.file_size not in sizes or (item.external_attr >> 16) & 0o170000 == 0o120000:
                    continue
                with archive.open(item) as content:
                    digest = hashlib.file_digest(content, 'sha256').hexdigest()
                archive_entry_hashes += 1
                if digest in unresolved and unresolved[digest]['size'] == item.file_size:
                    needed[digest]['localMatches'].append({'path':str(path), 'kind':'archive-entry', 'entry':item.filename, 'sha256Verified':True})
                    del unresolved[digest]
    except (OSError, zipfile.BadZipFile, RuntimeError) as error:
        local_errors.append({'path':str(path), 'error':type(error).__name__})

def counts(entries):
    values = list(entries)
    return {'objects':len(values), 'bytes':sum(entry['size'] for entry in values)}

summary = {
    'required':counts(required.values()),
    'remoteReusable':counts(entry for entry in required.values() if entry['remotePaths']),
    'remoteMissing':counts(needed.values()),
    'remoteMissingRecoverableLocally':counts(entry for entry in needed.values() if entry['localMatches']),
    'remoteMissingUnresolved':counts(entry for entry in needed.values() if not entry['localMatches']),
    'remoteObjectFilesScanned':remote_count,
    'remoteObjectBytesScanned':remote_bytes,
    'remoteDistributionFilesScanned':distribution_count,
    'remoteContentHashesVerified':remote_hashes_verified,
    'localCandidateFilesHashed':len(candidates),
    'localArchivesInspected':archive_count,
    'localArchiveEntriesHashed':archive_entry_hashes,
}
report = {
    'generatedAt':datetime.datetime.now(datetime.timezone.utc).isoformat(),
    'readOnly':True,
    'scope':'All Files, Runtime.Archive and Bundles.Archive in the three native manifests; no launcher/API/login/skin assets included.',
    'remoteEvidence':{'objects':str(args.remote_index.resolve()), 'distributions':str(args.remote_distributions.resolve()) if args.remote_distributions.exists() else None, 'hashes':str(args.remote_hashes.resolve()) if remote_hashes_verified else None, 'root':'/vol1/mc-client-hub/public', 'verification':'Every retained origin file SHA256 checked against manifest plus size inventory.' if remote_hashes_verified else 'Filename SHA256 and recorded byte length only. Rehash origin objects before final migration or publication.', 'contentHashesVerified':remote_hashes_verified},
    'localEvidence':'Every localMatch is SHA256 verified. Archive entries are referenced but not extracted.',
    'summary':summary,
    'manifests':manifests,
    'localRoots':local_stats,
    'remoteSizeMismatches':remote_mismatches,
    'localReadErrors':local_errors,
    'objects':sorted(required.values(), key=lambda item:item['sha256']),
}
args.output.parent.mkdir(parents=True, exist_ok=True)
args.output.write_text(json.dumps(report, ensure_ascii=False, indent=2)+'\n', encoding='utf-8')
print(json.dumps({'report':str(args.output.resolve()), **summary, 'remoteSizeMismatches':len(remote_mismatches), 'localReadErrors':len(local_errors)}, ensure_ascii=False))
