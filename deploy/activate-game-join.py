#!/usr/bin/env python3
"""Prepare held per-server join-gate candidates on the NAS; explicitly activate after 15:30.

No operation is scheduled by this file. --prepare writes only private staging data.
--activate/--rollback stop one exact game through RCON and restart it through GSManager.
"""
from __future__ import annotations

import argparse
import contextlib
import datetime as dt
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import stat
import subprocess
import sys
import time
import zipfile

STAGING = Path('/var/apps/mc-client-hub/staging/join-auth-1.0.0')
HOST_GAMES = Path('/var/apps/docker-gsmanager/shares/gsmanager/home/steam/games')
CONTAINER_GAMES = Path('/home/steam/games')
PROC_ROOT = Path('/proc')
NOT_BEFORE = dt.datetime(2026, 9, 6, 7, 30, tzinfo=dt.timezone.utc)
DC2_NOT_BEFORE = dt.datetime(2026, 9, 6, 7, 43, 23, tzinfo=dt.timezone.utc)  # User authorized immediate dc2 restart.
REDEEM_URL = 'http://hub-api:8080/internal/v1/join/redeem'
SERVERS = {
    'm3e': dict(directory='M3E66', instance='26e0ee30-5b71-49a5-8932-41f86b407373', script='run.sh', port=25575,
                scriptHash='5b02179ad05f1c0188e141228a6345474a818b8806d91e89da2154a9567b081d',
                command='$TASKSET_CMD "$JAVA_CMD" -Xms$MIN_MEMORY'),
    'dc2': dict(directory='DeceasedCraft-2', instance='3cd98756-9d98-4448-b97c-cca1785af034', script='run.sh', port=25576,
                scriptHash='46587c5db77eb67e31c316c24a732b05e4280ca3da1d66c14cc56e44abc7ad63',
                command='exec taskset -c 0-11 /usr/lib/jvm/temurin-21-jdk-amd64/bin/java @user_jvm_args.txt'),
    'mb': dict(directory='MeatballCraft-0.18.6.4', instance='f60bfc16-9907-425a-81e3-643ce636e47f', script='ServerStart.sh', port=25577,
                scriptHash='ac4e5f116ca5d228d2a4da8ec2aecf5f7aa74af8a80acd2b020b56842a0685e1',
                command='"$javaPath" "${javaArgs[@]}" -jar "$jarName" "${gameArgs[@]}"',
                outerScript='start.sh', outerHash='d39cfb9ce479ea8aa3f2475bccf7b5230da2a6b7db9593b0b36879684be2d9ca'),
    'vw': dict(directory='VoidWayfarer-4', instance='23295b5f-b303-4fb6-939f-cc3d03f9f1dc', script='start.sh', port=25578,
                scriptHash='11a0a05150ba4cc3f2f98bf0c6fcc60b240d7a28768f59caf2e613ad0beff1f6',
                command='exec /home/.local/java8-vw4/bin/java -Xms2G'),
}


