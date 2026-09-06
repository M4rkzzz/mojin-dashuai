"""Manage only the explicitly approved public group invitation, using the existing Invitation schema."""
import argparse
import hashlib
import json
import subprocess
import uuid

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--apply', action='store_true', help='Idempotently activate the approved public super invitation')
args = parser.parse_args()
code = '1105114550'
digest = hashlib.sha256(code.encode()).hexdigest()


def query(sql):
    result = subprocess.run(['docker', 'exec', 'mc-client-hub-postgres-1', 'psql', '-X', '-q', '-U', 'hub', '-d', 'hub', '-v', 'ON_ERROR_STOP=1', '-Atc', sql], check=True, capture_output=True, text=True)
    return result.stdout.strip()


columns = set(query("SELECT column_name FROM information_schema.columns WHERE table_schema='public' AND table_name='Invitations'").splitlines())
expected = {'Id', 'CodeHash', 'Reusable', 'Revoked', 'ExpiresAt', 'BoundGameName', 'UseCount', 'CreatedAt'}
if columns != expected:
    raise SystemExit('Invitation schema differs; no changes applied')
if args.apply:
    # The unique CodeHash constraint serializes competing upserts. Existing IDs,
    # creation timestamps and the historical audit UseCount are deliberately retained.
    query(f'''BEGIN;
        INSERT INTO "Invitations" ("Id","CodeHash","Reusable","Revoked","ExpiresAt","BoundGameName","UseCount","CreatedAt")
        VALUES ('{uuid.uuid4()}','{digest}',true,false,NULL,NULL,0,now())
        ON CONFLICT ("CodeHash") DO UPDATE SET "Reusable"=true,"Revoked"=false,"ExpiresAt"=NULL,"BoundGameName"=NULL;
        COMMIT;''')
result = query(f'''SELECT json_build_object(
    'invitationId',"Id",'reusable',"Reusable",'revoked',"Revoked",'expiresAt',"ExpiresAt",
    'boundGameName',"BoundGameName",'useCount',"UseCount",'createdAt',"CreatedAt")
    FROM "Invitations" WHERE "CodeHash"='{digest}';''')
report = json.loads(result) if result else {'exists': False}
report.update({'code': code, 'applied': args.apply, 'usageLimit': None, 'existingAccountsModified': False})
if args.apply and (not report.get('reusable') or report.get('revoked') or report.get('expiresAt') is not None or report.get('boundGameName') is not None):
    raise SystemExit('Invitation metadata verification failed')
print(json.dumps(report, ensure_ascii=False, indent=2))
