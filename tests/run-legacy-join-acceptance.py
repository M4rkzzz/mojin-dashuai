"""Run compiled legacy association fixtures in a disposable PostgreSQL DB on the NAS."""
import json
import os
import pathlib
import re
import secrets
import subprocess
import sys

release = pathlib.Path(sys.argv[1]).resolve()
assert release.parent == pathlib.Path('/var/apps/mc-client-hub/releases')
assert re.fullmatch(r'api-\d+\.\d+\.\d+', release.name)
stamp = secrets.token_hex(4)
database = 'hub_legacy_acceptance_' + stamp
env = pathlib.Path('/var/apps/mc-client-hub/secrets/legacy-acceptance-' + stamp + '.env')
password = pathlib.Path('/var/apps/mc-client-hub/secrets/db-password').read_text().strip()
pg = 'mc-client-hub-postgres-1'
container = 'boshan-legacy-acceptance-' + stamp
created = False
def run(*args):
    return subprocess.run(args, check=True, capture_output=True, text=True).stdout
try:
    with env.open('x') as stream:
        os.chmod(env, 0o600)
        stream.write('ConnectionStrings__Hub=Host=' + pg + ';Database=' + database + ';Username=hub;Password=' + password + '\n')
    run('docker', 'exec', pg, 'createdb', '-U', 'hub', database)
    created = True
    result = subprocess.run(['docker', 'run', '--rm', '--name', container, '--network', 'mc-client-hub_database', '--env-file', str(env), '-v', str(release / 'legacy-tests') + ':/test:ro', '--entrypoint', 'dotnet', 'boshan/hub-api:' + release.name[4:], '/test/JoinLegacy.Acceptance.dll', '/test/roles.json'], capture_output=True, text=True)
    if result.returncode:
        # Only synthetic fixture identifiers reach this log; redact the DB password.
        details = (result.stdout + result.stderr).replace(password, '[redacted]')
        (release / 'legacy-acceptance-failure.log').write_text(details)
        raise RuntimeError('Legacy acceptance failed; inspect private release failure log')
    report = json.loads(result.stdout)
    (release / 'legacy-acceptance.json').write_text(json.dumps(report, indent=2))
    print(json.dumps({key: report[key] for key in ('passed', 'scope', 'auditedRolesCovered', 'checkCount')}))
finally:
    subprocess.run(['docker', 'rm', '-f', container], capture_output=True)
    if created:
        subprocess.run(['docker', 'exec', pg, 'dropdb', '-U', 'hub', database], capture_output=True, check=True)
    env.unlink(missing_ok=True)
