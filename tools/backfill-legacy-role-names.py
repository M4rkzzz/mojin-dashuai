"""NAS: reserve all unclaimed, unambiguous historical names. Never bind an owner."""
import argparse
import datetime
import json
import pathlib
import re
import subprocess

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--apply', action='store_true')
args = parser.parse_args()
audit = json.loads(subprocess.run(['python3', '/tmp/mojin-audit-legacy-join.py'], check=True, capture_output=True, text=True).stdout)
if audit['errors'] or any(server['playerDataWithoutKnownName'] for server in audit['servers'].values()):
    raise SystemExit('Incomplete role audit; inspect it before backfill')
candidates = [row['names'][0] for row in audit['roles'] if not row['protected'] and not row['issues'] and len(row['names']) == 1 and row['status'] == 'automatic_on_verified_microsoft_login']
if any(not re.fullmatch(r'[A-Za-z0-9_]{3,16}', name) for name in candidates):
    raise SystemExit('Invalid historical name')
report = {'at': datetime.datetime.now(datetime.timezone.utc).isoformat(), 'applied': args.apply, 'candidates': candidates, 'automaticBindingRequiresOfficialToken': True, 'ownerBindingsChanged': False}
if args.apply and candidates:
    values = ','.join("('%s','%s')" % (name.upper(), name) for name in candidates)
    # Recheck live account ownership inside the same serializable transaction.
    sql = '''BEGIN ISOLATION LEVEL SERIALIZABLE;
    WITH inserted AS (
      INSERT INTO "ProtectedNames" ("Key","ExactName")
      SELECT candidate.key,candidate.name FROM (VALUES %s) candidate(key,name)
      WHERE NOT EXISTS (SELECT 1 FROM "AspNetUsers" u WHERE u."GameNameKey"=candidate.key)
        AND NOT EXISTS (SELECT 1 FROM "JoinIdentities" i WHERE i."GameNameKey"=candidate.key)
      ON CONFLICT DO NOTHING RETURNING "ExactName")
    SELECT COALESCE(json_agg("ExactName"),'[]'::json) FROM inserted; COMMIT;''' % values
    result = subprocess.run(['docker', 'exec', '-i', 'mc-client-hub-postgres-1', 'psql', '-U', 'hub', '-d', 'hub', '-v', 'ON_ERROR_STOP=1', '-qAt'], input=sql, check=True, capture_output=True, text=True)
    report['inserted'] = json.loads(result.stdout)
    report['insertedCount'] = len(report['inserted'])
    path = pathlib.Path('/var/apps/mc-client-hub/releases/api-1.1.1/legacy-name-backfill.json')
    path.write_text(json.dumps(report, ensure_ascii=False, indent=2))
print(json.dumps(report, ensure_ascii=False, indent=2))
