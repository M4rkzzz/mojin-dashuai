"""Prepare private join keys, then activate only the Hub API at the authorized time.

Run on the NAS as root. Does not restart gsmanager or any Minecraft process.
Never prints environment contents, service keys, or database credentials.
"""
import argparse
import datetime as dt
import fcntl
import json
import os
import pathlib
import re
import secrets
import shutil
import subprocess
import sys
import time
import urllib.request

ROOT = pathlib.Path('/var/apps/mc-client-hub')
STAGE = ROOT / 'staging/join-auth-1.0.0'
KEY_FILE = STAGE / 'server-keys.json'
STATE_FILE = STAGE / 'state.json'
EDGE = 'mc-client-hub_edge'
BACKUP_ROOT = pathlib.Path('/vol1/mc-client-hub/backups')
API_VERSION = '1.1.0'
NOT_BEFORE = dt.datetime(2026, 9, 6, 7, 30, tzinfo=dt.timezone.utc)
INSTANCES = ('m3e', 'dc2', 'mb', 'vw')


def utcnow():
    return dt.datetime.now(dt.timezone.utc)


def run(*args):
    return subprocess.run(args, cwd=ROOT, check=True, capture_output=True, text=True).stdout


def private_directory(path):
    if path.is_symlink() or not path.resolve().is_relative_to(ROOT.resolve()):
        raise RuntimeError('Unexpected private directory target')
    path.mkdir(parents=True, exist_ok=True, mode=0o700)
    path.chmod(0o700)


def write_private(path, data):
    if path.is_symlink() or not path.resolve().is_relative_to(ROOT.resolve()):
        raise RuntimeError('Unexpected private file target')
    temporary = path.with_name(path.name + '.tmp-' + secrets.token_hex(4))
    with temporary.open('x', encoding='utf-8') as stream:
        os.chmod(temporary, 0o600)
        stream.write(data)
        stream.flush()
        os.fsync(stream.fileno())
    temporary.replace(path)
    path.chmod(0o600)


def write_json(path, value):
    write_private(path, json.dumps(value, ensure_ascii=False, indent=2) + '\n')


def inspect(container):
    return json.loads(run('docker', 'inspect', container))[0]


def key_material():
    if KEY_FILE.exists():
        keys = json.loads(KEY_FILE.read_text())
        if set(keys) != set(INSTANCES) or any(not isinstance(v, str) or not re.fullmatch(r'[a-f0-9]{64}', v) for v in keys.values()) or len(set(keys.values())) != 4:
            raise RuntimeError('Existing staged key material is invalid; refusing to replace it')
        KEY_FILE.chmod(0o600)
        return keys
    keys = {instance: secrets.token_hex(32) for instance in INSTANCES}
    write_json(KEY_FILE, keys)
    return keys


def prepare():
    keys = key_material()
    if not STATE_FILE.exists():
        write_json(STATE_FILE, {'state': 'prepared', 'createdAt': utcnow().isoformat(), 'apiVersion': API_VERSION, 'notBefore': NOT_BEFORE.isoformat(), 'attempts': []})
    state = json.loads(STATE_FILE.read_text())
    if state.get('state') == 'prepared' and state.get('notBefore') != NOT_BEFORE.isoformat():
        state['notBefore'] = NOT_BEFORE.isoformat()
        state['scheduleUpdatedAt'] = utcnow().isoformat()
        write_json(STATE_FILE, state)
    return keys, state


def patch_environment(original, updates):
    lines, handled = [], set()
    for line in original.splitlines():
        key = line.partition('=')[0].strip()
        if key in updates:
            if key not in handled:
                lines.append(key + '=' + updates[key])
                handled.add(key)
        else:
            lines.append(line)
    lines.extend(key + '=' + value for key, value in updates.items() if key not in handled)
    return '\n'.join(lines) + '\n'


def healthy():
    try:
        with urllib.request.urlopen('http://127.0.0.1:18081/health', timeout=3) as response:
            return response.status == 200
    except Exception:
        return False


def wait_health():
    for _ in range(30):
        if healthy():
            return True
        time.sleep(.5)
    return False


def snapshot_file(source, target):
    if source.is_symlink() or not source.is_file():
        raise RuntimeError('Missing or unexpected deployment file: ' + source.name)
    with target.open('xb') as output:
        os.chmod(target, 0o600)
        output.write(source.read_bytes())


def restore_file(source, target, in_place=False):
    original_stat = target.stat()
    if in_place:
        # Nginx uses a single-file bind mount: retain the host inode.
        target.write_bytes(source.read_bytes())
    else:
        write_private(target, source.read_text())
    os.chown(target, original_stat.st_uid, original_stat.st_gid)


