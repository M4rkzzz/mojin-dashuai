"""Prepare one immutable-object tar locally, or add it to the existing origin.

prepare never uploads; import never changes release metadata or existing objects.
"""
import argparse
import contextlib
import hashlib
import io
import json
import os
import pathlib
import re
import shutil
import tarfile
import uuid
import zipfile

def valid_hash(value):
    return isinstance(value, str) and re.fullmatch('[0-9a-f]{64}', value)

def hash_file(path):
    with path.open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()

class HashingReader:
    def __init__(self, stream):
        self.stream, self.digest = stream, hashlib.sha256()
    def read(self, size=-1):
        block = self.stream.read(size)
        self.digest.update(block)
        return block

@contextlib.contextmanager
def local_source(match, size):
    path = pathlib.Path(match['path'])
    if path.is_symlink() or not path.is_file():
        raise ValueError('Local source disappeared or became a link')
    if match['kind'] == 'file':
        if path.stat().st_size != size:
            raise ValueError('Local source size changed since audit')
        with path.open('rb') as stream:
            yield stream
    elif match['kind'] == 'archive-entry':
        with zipfile.ZipFile(path) as archive:
            item = archive.getinfo(match['entry'])
            if item.file_size != size or item.is_dir():
                raise ValueError('Local archive entry changed since audit')
            with archive.open(item) as stream:
                yield stream
    else:
        raise ValueError('Unknown local match kind')

def prepare(inventory_path, output):
    inventory = json.loads(inventory_path.read_text(encoding='utf-8-sig'))
    if not inventory['remoteEvidence']['contentHashesVerified']:
        raise ValueError('Hash the existing origin files before staging the migration')
    for manifest in inventory['manifests']:
        if hash_file(pathlib.Path(manifest['path'])) != manifest['sha256']:
            raise ValueError('A native manifest changed since the audit; rerun inventory')
    objects, local = [], {}
    for item in inventory['objects']:
        digest = item['sha256']
        if not valid_hash(digest) or item['size'] < 0:
            raise ValueError('Invalid inventory object')
        remote_paths = item['remotePaths'][:3]
        if not remote_paths:
            if not item['localMatches']:
                raise ValueError('Required object is unavailable: ' + digest)
            local[digest] = item['localMatches'][0]
        objects.append({'sha256':digest, 'size':item['size'], 'remotePaths':remote_paths, 'fromTar':not remote_paths})
    if len({item['sha256'] for item in objects}) != len(objects):
        raise ValueError('Duplicate inventory object')
    plan = {'schema':1, 'totalObjects':len(objects), 'totalBytes':sum(item['size'] for item in objects), 'objects':objects}
    payload = json.dumps(plan, separators=(',', ':')).encode()
    output.parent.mkdir(parents=True, exist_ok=True)
    if output.exists():
        raise FileExistsError('Staging archive already exists')
    temporary = output.with_name(output.name + '.part-' + uuid.uuid4().hex)
    with tarfile.open(temporary, 'w', format=tarfile.PAX_FORMAT) as archive:
        info = tarfile.TarInfo('origin-plan.json');info.size = len(payload);info.mode = 0o600
        archive.addfile(info, io.BytesIO(payload))
        for item in objects:
            digest = item['sha256']
            if not item['fromTar']:
                continue
            info = tarfile.TarInfo('objects/sha256/' + digest);info.size = item['size'];info.mode = 0o644
            with local_source(local[digest], item['size']) as stream:
                reader = HashingReader(stream)
                archive.addfile(info, reader)
                if reader.digest.hexdigest() != digest:
                    raise ValueError('Local source SHA256 changed since audit: ' + digest)
    temporary.replace(output)
    report = {'prepared':True, 'uploaded':False, 'archive':str(output.resolve()), 'archiveBytes':output.stat().st_size,
              'totalObjects':len(objects), 'totalBytes':plan['totalBytes'], 'localObjects':len(local),
              'localBytes':sum(item['size'] for item in objects if item['fromTar']), 'originReusableObjects':len(objects)-len(local)}
    output.with_suffix(output.suffix + '.json').write_text(json.dumps(report, indent=2)+'\n', encoding='utf-8')
    print(json.dumps(report))

def safe_path(root, relative):
    pieces = pathlib.PurePosixPath(relative).parts
    if not pieces or any(piece in ['..', '.'] for piece in pieces) or pathlib.PurePosixPath(relative).is_absolute():
        raise ValueError('Unsafe origin relative path')
    path = root.joinpath(*pieces)
    if not path.resolve().is_relative_to(root) or any(parent.is_symlink() for parent in [path, *path.parents] if parent != root.parent):
        raise ValueError('Origin path is a link or outside the public root')
    return path

