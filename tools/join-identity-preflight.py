"""Read only existing role-binding metadata on the NAS; never reads account credentials."""
import argparse
import json
import re
import subprocess

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('game_name')
args = parser.parse_args()
if not re.fullmatch(r'[A-Za-z0-9_]{3,16}', args.game_name):
    raise SystemExit('Invalid game name')


def query(sql):
    result = subprocess.run(['docker', 'exec', 'mc-client-hub-postgres-1', 'psql', '-U', 'hub', '-d', 'hub', '-v', 'ON_ERROR_STOP=1', '-Atc', sql], check=True, capture_output=True, text=True)
    return result.stdout.strip()


key = args.game_name.upper()
report = json.loads(query('''SELECT json_build_object(
  'gameName','%s',
  'protectedExactName',(SELECT "ExactName" FROM "ProtectedNames" WHERE "Key"='%s'),
  'hubExactName',(SELECT "GameName" FROM "AspNetUsers" WHERE "GameNameKey"='%s'),
  'hubDisabled',(SELECT "Disabled" FROM "AspNetUsers" WHERE "GameNameKey"='%s'),
  'joinSchemaExists',to_regclass('"JoinIdentities"') IS NOT NULL);''' % (args.game_name, key, key, key)))
if report['joinSchemaExists']:
    report['joinIdentity'] = json.loads(query('''SELECT COALESCE((SELECT json_build_object('gameName',"GameName",'hasHubBinding',"HubUserId" IS NOT NULL,'minecraftProfileId',"MinecraftProfileId",'gameUuid',"GameUuid",'disabled',"Disabled") FROM "JoinIdentities" WHERE "GameNameKey"='%s'),'null'::json);''' % key))
print(json.dumps(report, ensure_ascii=False))
