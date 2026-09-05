"""Install from public sources into a new empty root, using the actual native installer.

This checks network completeness and bundled Java, not a clean Windows OS or gameplay.
The one cache is initially empty; subsequent instances may reuse verified shared assets.
"""
import argparse
import hashlib
import json
from pathlib import Path
import subprocess
import time
from pack_distribution import ROOT

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--instances', nargs='+', choices=['m3e', 'dc2', 'mb'], default=['dc2', 'm3e', 'mb'])
parser.add_argument('--root', type=Path, required=True)
parser.add_argument('--dotnet', type=Path, default=ROOT.parent / '.tools/dotnet10/dotnet.exe')
args = parser.parse_args()
target = args.root.resolve()
if not target.is_relative_to(ROOT / '.local') or target.exists():
    parser.error('--root must be a new directory under .local; existing cache cannot count as a fresh network check')
target.mkdir(parents=True)
report = {'startedAt': time.strftime('%Y-%m-%dT%H:%M:%SZ', time.gmtime()), 'initialCacheEmpty': True,
          'cleanWindows': False, 'gameStarted': False, 'allCompleted': False, 'checks': []}
report_path = target / 'network-install-report.json'

def save():
    report_path.write_text(json.dumps(report, indent=2) + '\n', encoding='utf-8')

save()
for instance in args.instances:
    manifest = ROOT / f'artifacts/native/{instance}-manifest.json'
    # Pin this run's input even if the next candidate is prepared concurrently.
    snapshot = target / (instance + '-manifest.json')
    snapshot.write_bytes(manifest.read_bytes())
    print(json.dumps({'instance': instance, 'networkInstallStarted': True, 'limitMiB': 2}), flush=True)
    result = subprocess.run([str(args.dotnet), str(ROOT / 'src/Publisher/bin/Release/net10.0/Publisher.dll'),
                             'check-install', str(snapshot), str(target)], cwd=ROOT,
                            creationflags=subprocess.CREATE_NO_WINDOW, capture_output=True)
    (target / (instance + '-install.log')).write_bytes(result.stdout + result.stderr)
    if result.returncode:
        report['failedInstance'] = instance
        save()
        raise SystemExit(instance + ': network installation failed; see isolated install log')
    installed = json.loads((target / (instance + '-install-check.json')).read_text(encoding='utf-8-sig'))
    installed['manifestSha256'] = hashlib.sha256(snapshot.read_bytes()).hexdigest()
    report['checks'].append(installed)
    save()
    print(json.dumps({'instance': instance, 'networkInstallCompleted': True}), flush=True)
report['allCompleted'] = True
save()
print(json.dumps({'report': str(report_path), 'allCompleted': True, 'cleanWindows': False}), flush=True)