def import_archive(archive_path, public_root):
    root = public_root.resolve()
    if not root.is_dir() or public_root.is_symlink():
        raise ValueError('The existing public root must be a regular directory')
    run = uuid.uuid4().hex
    report = {'complete':False, 'objects':0, 'existing':0, 'linked':0, 'copied':0, 'uploadedObjects':0, 'bytes':0, 'releaseMetadataChanged':False}
    with tarfile.open(archive_path, 'r:*') as archive:
        members = {}
        for member in archive.getmembers():
            if not member.isfile() or member.name in members:
                raise ValueError('Staging archive contains a link, directory or duplicate')
            members[member.name] = member
        info = members.get('origin-plan.json')
        if info is None or info.size > 32 * 1024 * 1024:
            raise ValueError('Missing or oversized origin plan')
        with archive.extractfile(info) as stream:
            plan = json.load(stream)
        if plan.get('schema') != 1:
            raise ValueError('Unknown origin plan schema')
        objects = plan['objects']
        expected = {'origin-plan.json'}
        hashes = set()
        for item in objects:
            digest = item['sha256']
            if not valid_hash(digest) or digest in hashes or not isinstance(item['size'], int) or item['size'] < 0:
                raise ValueError('Invalid origin object')
            hashes.add(digest)
            if item['fromTar']:
                name = 'objects/sha256/' + digest
                if name not in members or members[name].size != item['size']:
                    raise ValueError('Staging archive object is missing or has the wrong size')
                expected.add(name)
        if set(members) != expected or len(objects) != plan['totalObjects'] or sum(item['size'] for item in objects) != plan['totalBytes']:
            raise ValueError('Staging archive inventory differs from its plan')
        for item in objects:
            digest = item['sha256'];size = item['size']
            target = safe_path(root, 'objects/sha256/' + digest)
            target.parent.mkdir(parents=True, exist_ok=True)
            if target.exists():
                if target.stat().st_size != size or hash_file(target) != digest:
                    raise ValueError('An existing immutable origin object differs: ' + digest)
                report['existing'] += 1
            else:
                temporary = target.with_name(digest + '.stage-' + run)
                try:
                    if item['fromTar']:
                        with archive.extractfile(members['objects/sha256/' + digest]) as source, temporary.open('xb') as output:
                            shutil.copyfileobj(source, output, 1024 * 1024)
                        report['uploadedObjects'] += 1
                    else:
                        source = None
                        for candidate in item['remotePaths']:
                            path = pathlib.Path(candidate)
                            if not path.is_absolute() or not path.is_relative_to(root):
                                raise ValueError('Reuse candidate is outside the existing public root')
                            path = safe_path(root, path.relative_to(root).as_posix())
                            if path.is_file() and path.stat().st_size == size and hash_file(path) == digest:
                                source = path
                                break
                        if source is None:
                            raise ValueError('No validated retained origin source remains: ' + digest)
                        try:
                            os.link(source, temporary)
                            report['linked'] += 1
                        except OSError:
                            shutil.copyfile(source, temporary)
                            report['copied'] += 1
                    if temporary.stat().st_size != size or hash_file(temporary) != digest:
                        raise ValueError('Prepared origin object failed SHA256 verification: ' + digest)
                    temporary.chmod(0o644)
                    # Publish without ever overwriting an existing immutable name.
                    try:
                        os.link(temporary, target)
                    except FileExistsError:
                        if target.stat().st_size != size or hash_file(target) != digest:
                            raise ValueError('Concurrent origin object differs: ' + digest)
                finally:
                    if temporary.exists():
                        temporary.unlink()
            report['objects'] += 1;report['bytes'] += size
    report['complete'] = True
    report_path = archive_path.with_suffix(archive_path.suffix + '.import.json')
    report_path.write_text(json.dumps(report, indent=2)+'\n', encoding='utf-8')
    print(json.dumps(report))

parser = argparse.ArgumentParser(description=__doc__)
commands = parser.add_subparsers(dest='command', required=True)
make = commands.add_parser('prepare');make.add_argument('inventory', type=pathlib.Path);make.add_argument('output', type=pathlib.Path)
apply = commands.add_parser('import');apply.add_argument('archive', type=pathlib.Path);apply.add_argument('--public-root', type=pathlib.Path, default=pathlib.Path('/vol1/mc-client-hub/public'))
args = parser.parse_args()
if args.command == 'prepare':
    prepare(args.inventory, args.output)
else:
    import_archive(args.archive, args.public_root)
