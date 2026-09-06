"""NAS read-only ticket consumption evidence. No ticket, token hash, grant or service key is selected."""
import argparse
import datetime as dt
import hashlib
import json
import re
import subprocess
import uuid

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--instance', choices=('m3e', 'dc2', 'mb', 'vw'), required=True)
parser.add_argument('--from-utc', required=True)
parser.add_argument('--until-utc', required=True)
parser.add_argument('--game-name', default='M4rkzzz')
parser.add_argument('--official-uuid', default='3891a818-7997-4b99-ba87-314acd6710d8')
args = parser.parse_args()
if not re.fullmatch(r'[A-Za-z0-9_]{3,16}', args.game_name):
    raise SystemExit('Invalid game name')


def utc(value):
    result = dt.datetime.fromisoformat(value.replace('Z', '+00:00'))
    if result.tzinfo is None:
        raise ValueError('Explicit UTC offset required')
    return result.astimezone(dt.timezone.utc)


start, end = utc(args.from_utc), utc(args.until_utc)
if end <= start or end - start > dt.timedelta(hours=4):
    raise SystemExit('Window must be positive and at most four hours')
official_id = uuid.UUID(args.official_uuid).hex
offline_id = str(uuid.UUID(bytes=hashlib.md5(('OfflinePlayer:' + args.game_name).encode()).digest(), version=3))
# Inputs below are constrained enum/name/UUID or normalized timestamps, never raw SQL fragments.
name, instance = args.game_name, args.instance
start_sql, end_sql = start.isoformat(), end.isoformat()
sql = f'''WITH bound_identity AS (
 SELECT "Id" FROM "JoinIdentities"
 WHERE "GameName"='{name}' AND "MinecraftProfileId"='{official_id}'
 AND "GameUuid"='{offline_id}' AND NOT "Disabled"
), scoped AS (
 SELECT t."InstanceId", t."ExactName", t."ExpiresAt" - interval '120 seconds' AS issued_at,
        t."ConsumedAt" AS consumed_at
 FROM "JoinTickets" t JOIN bound_identity i ON i."Id"=t."IdentityId"
 WHERE t."InstanceId"='{instance}' AND t."ExactName"='{name}' AND t."GameUuid"='{offline_id}'
), issued AS (
 SELECT * FROM scoped WHERE issued_at >= '{start_sql}'::timestamptz AND issued_at < '{end_sql}'::timestamptz
), successful AS (
 SELECT * FROM scoped WHERE consumed_at >= '{start_sql}'::timestamptz AND consumed_at < '{end_sql}'::timestamptz
)
SELECT json_build_object(
 'verifiedIdentityExists',EXISTS(SELECT 1 FROM bound_identity),
 'issuedDuringWindow',(SELECT count(*) FROM issued),
 'successfulConsumptionCount',(SELECT count(*) FROM successful),
 'issuedRecords',COALESCE((SELECT json_agg(json_build_object(
     'instance',"InstanceId",'gameName',"ExactName",
     'issuedAtDerivedUtc',to_char(issued_at AT TIME ZONE 'UTC','YYYY-MM-DD"T"HH24:MI:SS.US"Z"'),
     'consumedAtUtc',to_char(consumed_at AT TIME ZONE 'UTC','YYYY-MM-DD"T"HH24:MI:SS.US"Z"')) ORDER BY issued_at)
     FROM issued),'[]'::json),
 'records',COALESCE((SELECT json_agg(json_build_object(
     'instance',"InstanceId",'gameName',"ExactName",
     'issuedAtDerivedUtc',to_char(issued_at AT TIME ZONE 'UTC','YYYY-MM-DD"T"HH24:MI:SS.US"Z"'),
     'consumedAtUtc',to_char(consumed_at AT TIME ZONE 'UTC','YYYY-MM-DD"T"HH24:MI:SS.US"Z"')) ORDER BY consumed_at)
     FROM successful),'[]'::json));'''
result = subprocess.run(['docker', 'exec', 'mc-client-hub-postgres-1', 'psql', '-X', '-U', 'hub', '-d', 'hub', '-v', 'ON_ERROR_STOP=1', '-Atc', sql], capture_output=True, text=True)
if result.returncode:
    print(json.dumps({'scope': 'production-read-only', 'instance': instance, 'gameName': name, 'querySucceeded': False, 'errorCategory': 'DatabaseQueryFailed'}))
    raise SystemExit(1)
report = json.loads(result.stdout.strip())
report.update({
    'scope': 'production-read-only',
    'querySucceeded': True,
    'queriedAtUtc': dt.datetime.now(dt.timezone.utc).isoformat(),
    'instance': instance,
    'gameName': name,
    'windowFromUtc': start_sql,
    'windowUntilUtcExclusive': end_sql,
    'issuedTimestampBasis': 'ExpiresAt minus fixed 120-second ticket lifetime; no dedicated issuance column',
    'originalTicketOrHashIncluded': False,
})
print(json.dumps(report, ensure_ascii=False, indent=2))
