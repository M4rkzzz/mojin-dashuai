"""Run on the Hub Docker host against an isolated database/container. Prints no credentials."""
import concurrent.futures
import hashlib
import json
import os
import pathlib
import re
import secrets
import subprocess
import time
import urllib.error
import urllib.request


def run(*args):
    completed = subprocess.run(args, check=True, capture_output=True, text=True)
    return completed.stdout


stamp = secrets.token_hex(4)
database = 'hub_join_acceptance_' + stamp
container = 'boshan-join-acceptance-' + stamp
image = os.environ.get('HUB_API_TEST_IMAGE', 'boshan/hub-api:1.1.0')
pg = 'mc-client-hub-postgres-1'
port = int(os.environ.get('HUB_JOIN_TEST_PORT', '18083'))
assert re.fullmatch(r'hub_join_acceptance_[a-f0-9]{8}', database)
assert 18083 <= port <= 18100
password = pathlib.Path('/var/apps/mc-client-hub/secrets/db-password').read_text().strip()
keys = {name: secrets.token_hex(32) for name in ('m3e', 'dc2', 'mb', 'vw')}
networks = json.loads(run('docker', 'network', 'inspect', 'mc-client-hub_database', 'mc-client-hub_edge'))
gateways = [entry['Gateway'] + '/32' for network in networks for entry in network['IPAM']['Config'] if entry.get('Gateway') and ':' not in entry['Gateway']]
assert gateways, 'Isolated Docker network gateway missing'
env = pathlib.Path('/var/apps/mc-client-hub/secrets/join-acceptance-' + stamp + '.env')
env.write_text('ConnectionStrings__Hub=Host=' + pg + ';Database=' + database + ';Username=hub;Password=' + password + '\nInitializeDatabase=true\nASPNETCORE_URLS=http://+:8080\nJoinAuth__Enabled=true\nJoinAuth__InternalNetworks=' + ','.join(gateways) + '\n' + ''.join('JoinAuth__ServerKeys__' + name + '=' + value + '\n' for name, value in keys.items()))
env.chmod(0o600)
checks = []
created = False


def check(condition, label):
    if not condition:
        raise AssertionError(label)
    checks.append(label)


def api(path, body=None, bearer=None):
    headers = {'Content-Type': 'application/json'}
    if bearer:
        headers['Authorization'] = 'Bearer ' + bearer
    request = urllib.request.Request('http://127.0.0.1:' + str(port) + path, data=None if body is None else json.dumps(body).encode(), headers=headers)
    try:
        with urllib.request.urlopen(request, timeout=15) as response:
            raw = response.read()
            return response.status, json.loads(raw) if raw else None
    except urllib.error.HTTPError as error:
        raw = error.read()
        return error.code, json.loads(raw) if raw else None


def admin(*args):
    return run('docker', 'exec', container, 'dotnet', 'Hub.Api.dll', 'admin', *args)


def sql(command):
    return run('docker', 'exec', pg, 'psql', '-U', 'hub', '-d', database, '-v', 'ON_ERROR_STOP=1', '-Atc', command)


def issue(bearer, instance='dc2'):
    status, result = api('/v1/join/tickets', {'instance': instance}, bearer)
    check(status == 200, 'authenticated ticket issue')
    check(re.fullmatch(r'[A-Za-z0-9_-]{43}', result['ticket']) is not None, 'opaque ticket format')
    return result


def redeem(ticket, instance='dc2', name='JoinAlice', key=None):
    return api('/internal/v1/join/redeem', {'ticket': ticket, 'instance': instance, 'gameName': name}, keys[instance] if key is None else key)


