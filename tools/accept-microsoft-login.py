"""Manual test: only run when the player is ready for a visible Microsoft authorization window."""
import argparse, datetime, json, pathlib, subprocess
from release_config import ROOT, auth_fingerprint

parser=argparse.ArgumentParser()
parser.add_argument('--dotnet',default='dotnet')
args=parser.parse_args()
assembly=ROOT/'tests/NativeAccountSmoke/bin/Release/net10.0-windows/NativeAccountSmoke.dll'
if not assembly.is_file():raise SystemExit('Build NativeAccountSmoke in Release first.')
before=auth_fingerprint()
process=subprocess.run([args.dotnet,str(assembly),'--microsoft-live'],cwd=ROOT,capture_output=True,text=True,encoding='utf-8')
if process.returncode:
    print(process.stderr.strip() or process.stdout.strip() or 'Microsoft acceptance did not complete.')
    raise SystemExit(process.returncode)
result=json.loads(process.stdout.strip().splitlines()[-1])
required=['liveMicrosoftLoginVerified','encryptedSessionRestored','silentAuthenticationPassed','gameSessionVerified']
if not all(result.get(key) is True for key in required):raise SystemExit('Microsoft acceptance is incomplete.')
if before!=auth_fingerprint():raise SystemExit('Authentication code changed during the test; repeat acceptance on a consistent build.')
report={key:result[key] for key in required}
report.update({'skinDownloaded':result.get('skinDownloaded',False),'checkedAt':datetime.datetime.now(datetime.timezone.utc).isoformat(),'authFingerprint':before,'provider':'cmllib-windows'})
(ROOT/'packs/launcher-acceptance.json').write_text(json.dumps(report,indent=2)+'\n',encoding='utf-8')
print(json.dumps(report))