def activate(keys, state):
    if utcnow() < NOT_BEFORE:
        raise RuntimeError('Activation is authorized only from 2026-09-06 15:30 Asia/Shanghai')
    if state['state'] in ('activating', 'failed-needs-review'):
        raise RuntimeError('A previous activation was interrupted. Inspect the recorded backup before retrying.')
    if state['state'] == 'active':
        current = inspect('gsmanager')['NetworkSettings']['Networks'].get(EDGE, {})
        if current.get('IPAddress') != state.get('internalIp') or not healthy():
            raise RuntimeError('Previously activated state no longer matches; refusing an implicit second cutover')
        return {k: state[k] for k in ('state', 'apiVersion', 'internalIp', 'activatedAt', 'firstBackup')}

    release = ROOT / ('releases/api-' + API_VERSION)
    if not (release / 'api/Hub.Api.dll').is_file():
        raise RuntimeError('Prepared API release missing')
    if not (ROOT / 'upgrade-api.py').is_file():
        raise RuntimeError('Scoped API upgrade helper missing')
    run('docker', 'image', 'inspect', 'boshan/hub-api:' + API_VERSION)
    run('docker', 'network', 'inspect', EDGE)
    original_game = inspect('gsmanager')
    if not original_game['State']['Running']:
        raise RuntimeError('gsmanager must be running; activation will not start or restart it')
    env_file, compose_file = ROOT / 'secrets/api.env', ROOT / 'compose.yml'
    nginx_id = run('docker', 'compose', 'ps', '-q', 'downloads').strip()
    if not nginx_id:
        raise RuntimeError('Existing download gateway is unavailable')
    mounts = inspect(nginx_id)['Mounts']
    nginx_sources = [pathlib.Path(m['Source']) for m in mounts if m['Destination'] == '/etc/nginx/conf.d/default.conf']
    if len(nginx_sources) != 1 or nginx_sources[0] not in (ROOT / 'direct-tls.nginx.conf', ROOT / 'nginx.conf'):
        raise RuntimeError('Unexpected download gateway configuration mount')
    nginx_file = nginx_sources[0]
    if (ROOT / 'api').is_symlink() or not (ROOT / 'api').is_dir():
        raise RuntimeError('Unexpected existing API directory')

    stamp = utcnow().strftime('%Y%m%dT%H%M%SZ') + '-' + secrets.token_hex(3)
    attempt = STAGE / 'backups' / stamp
    private_directory(attempt)
    snapshot_file(env_file, attempt / 'api.env')
    snapshot_file(compose_file, attempt / 'compose.yml')
    snapshot_file(nginx_file, attempt / 'nginx.conf')
    shutil.copytree(ROOT / 'api', attempt / 'api')
    original_environment = env_file.read_text()
    original_nginx = nginx_file.read_text()
    original_network = original_game['NetworkSettings']['Networks'].get(EDGE)
    write_json(attempt / 'original-state.json', {'gsmanagerId': original_game['Id'], 'gsmanagerStartedAt': original_game['State']['StartedAt'], 'edgeWasConnected': original_network is not None, 'edgeIp': original_network.get('IPAddress') if original_network else None, 'nginxConfig': str(nginx_file)})
    state.setdefault('firstBackup', str(attempt))
    state.setdefault('attempts', []).append({'id': stamp, 'backup': str(attempt), 'startedAt': utcnow().isoformat()})
    state.update({'state': 'activating', 'currentBackup': str(attempt)})
    write_json(STATE_FILE, state)

    network_added = nginx_changed = env_changed = api_attempted = False
    phase = 'backup'
    try:
        run('sh', 'backup.sh')
        dumps = sorted(BACKUP_ROOT.glob('hub-*.dump'))
        if not dumps or dumps[-1].stat().st_size == 0:
            raise RuntimeError('Database backup missing')
        shutil.copy2(dumps[-1], attempt / dumps[-1].name)
        phase = 'internal-network'
        if original_network is None:
            run('docker', 'network', 'connect', EDGE, 'gsmanager')
            network_added = True
        internal_ip = inspect('gsmanager')['NetworkSettings']['Networks'][EDGE]['IPAddress']
        import ipaddress
        if ipaddress.ip_address(internal_ip).version != 4:
            raise RuntimeError('Expected IPv4 for explicit internal /32 allowlist')
        phase = 'private-environment'
        updates = {'JoinAuth__Enabled': 'true', 'JoinAuth__InternalNetworks': internal_ip + '/32'}
        updates.update({'JoinAuth__ServerKeys__' + k: v for k, v in keys.items()})
        previous_stat = env_file.stat()
        write_private(env_file, patch_environment(original_environment, updates))
        os.chown(env_file, previous_stat.st_uid, previous_stat.st_gid)
        env_changed = True

        phase = 'public-internal-deny'
        if 'location ^~ /internal/' not in original_nginx:
            marker = re.search(r'(?m)^([ \t]*)location\s', original_nginx)
            if not marker:
                raise RuntimeError('No gateway location marker found')
            rules = marker.group(1) + 'location = /internal { return 404; }\n' + marker.group(1) + 'location ^~ /internal/ { return 404; }\n'
            nginx_file.write_text(original_nginx[:marker.start()] + rules + original_nginx[marker.start():])
            nginx_changed = True
            run('docker', 'exec', nginx_id, 'nginx', '-t')
            run('docker', 'exec', nginx_id, 'nginx', '-s', 'reload')

        phase = 'upgrade-api'
        api_attempted = True
        # Reuse the established scoped deployment and its own backup/health/rollback checks.
        upgrade_report = run(sys.executable, str(ROOT / 'upgrade-api.py'), API_VERSION)
        write_private(attempt / 'upgrade-result.json', upgrade_report)
        phase = 'verify'
        if not wait_health():
            raise RuntimeError('Updated API health check failed')
        after_game = inspect('gsmanager')
        if after_game['Id'] != original_game['Id'] or after_game['State']['StartedAt'] != original_game['State']['StartedAt']:
            raise RuntimeError('gsmanager process changed during API activation')
        api_id = run('docker', 'compose', 'ps', '-q', 'hub-api').strip()
        api_config = inspect(api_id)['Config']
        if api_config['Image'] != 'boshan/hub-api:' + API_VERSION:
            raise RuntimeError('Unexpected running API image')
        running_environment = dict(entry.split('=', 1) for entry in api_config.get('Env', []) if '=' in entry)
        if any(running_environment.get(k) != v for k, v in updates.items()):
            raise RuntimeError('Running API authorization settings differ from staged settings')
        state.update({'state': 'active', 'activatedAt': utcnow().isoformat(), 'apiVersion': API_VERSION, 'internalIp': internal_ip, 'healthy': True, 'gsmanagerRestarted': False, 'publicInternalRouteBlocked': True})
        state['attempts'][-1]['result'] = 'active'
        write_json(STATE_FILE, state)
        return {k: state[k] for k in ('state', 'apiVersion', 'internalIp', 'healthy', 'activatedAt', 'firstBackup', 'gsmanagerRestarted', 'publicInternalRouteBlocked')}
    except Exception as error:
        rollback_errors = []

        def rollback(label, action):
            try:
                action()
            except Exception:
                rollback_errors.append(label)

        if env_changed:
            rollback('restore-environment', lambda: restore_file(attempt / 'api.env', env_file))
        rollback('restore-compose', lambda: restore_file(attempt / 'compose.yml', compose_file))
        if api_attempted:
            def restore_api():
                if (ROOT / 'api').exists():
                    (ROOT / 'api').rename(attempt / 'failed-api')
                shutil.copytree(attempt / 'api', ROOT / 'api')
                run('docker', 'compose', 'up', '-d', '--no-deps', '--no-build', 'hub-api')
                if not wait_health():
                    raise RuntimeError('Previous API unhealthy after rollback')
            rollback('restore-api', restore_api)
        if nginx_changed:
            def restore_nginx():
                restore_file(attempt / 'nginx.conf', nginx_file, in_place=True)
                run('docker', 'exec', nginx_id, 'nginx', '-t')
                run('docker', 'exec', nginx_id, 'nginx', '-s', 'reload')
            rollback('restore-nginx', restore_nginx)
        if network_added:
            rollback('disconnect-new-internal-network', lambda: run('docker', 'network', 'disconnect', EDGE, 'gsmanager'))
        state.update({'state': 'failed-restored' if not rollback_errors else 'failed-needs-review', 'failedAt': utcnow().isoformat(), 'failedPhase': phase, 'errorCategory': type(error).__name__, 'rollbackErrors': rollback_errors})
        state['attempts'][-1]['result'] = state['state']
        write_json(STATE_FILE, state)
        # Deliberately do not print captured subprocess output or environment values.
        return {'state': state['state'], 'failedPhase': phase, 'errorCategory': type(error).__name__, 'rollbackErrors': rollback_errors, 'firstBackup': state['firstBackup'], 'attemptBackup': str(attempt)}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    modes = parser.add_mutually_exclusive_group(required=True)
    modes.add_argument('--prepare', action='store_true')
    modes.add_argument('--activate', action='store_true')
    args = parser.parse_args()
    os.umask(0o077)
    private_directory(STAGE)
    with (STAGE / '.lock').open('a') as lock:
        fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
        keys, state = prepare()
        if args.prepare:
            result = {'state': state['state'], 'apiVersion': API_VERSION, 'privateKeysPath': str(KEY_FILE), 'notBefore': NOT_BEFORE.isoformat(), 'productionChanged': False}
        else:
            result = activate(keys, state)
        print(json.dumps(result, ensure_ascii=False))
        return 0 if result['state'] in ('prepared', 'active') else 1


if __name__ == '__main__':
    try:
        raise SystemExit(main())
    except (RuntimeError, ValueError, OSError, subprocess.SubprocessError) as failure:
        print(json.dumps({'state': 'refused', 'errorCategory': type(failure).__name__, 'message': str(failure) if isinstance(failure, RuntimeError) else 'Preflight or file/command operation failed; private state is retained.'}, ensure_ascii=False))
        raise SystemExit(1)