try:
    run('docker', 'exec', pg, 'createdb', '-U', 'hub', database)
    created = True
    run('docker', 'create', '--name', container, '--network', 'mc-client-hub_database', '--env-file', str(env), '-p', '127.0.0.1:' + str(port) + ':8080', image)
    # The database network is internal-only; attach edge like production so the host's
    # loopback-published acceptance port can actually reach the container.
    run('docker', 'network', 'connect', 'mc-client-hub_edge', container)
    run('docker', 'start', container)
    for _ in range(40):
        try:
            if api('/health')[0] == 200:
                break
        except Exception:
            pass
        time.sleep(.5)
    check(api('/health')[0] == 200, 'isolated API healthy')
    admin('join-init')
    admin('join-init')
    check(True, 'additive schema initialization idempotent')
    invitation = re.search(r'Code \(shown once\): ([a-f0-9]+)', admin('invite-create', 'super'))[1]
    status, account = api('/v1/auth/register', {'loginName': 'join-alice', 'gameName': 'JoinAlice', 'password': 'join-acceptance-' + stamp, 'invitation': invitation})
    check(status == 200, 'Hub account registration retains join identity')
    bearer = account['accessToken']
    check(api('/v1/join/tickets', {'instance': 'dc2'})[0] == 401, 'unauthenticated issue rejected')
    check(api('/v1/join/tickets', {'instance': 'unknown'}, bearer)[0] == 400, 'unknown instance rejected')
    ticket = issue(bearer)
    raw = ticket['ticket']
    digest = hashlib.sha256(raw.encode()).hexdigest()
    check(sql('SELECT count(*) FROM "JoinTickets" WHERE "TokenHash" = \'' + digest + "';").strip() == '1', 'only ticket hash persisted')
    check(redeem(raw, key=keys['mb'])[0] == 403, 'other server key rejected')
    check(redeem(raw, instance='mb')[0] == 403, 'cross-instance ticket rejected')
    check(redeem(raw, name='joinalice')[0] == 403, 'case-changed login rejected')
    with concurrent.futures.ThreadPoolExecutor(max_workers=2) as pool:
        responses = list(pool.map(lambda _: redeem(raw), range(2)))
    check(sorted(code for code, _ in responses) == [200, 403], 'concurrent redemption accepts exactly once')
    accepted = next(body for code, body in responses if code == 200)
    check(accepted == {'allowed': True, 'gameName': 'JoinAlice', 'gameUuid': ticket['gameUuid']}, 'accepted exact identity and offline UUID retained')
    check(redeem(raw)[0] == 403, 'spent ticket cannot replay')
    expired = issue(bearer)['ticket']
    expired_hash = hashlib.sha256(expired.encode()).hexdigest()
    sql('UPDATE "JoinTickets" SET "ExpiresAt" = now() - interval \'1 second\' WHERE "TokenHash" = \'' + expired_hash + "';")
    check(redeem(expired)[0] == 403, 'expired ticket rejected')
    revoke = issue(bearer)['ticket']
    check(api('/v1/auth/logout', {}, bearer)[0] == 204, 'session logout accepted')
    check(redeem(revoke)[0] == 403, 'revoked session invalidates unconsumed ticket')
    check(api('/v1/join/tickets', {'instance': 'dc2'}, bearer)[0] == 401, 'revoked session cannot issue')
    official_id = '069a79f444e94726a5befca90e38aaf5'
    result = subprocess.run(['docker', 'exec', container, 'dotnet', 'Hub.Api.dll', 'admin', 'join-bind-minecraft', official_id, 'JoinAlice'], capture_output=True, text=True)
    check(result.returncode != 0, 'same-name Hub/Microsoft identities are not implicitly merged')
    admin('join-bind-minecraft', official_id, 'JoinAlice', '--link-existing-hub')
    check(True, 'administrator explicit owner-confirmed binding available')
    print(json.dumps({'scope': 'isolated-postgresql-not-production', 'image': image, 'passed': True, 'checks': checks}, ensure_ascii=False, indent=2))
except Exception as error:
    capture = subprocess.run(['docker', 'logs', '--tail', '100', container], capture_output=True, text=True)
    details = capture.stdout + capture.stderr
    for secret in [password, *keys.values(), *[str(globals().get(n, '')) for n in ('invitation', 'bearer', 'raw', 'expired', 'revoke')]]:
        if secret:
            details = details.replace(secret, '[redacted]')
    diagnostics = env.with_suffix('.failure.log')
    diagnostics.write_text(details)
    diagnostics.chmod(0o600)
    print(json.dumps({'passed': False, 'category': type(error).__name__, 'completedChecks': checks, 'diagnosticLog': str(diagnostics)}, ensure_ascii=False))
    raise
finally:
    subprocess.run(['docker', 'rm', '-f', container], capture_output=True)
    if created:
        subprocess.run(['docker', 'exec', pg, 'dropdb', '-U', 'hub', database], capture_output=True)
    env.unlink(missing_ok=True)