def sha(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def now() -> str:
    return dt.datetime.now(dt.timezone.utc).isoformat()


def time_guard(id: str):
    earliest = max(NOT_BEFORE, DC2_NOT_BEFORE) if id == 'dc2' else NOT_BEFORE
    if dt.datetime.now(dt.timezone.utc) < earliest:
        local = earliest.astimezone(dt.timezone(dt.timedelta(hours=8)))
        raise RuntimeError(f'{id}: operation is held until {local:%Y-%m-%d %H:%M} Asia/Shanghai')


def command(*args, input=None, timeout=60) -> str:
    result = subprocess.run(args, input=input, capture_output=True, text=True, timeout=timeout)
    if result.returncode:
        # Do not echo docker/RCON stderr: nested tools may include credentials.
        raise RuntimeError(f'{args[0]} command failed (exit {result.returncode})')
    return result.stdout


def safe(root: Path, relative: str) -> Path:
    if Path(relative).is_absolute() or '..' in Path(relative).parts:
        raise ValueError('Unsafe relative path')
    # The NAS's audited game bind uses the application-managed share link
    # /var/apps/docker-gsmanager/shares/gsmanager -> /vol1/@appshare/gsmanager.
    # Resolve the explicitly configured deployment root once as the trusted boundary;
    # only links beneath that root are untrusted and forbidden.
    boundary = root.resolve()
    path = boundary
    for component in Path(relative).parts:
        path = path / component
        candidate = path
        if candidate.is_symlink():
            raise ValueError('Deployment paths cannot contain symlinks')
    if not path.resolve().is_relative_to(boundary):
        raise ValueError('Deployment path escaped its root')
    return path


def atomic(path: Path, data: bytes, mode=0o600, owner=None):
    path.parent.mkdir(parents=True, exist_ok=True, mode=0o700)
    temporary = path.with_name(path.name + f'.tmp-{os.getpid()}-{time.time_ns()}')
    try:
        with temporary.open('xb') as stream:
            stream.write(data)
            stream.flush()
            os.fsync(stream.fileno())
        os.chmod(temporary, mode)
        if owner is not None and hasattr(os, 'chown'):
            os.chown(temporary, *owner)
        os.replace(temporary, path)
    finally:
        temporary.unlink(missing_ok=True)


def write_json(path: Path, data):
    atomic(path, (json.dumps(data, ensure_ascii=False, indent=2) + '\n').encode())


def read_json(path: Path):
    return json.loads(path.read_text())


def tree_inventory(path: Path):
    if not path.exists():
        return None
    if not path.is_dir() or path.is_symlink():
        raise RuntimeError('Existing .join-auth is not a normal directory')
    result = {}
    for file in sorted(path.rglob('*')):
        if file.is_symlink():
            raise RuntimeError('Existing .join-auth contains a symlink')
        if file.is_file():
            result[str(file.relative_to(path))] = sha(file.read_bytes())
    return result


def private_keys():
    source = safe(STAGING, 'server-keys.json')
    if os.name != 'nt' and stat.S_IMODE(source.stat().st_mode) & 0o077:
        raise RuntimeError('server-keys.json must be private (0600)')
    keys = read_json(source)
    if set(keys) != set(SERVERS):
        raise RuntimeError('Expected exactly four instance-specific server keys')
    if any(not isinstance(value, str) or not re.fullmatch(r'[A-Za-z0-9_+/=-]{32,512}', value) for value in keys.values()):
        raise RuntimeError('Server key format is invalid')
    if len(set(keys.values())) != 4:
        raise RuntimeError('Every instance requires a different server key')
    return keys


def inject(original: bytes, id: str, agent_hash: str) -> bytes:
    spec = SERVERS[id]
    text = original.decode('utf-8')
    if not text.startswith('#!') or 'mojin.join.server.config' in text:
        raise RuntimeError('Unexpected or already modified start script')
    root = CONTAINER_GAMES / spec['directory'] / '.join-auth'
    options = f'-javaagent:{root}/agent-{agent_hash}.jar -Dmojin.join.server.config={root}/server.properties '
    old = spec['command']
    if id == 'm3e':
        new = old.replace(' -Xms', ' ' + options + '-Xms', 1)
    elif id == 'dc2':
        new = old.replace(' @user_jvm_args.txt', ' ' + options + '@user_jvm_args.txt', 1)
    elif id == 'mb':
        new = old.replace(' "${javaArgs[@]}"', ' ' + options + '"${javaArgs[@]}"', 1)
    else:
        new = old.replace(' -Xms2G', ' ' + options + '-Xms2G', 1)
    # Match executable lines only, not diagnostic echo lines containing the same text.
    pattern = re.compile(r'(?m)^(?P<indent>[ \t]*)' + re.escape(old))
    text, count = pattern.subn(lambda match: match['indent'] + new, text)
    if count != 1:
        raise RuntimeError(f'{id}: exact game Java command was not found once')
    return text.encode('utf-8')


def inspect_environment():
    containers = json.loads(command('docker', 'inspect', 'gsmanager'))
    mounts = containers[0]['Mounts']
    if not any(item['Source'] == str(HOST_GAMES) and item['Destination'] == str(CONTAINER_GAMES) for item in mounts):
        raise RuntimeError('The live GSManager game bind differs from the audited path')


def prepare(agent: Path):
    inspect_environment()
    data = agent.read_bytes()
    with zipfile.ZipFile(agent) as archive:
        manifest = archive.read('META-INF/MANIFEST.MF')
        if b'Premain-Class:' not in manifest:
            raise RuntimeError('Agent JAR has no premain entrypoint')
    agent_hash = sha(data)
    keys = private_keys()
    # Audit every instance before replacing even one staged candidate. A late active
    # instance or changed production script must not leave a mixed-generation plan.
    inputs = []
    for id, spec in SERVERS.items():
        root = safe(HOST_GAMES, spec['directory'])
        work = safe(STAGING, 'servers/' + id)
        state_path = work / 'state.json'
        if state_path.exists() and read_json(state_path).get('phase') not in ('prepared', 'rolled-back'):
            raise RuntimeError(f'{id}: existing activation state needs review before preparing again')
        script = safe(root, spec['script'])
        original = script.read_bytes()
        if sha(original) != spec['scriptHash']:
            raise RuntimeError(f'{id}: live original script hash changed; do not overwrite it')
        if spec.get('outerScript') and sha(safe(root, spec['outerScript']).read_bytes()) != spec['outerHash']:
            raise RuntimeError('mb: outer start script hash changed')
        candidate = inject(original, id, agent_hash)
        inputs.append((id, spec, root, work, state_path, script, original, candidate, tree_inventory(root / '.join-auth')))
    for id, spec, root, work, state_path, script, original, candidate, old_auth in inputs:
        candidate_dir = work / 'candidate'
        atomic(candidate_dir / spec['script'], candidate, stat.S_IMODE(script.stat().st_mode))
        atomic(candidate_dir / 'agent.jar', data)
        props = f'mode=observe\ninstance={id}\nredeemUrl={REDEEM_URL}\nsecret={keys[id]}\nallowLocalContainerHttp=true\n'
        atomic(candidate_dir / 'server.properties', props.encode())
        command('bash', '-n', str(candidate_dir / spec['script']))
        earliest = max(NOT_BEFORE, DC2_NOT_BEFORE) if id == 'dc2' else NOT_BEFORE
        plan = dict(instance=id, preparedAt=now(), phase='hold', notBefore=earliest.isoformat(),
                    agentSha256=agent_hash, originalScriptSha256=sha(original), candidateScriptSha256=sha(candidate),
                    configSha256=sha(props.encode()), previousJoinAuth=old_auth,
                    script=spec['script'], untouchedOuterScript=spec.get('outerScript'),
                    requiredApi='state.json: state=active, apiVersion=1.1.0, healthy=true; verify live container and /32',
                    initialMode='observe', activationAllowed=False)
        write_json(work / 'plan.json', plan)
        write_json(state_path, dict(instance=id, phase='prepared', at=now(), agentSha256=agent_hash))
    print(json.dumps(dict(prepared=list(SERVERS), agentSha256=agent_hash, phase='hold', productionChanged=False)))


def game_processes(id: str):
    expected = str(CONTAINER_GAMES / SERVERS[id]['directory'])
    result = []
    for directory in PROC_ROOT.glob('[0-9]*'):
        try:
            if (directory / 'comm').read_text().strip() != 'java' or os.readlink(directory / 'cwd') != expected:
                continue
            args = (directory / 'cmdline').read_bytes().split(b'\0')
            if any(flag in args for flag in (b'-version', b'--version', b'-fullversion')):
                continue  # Startup scripts probe Java before launching the actual server.
            ticks = (directory / 'stat').read_text().rsplit(')', 1)[1].split()[19]
            result.append(dict(pid=int(directory.name), startedTicks=ticks))
        except (OSError, IndexError):
            continue
    if len(result) > 1:
        raise RuntimeError(f'{id}: multiple Java processes in the exact game directory')
    return result


def workdir_processes(id: str):
    expected = str(CONTAINER_GAMES / SERVERS[id]['directory'])
    result = []
    for directory in PROC_ROOT.glob('[0-9]*'):
        try:
            if os.readlink(directory / 'cwd') == expected:
                args = [part for part in (directory / 'cmdline').read_bytes().split(b'\0') if part]
                comm = (directory / 'comm').read_text().strip()
                if comm in ('bash', 'sh', 'zsh') and len(args) == 1 and args[0].rsplit(b'/', 1)[-1].lstrip(b'-') in (b'bash', b'sh', b'zsh'):
                    continue  # Existing idle interactive terminals do not launch a game by themselves.
                result.append(int(directory.name))
        except OSError:
            pass
    return result


def gsm(id: str, action: str):
    output = command('docker', 'exec', '-w', '/root/server', 'gsmanager', 'node', 'gsmanager-api.mjs', action, SERVERS[id]['instance'])
    if action == 'get':
        # Some historical panel descriptions contain unescaped control characters;
        # read the two bounded fields without parsing or logging arbitrary description data.
        fields = {key: re.search(r'"' + key + r'"\s*:\s*"([^"\r\n]*)"', output) for key in ('status', 'workingDirectory')}
        if not all(fields.values()) or fields['workingDirectory'][1] != str(CONTAINER_GAMES / SERVERS[id]['directory']):
            raise RuntimeError(f'{id}: panel instance working directory does not match')
        return fields['status'][1]


def rcon(id: str, stop=False):
    code = '''import asyncio,bridge,json,sys
async def main():
 c=next(c for c in bridge.load_server_configs() if c.rcon_port==int(sys.argv[1]))
 client=bridge.RconClient(c.rcon_host,c.rcon_port,bridge.load_rcon_password(c.password_file,c.rcon_password))
 await asyncio.wait_for(client.command("list"),timeout=20)
 if sys.argv[2]=="stop":
  await asyncio.wait_for(client.command("save-all"),timeout=45)
  try: await asyncio.wait_for(client.command("stop"),timeout=15)
  except Exception: pass
 print(json.dumps({"rconAvailable":True,"stopRequested":sys.argv[2]=="stop"}))
asyncio.run(main())
'''
    command('docker', 'exec', '-i', 'mcqq-bridge', 'python', '-', str(SERVERS[id]['port']), 'stop' if stop else 'probe', input=code, timeout=90)


def wait_for(predicate, seconds: int, failure: str):
    deadline = time.monotonic() + seconds
    while time.monotonic() < deadline:
        value = predicate()
        if value:
            return value
        time.sleep(1)
    raise RuntimeError(failure)


def stop_game(id: str):
    old = game_processes(id)
    if old:
        rcon(id, stop=True)
        wait_for(lambda: not game_processes(id), 300, f'{id}: Java did not exit; refusing any kill or duplicate start')
    # Close the idle PTY only after the exact Java has exited normally.
    if gsm(id, 'get') not in ('stopped', 'error'):
        gsm(id, 'close-terminal')
    wait_for(lambda: gsm(id, 'get') in ('stopped', 'error') and not workdir_processes(id), 30,
             f'{id}: GSManager has not reached a startable state')
    return old


def api_ready(plan):
    # This is written only by activate-join-api.py after its live image, environment,
    # exact /32 allowlist and health checks succeed. There is no invented ready flag.
    ready = read_json(safe(STAGING, 'state.json'))
    if ready.get('state') != 'active' or ready.get('apiVersion') != '1.1.0' or ready.get('healthy') is not True or ready.get('publicInternalRouteBlocked') is not True:
        raise RuntimeError('The real API activation state is not ready')
    game = json.loads(command('docker', 'inspect', 'gsmanager'))[0]
    ip = game['NetworkSettings']['Networks'].get('mc-client-hub_edge', {}).get('IPAddress')
    if not ip or ip != ready.get('internalIp'):
        raise RuntimeError('Game container network changed since API allowlist activation')
    api = json.loads(command('docker', 'inspect', 'mc-client-hub-hub-api-1'))[0]
    environment = dict(item.split('=', 1) for item in api['Config'].get('Env', []) if '=' in item)
    keys = private_keys()
    if (api['Config']['Image'] != 'boshan/hub-api:1.1.0' or not api['State']['Running']
            or environment.get('JoinAuth__Enabled') != 'true'
            or environment.get('JoinAuth__InternalNetworks') != ip + '/32'
            or any(environment.get('JoinAuth__ServerKeys__' + id) != key for id, key in keys.items())):
        raise RuntimeError('Live API image or private authorization settings differ from the activated state')
    config = (STAGING / 'servers' / plan['instance'] / 'candidate/server.properties').read_text()
    if 'secret=' + keys[plan['instance']] + '\n' not in config:
        raise RuntimeError('Staged game key differs from the activated API key')
    # Probe from the actual game container. No automatic network mutation or fallback.
    code = "const r=await fetch('http://hub-api:8080/health',{signal:AbortSignal.timeout(5000)});if(!r.ok)process.exit(2);"
    command('docker', 'exec', 'gsmanager', 'node', '--input-type=module', '-e', code, timeout=10)


def start_game(id: str, agent_hash: str | None):
    if game_processes(id):
        raise RuntimeError(f'{id}: refusing to start while Java is already running')
    gsm(id, 'start')
    process = wait_for(lambda: game_processes(id), 120, f'{id}: new Java did not appear')[0]
    if agent_hash:
        args = (PROC_ROOT / str(process['pid']) / 'cmdline').read_bytes().split(b'\0')
        expected = f'-javaagent:{CONTAINER_GAMES / SERVERS[id]["directory"]}/.join-auth/agent-{agent_hash}.jar'.encode()
        if expected not in args:
            raise RuntimeError(f'{id}: started Java did not receive the pinned authentication agent')
    def ready():
        if game_processes(id) != [process]:
            raise RuntimeError(f'{id}: Java exited or restarted during startup')
        try:
            rcon(id)
            return True
        except (RuntimeError, subprocess.TimeoutExpired):
            return False
    wait_for(ready, 600, f'{id}: new server did not become RCON-ready')
    return process


def validate_candidate(id: str):
    work = safe(STAGING, 'servers/' + id)
    plan = read_json(work / 'plan.json')
    spec = SERVERS[id]
    for name, expected in [('agent.jar', plan['agentSha256']), ('server.properties', plan['configSha256']), (spec['script'], plan['candidateScriptSha256'])]:
        if sha((work / 'candidate' / name).read_bytes()) != expected:
            raise RuntimeError(f'{id}: staged candidate bytes changed')
    if plan['instance'] != id or plan['originalScriptSha256'] != spec['scriptHash']:
        raise RuntimeError('Prepared plan differs from the audited live-script baseline')
    return work, plan


def validate_live_generation(id: str, work: Path, plan):
    state = read_json(work / 'state.json')
    root = safe(HOST_GAMES, SERVERS[id]['directory'])
    actual_script = sha(safe(root, SERVERS[id]['script']).read_bytes())
    if state['phase'] == 'active':
        if (actual_script != plan['candidateScriptSha256'] or game_processes(id) != [state.get('process')]
                or sha((root / '.join-auth/server.properties').read_bytes()) != state.get('runtimeConfigSha256', plan['configSha256'])
                or sha((root / '.join-auth' / ('agent-' + plan['agentSha256'] + '.jar')).read_bytes()) != plan['agentSha256']):
            raise RuntimeError(f'{id}: current active generation drifted')
    elif state['phase'] in ('maintenance', 'rolled-back'):
        if actual_script != plan['originalScriptSha256'] or tree_inventory(root / '.join-auth') != plan['previousJoinAuth']:
            raise RuntimeError(f'{id}: restored production baseline drifted')
        if state['phase'] == 'maintenance' and (game_processes(id) or gsm(id, 'get') not in ('stopped', 'error')):
            raise RuntimeError(f'{id}: maintenance instance is not stopped')
        if state['phase'] == 'rolled-back' and game_processes(id) != [state.get('process')]:
            raise RuntimeError(f'{id}: restored Java process changed')
    else:
        raise RuntimeError(f'{id}: cannot prepare a replacement during an interrupted operation')
    if SERVERS[id].get('outerScript') and sha(safe(root, SERVERS[id]['outerScript']).read_bytes()) != SERVERS[id]['outerHash']:
        raise RuntimeError(f'{id}: outer start script drifted')
    return state


def prepare_server(id: str, agent: Path):
    """Stage one replacement without touching the active candidate, backup or game."""
    if id not in SERVERS:
        raise RuntimeError('Unknown server instance')
    inspect_environment()
    work, old_plan = validate_candidate(id)
    state = validate_live_generation(id, work, old_plan)
    original = (work / 'backup' / SERVERS[id]['script']).read_bytes()
    if sha(original) != old_plan['originalScriptSha256']:
        raise RuntimeError('First deployment backup is missing or changed')
    data = agent.read_bytes()
    with zipfile.ZipFile(agent) as archive:
        if b'Premain-Class:' not in archive.read('META-INF/MANIFEST.MF'):
            raise RuntimeError('Replacement JAR has no premain entrypoint')
    agent_hash = sha(data)
    candidate = inject(original, id, agent_hash)
    keys = private_keys()
    props = f'mode=observe\ninstance={id}\nredeemUrl={REDEEM_URL}\nsecret={keys[id]}\nallowLocalContainerHttp=true\n'.encode()
    pending = work / 'pending'
    atomic(pending / 'agent.jar', data)
    atomic(pending / SERVERS[id]['script'], candidate)
    atomic(pending / 'server.properties', props)
    command('bash', '-n', str(pending / SERVERS[id]['script']))
    plan = dict(old_plan, phase='hold', preparedAt=now(), agentSha256=agent_hash,
                candidateScriptSha256=sha(candidate), configSha256=sha(props), initialMode='observe',
                sourcePlanSha256=sha((work / 'plan.json').read_bytes()), sourcePhase=state['phase'],
                sourceProcess=state.get('process'), replacementOf=old_plan['agentSha256'],
                previousActiveScriptSha256=old_plan['candidateScriptSha256'])
    write_json(work / 'pending-plan.json', plan)
    print(json.dumps(dict(instance=id, phase='replacement-held', agentSha256=agent_hash, productionChanged=False, firstBackupPreserved=True)))


def activate_server(id: str):
    """Apply only an explicitly prepared server generation; retain the first backup."""
    time_guard(id)
    inspect_environment()
    work, old_plan = validate_candidate(id)
    state = validate_live_generation(id, work, old_plan)
    plan = read_json(work / 'pending-plan.json')
    if (plan.get('instance') != id or plan.get('sourcePlanSha256') != sha((work / 'plan.json').read_bytes())
            or plan.get('sourcePhase') != state['phase'] or plan.get('sourceProcess') != state.get('process')
            or plan['originalScriptSha256'] != old_plan['originalScriptSha256']):
        raise RuntimeError('Replacement no longer matches the generation that was reviewed')
    pending = work / 'pending'
    for name, expected in [('agent.jar', plan['agentSha256']), ('server.properties', plan['configSha256']), (SERVERS[id]['script'], plan['candidateScriptSha256'])]:
        if sha((pending / name).read_bytes()) != expected:
            raise RuntimeError('Held replacement bytes changed')
    keys = private_keys()
    if 'secret=' + keys[id] + '\n' not in (pending / 'server.properties').read_text():
        raise RuntimeError('Replacement key differs from activated API key')
    api_ready(old_plan)
    root = safe(HOST_GAMES, SERVERS[id]['directory'])
    meta = read_json(work / 'backup/metadata.json')
    if sha((work / 'backup' / SERVERS[id]['script']).read_bytes()) != plan['originalScriptSha256']:
        raise RuntimeError('First backup changed before replacement')
    other = {x: game_processes(x) for x in SERVERS if x != id}
    if state['phase'] != 'maintenance':
        rcon(id)
    write_json(work / 'state.json', dict(instance=id, phase='stopping-server-update', at=now(), agentSha256=old_plan['agentSha256']))
    stop_game(id)
    history = work / 'generations' / (old_plan['agentSha256'] + '-' + str(time.time_ns()))
    history.mkdir(parents=True, mode=0o700)
    shutil.copytree(work / 'candidate', history / 'candidate')
    shutil.copy2(work / 'plan.json', history / 'plan.json')
    write_json(history / 'state-before-update.json', state)
    for name in ('agent.jar', 'server.properties', SERVERS[id]['script']):
        atomic(work / 'candidate' / name, (pending / name).read_bytes())
    write_json(work / 'plan.json', plan)
    owner = (meta['gameUid'], meta['gameGid'])
    auth = safe(root, '.join-auth')
    auth.mkdir(exist_ok=True, mode=0o700)
    os.chmod(auth, 0o700)
    os.chown(auth, *owner)
    atomic(auth / ('agent-' + plan['agentSha256'] + '.jar'), (pending / 'agent.jar').read_bytes(), 0o644, owner)
    atomic(auth / 'server.properties', (pending / 'server.properties').read_bytes(), 0o600, owner)
    atomic(safe(root, SERVERS[id]['script']), (pending / SERVERS[id]['script']).read_bytes(), meta['scriptMode'], (meta['scriptUid'], meta['scriptGid']))
    write_json(work / 'state.json', dict(instance=id, phase='starting', at=now(), agentSha256=plan['agentSha256']))
    process = start_game(id, plan['agentSha256'])
    if other != {x: game_processes(x) for x in other}:
        raise RuntimeError('Another game process changed during replacement')
    write_json(work / 'state.json', dict(instance=id, phase='active', at=now(), agentSha256=plan['agentSha256'],
                                      process=process, mode='observe', runtimeConfigSha256=plan['configSha256']))
    print(json.dumps(dict(instance=id, phase='active', mode='observe', agentSha256=plan['agentSha256'],
                         process=process, otherGamesUnchanged=True, firstBackupPreserved=True)))


def activate(id: str):
    time_guard(id)
    inspect_environment()
    work, plan = validate_candidate(id)
    root = safe(HOST_GAMES, SERVERS[id]['directory'])
    script = safe(root, SERVERS[id]['script'])
    state = read_json(work / 'state.json')
    if state['phase'] == 'active':
        auth = safe(root, '.join-auth')
        if (sha(script.read_bytes()) != plan['candidateScriptSha256']
                or game_processes(id) != [state.get('process')]
                or sha((auth / 'server.properties').read_bytes()) != state.get('runtimeConfigSha256', plan['configSha256'])
                or sha((auth / ('agent-' + plan['agentSha256'] + '.jar')).read_bytes()) != plan['agentSha256']):
            raise RuntimeError(f'{id}: active state drifted; inspect before retry')
        api_ready(plan)
        print(json.dumps(dict(instance=id, phase='active', unchanged=True)))
        return
    if state['phase'] != 'prepared':
        raise RuntimeError(f'{id}: interrupted deployment; use explicit --rollback before retry')
    if sha(script.read_bytes()) != plan['originalScriptSha256'] or tree_inventory(root / '.join-auth') != plan['previousJoinAuth']:
        raise RuntimeError(f'{id}: production files changed since preparation')
    if SERVERS[id].get('outerScript') and sha(safe(root, SERVERS[id]['outerScript']).read_bytes()) != SERVERS[id]['outerHash']:
        raise RuntimeError(f'{id}: outer script changed since preparation')
    api_ready(plan)
    others = {other: game_processes(other) for other in SERVERS if other != id}
    rcon(id)  # Verify clean-stop control before changing production files.
    before = game_processes(id)
    if len(before) != 1:
        raise RuntimeError('Expected one running game before activation')
    process_fields = dict(line.split(':', 1) for line in (PROC_ROOT / str(before[0]['pid']) / 'status').read_text().splitlines() if ':' in line)
    game_owner = (int(process_fields['Uid'].split()[1]), int(process_fields['Gid'].split()[1]))
    backup = work / 'backup'
    metadata = script.stat()
    if backup.exists():
        if sha((backup / SERVERS[id]['script']).read_bytes()) != plan['originalScriptSha256'] or tree_inventory(backup / '.join-auth') != plan['previousJoinAuth']:
            raise RuntimeError('Existing rollback backup differs from this prepared baseline')
    else:
        backup.mkdir(mode=0o700)
        shutil.copy2(script, backup / SERVERS[id]['script'])
        if (root / '.join-auth').exists():
            shutil.copytree(root / '.join-auth', backup / '.join-auth')
        write_json(backup / 'metadata.json', dict(scriptUid=metadata.st_uid, scriptGid=metadata.st_gid, scriptMode=stat.S_IMODE(metadata.st_mode), gameUid=game_owner[0], gameGid=game_owner[1]))
    write_json(work / 'state.json', dict(instance=id, phase='stopping', at=now(), agentSha256=plan['agentSha256']))
    stop_game(id)
    auth = safe(root, '.join-auth')
    auth.mkdir(exist_ok=True, mode=0o700)
    os.chmod(auth, 0o700)
    os.chown(auth, *game_owner)
    atomic(auth / ('agent-' + plan['agentSha256'] + '.jar'), (work / 'candidate/agent.jar').read_bytes(), 0o644, game_owner)
    atomic(auth / 'server.properties', (work / 'candidate/server.properties').read_bytes(), 0o600, game_owner)
    atomic(script, (work / 'candidate' / SERVERS[id]['script']).read_bytes(), stat.S_IMODE(metadata.st_mode), (metadata.st_uid, metadata.st_gid))
    write_json(work / 'state.json', dict(instance=id, phase='starting', at=now(), agentSha256=plan['agentSha256']))
    process = start_game(id, plan['agentSha256'])
    if others != {other: game_processes(other) for other in others}:
        raise RuntimeError('Another game process changed concurrently; inspect before continuing rollout')
    write_json(work / 'state.json', dict(instance=id, phase='active', at=now(), agentSha256=plan['agentSha256'], process=process,
                                      mode='observe', runtimeConfigSha256=plan['configSha256']))
    print(json.dumps(dict(instance=id, phase='active', mode='observe', process=process, otherGamesUnchanged=True)))


def set_mode(id: str, mode: str):
    if id not in SERVERS or mode not in ('off', 'observe', 'enforce'):
        raise RuntimeError('Expected --mode INSTANCE off|observe|enforce')
    time_guard(id)
    inspect_environment()
    work, plan = validate_candidate(id)
    state = read_json(work / 'state.json')
    if state.get('phase') != 'active' or game_processes(id) != [state.get('process')]:
        raise RuntimeError('Expected the exact activated Java process before changing mode')
    root = safe(HOST_GAMES, SERVERS[id]['directory'])
    config = safe(root, '.join-auth/server.properties')
    original = config.read_bytes()
    if (sha(original) != state.get('runtimeConfigSha256', plan['configSha256'])
            or sha(safe(root, SERVERS[id]['script']).read_bytes()) != plan['candidateScriptSha256']
            or sha(safe(root, '.join-auth/agent-' + plan['agentSha256'] + '.jar').read_bytes()) != plan['agentSha256']):
        raise RuntimeError('Active script, agent or configuration drifted before mode change')
    args = (PROC_ROOT / str(state['process']['pid']) / 'cmdline').read_bytes().split(b'\0')
    expected = f'-javaagent:{CONTAINER_GAMES / SERVERS[id]["directory"]}/.join-auth/agent-{plan["agentSha256"]}.jar'.encode()
    if expected not in args:
        raise RuntimeError('Live Java does not carry the activated agent')
    api_ready(plan)
    updated, count = re.subn(rb'(?m)^mode=(?:off|observe|enforce)(?=\r?$)', b'mode=' + mode.encode('ascii'), original)
    if count != 1:
        raise RuntimeError('Active configuration must contain one recognized mode property')
    if updated != original:
        meta = config.stat()
        atomic(config, updated, 0o600, (meta.st_uid, meta.st_gid))
        # The Java reload key is lastModified in milliseconds; guarantee it changes
        # even for two rapid mode operations within one filesystem timestamp tick.
        stamp = max(time.time_ns(), meta.st_mtime_ns + 2_000_000)
        os.utime(config, ns=(stamp, stamp))
    state.update(mode=mode, modeChangedAt=now(), runtimeConfigSha256=sha(updated))
    write_json(work / 'state.json', state)
    print(json.dumps(dict(instance=id, phase='active', mode=mode, restarted=False, process=state['process'])))


def rollback(id: str):
    if id == 'dc2':
        time_guard(id)  # Follow the latest separately authorized dc2 restart time.
    inspect_environment()
    work, plan = validate_candidate(id)
    state = read_json(work / 'state.json')
    if state['phase'] == 'rolled-back':
        print(json.dumps(dict(instance=id, phase='rolled-back', unchanged=True)))
        return
    if state['phase'] not in ('active', 'starting', 'stopping', 'rolling-back', 'stopping-server-update'):
        raise RuntimeError('There is no attempted activation to roll back')
    spec = SERVERS[id]
    root = safe(HOST_GAMES, spec['directory'])
    script = safe(root, spec['script'])
    backup = work / 'backup'
    if sha((backup / spec['script']).read_bytes()) != plan['originalScriptSha256']:
        raise RuntimeError('Rollback script backup is missing or invalid')
    if sha(script.read_bytes()) not in (plan['originalScriptSha256'], plan['candidateScriptSha256'], plan.get('previousActiveScriptSha256')):
        raise RuntimeError('Live script was changed independently; refusing to overwrite it')
    write_json(work / 'state.json', dict(instance=id, phase='rolling-back', at=now()))
    stop_game(id)
    meta = read_json(backup / 'metadata.json')
    atomic(script, (backup / spec['script']).read_bytes(), meta['scriptMode'], (meta['scriptUid'], meta['scriptGid']))
    auth = safe(root, '.join-auth')
    if auth.exists():
        # Preserve the failed deployment for inspection instead of deleting its files.
        failed = safe(root, '.join-auth-failed-' + str(time.time_ns()))
        auth.rename(failed)
    if (backup / '.join-auth').exists():
        shutil.copytree(backup / '.join-auth', auth)
        for path in [auth, *auth.rglob('*')]:
            os.chown(path, meta['scriptUid'], meta['scriptGid'])
    process = start_game(id, None)
    write_json(work / 'state.json', dict(instance=id, phase='rolled-back', at=now(), process=process))
    print(json.dumps(dict(instance=id, phase='rolled-back', process=process)))


@contextlib.contextmanager
def maintenance_lock():
    import fcntl
    with Path('/var/lock/mcqq-server-maintenance.lock').open('a') as stream:
        fcntl.flock(stream, fcntl.LOCK_EX | fcntl.LOCK_NB)
        yield


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    action = parser.add_mutually_exclusive_group(required=True)
    action.add_argument('--prepare', type=Path, metavar='AGENT_JAR')
    action.add_argument('--activate', choices=SERVERS)
    action.add_argument('--prepare-server', nargs=2, metavar=('INSTANCE', 'AGENT_JAR'))
    action.add_argument('--activate-server', choices=SERVERS)
    action.add_argument('--rollback', choices=SERVERS)
    action.add_argument('--mode', nargs=2, metavar=('INSTANCE', 'MODE'))
    action.add_argument('--status', action='store_true')
    args = parser.parse_args()
    if not sys.platform.startswith('linux') or os.geteuid() != 0:
        raise SystemExit('Run on the NAS host with sudo')
    STAGING.mkdir(parents=True, exist_ok=True, mode=0o700)
    os.chmod(STAGING, 0o700)
    with maintenance_lock():
        if args.prepare:
            prepare(args.prepare)
        elif args.activate:
            activate(args.activate)
        elif args.prepare_server:
            prepare_server(args.prepare_server[0], Path(args.prepare_server[1]))
        elif args.activate_server:
            activate_server(args.activate_server)
        elif args.rollback:
            rollback(args.rollback)
        elif args.mode:
            set_mode(*args.mode)
        else:
            print(json.dumps({id: read_json(STAGING / 'servers' / id / 'state.json') if (STAGING / 'servers' / id / 'state.json').exists() else {'phase': 'unprepared'} for id in SERVERS}))


if __name__ == '__main__':
    try:
        main()
    except Exception as exc:
        print(f'Join-gate deployment stopped: {type(exc).__name__}: {exc}', file=sys.stderr)
        raise SystemExit(1)
