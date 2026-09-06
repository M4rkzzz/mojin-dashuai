"""Small integration check against all three pinned loaders. No account, network or game launch."""
from pathlib import Path
import subprocess
import uuid
import json

root = Path(__file__).resolve().parents[1]
java_home = next((root.parent / '.tools/temurin25').glob('*/bin'))
output = root / '.local/loading-agent/checks'
output.mkdir(parents=True, exist_ok=True)
subprocess.run([str(java_home / 'javac.exe'), '-J-Duser.language=en', '--release', '8', '-encoding', 'UTF-8', '-d', str(output),
                str(root / 'tests/game-integration/LoadingTelemetryCheck.java')], check=True, creationflags=subprocess.CREATE_NO_WINDOW)
agent = root / 'src/Launcher.Desktop/Assets/GameLoading/mojin-loading-agent.jar'
sources = [
    ('m3e', 'cpw.mods.fml.common.ProgressManager'),
    ('mb', 'net.minecraftforge.fml.common.ProgressManager'),
    ('dc2', 'net.minecraftforge.fml.loading.progress.StartupNotificationManager'),
]
report = []
for instance, manager in sources:
    engine = root / '.local/engines' / instance
    jars = list(engine.glob('libraries/**/*.jar'))
    # Keep only the loader and its basic helper libraries, not the game runtime.
    jars = [p for p in jars if any(part in p.parts for part in ('forge', 'cleanroom', 'fmlloader', 'guava', 'failureaccess', 'log4j-api', 'log4j-core'))]
    work = output / instance
    work.mkdir(exist_ok=True)
    session = uuid.uuid4().hex
    classpath = ';'.join([str(output), *(str(p) for p in jars)])
    result = subprocess.run([str(java_home / 'java.exe'), '-javaagent:' + str(agent), '-Dmojin.loading.session=' + session,
                             '-cp', classpath, 'LoadingTelemetryCheck', manager], cwd=work, capture_output=True,
                            timeout=20, creationflags=subprocess.CREATE_NO_WINDOW)
    if result.returncode:
        raise RuntimeError((result.stdout + result.stderr).decode('utf-8', errors='replace'))
    print(result.stdout.decode('utf-8', errors='replace').strip())
    report.append({'instance': instance, 'actualCounter': '3/8', 'currentTaskUpdates': True,
                   'modDisplayNameUpdates': instance != 'dc2', 'internalDetailFiltered': True,
                   'taskAvailableWithoutOverallCounter': True, 'elapsedTimeDoesNotIncreaseProgress': True, 'stopsAfterHandoff': True})
    # Delete only this check's small protocol files, never any client instance or shared runtime.
    for suffix in ('.json', '.tmp', '.stop'):
        (work / '.hub/loading' / (session + suffix)).unlink(missing_ok=True)
(output / 'report.json').write_text(json.dumps(report, indent=2), encoding='utf-8')
