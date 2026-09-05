"""Headless acceptance of the real pinned packwiz importer and our generated format."""
import argparse
import functools
import hashlib
from http.server import SimpleHTTPRequestHandler, ThreadingHTTPServer
import json
from pathlib import Path
import subprocess
import threading
import uuid
from pack_distribution import ROOT, packwiz_files

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--java', type=Path, required=True)
args = parser.parse_args()
root = ROOT / '.local/portable-acceptance' / uuid.uuid4().hex
hosted, client = root / 'hosted', root / 'client'
hosted.mkdir(parents=True)
client.mkdir()
row = json.loads((ROOT / '.local/fallback-native-check.json').read_text(encoding='utf-8'))
row['sources'] = row['sources'][1:]
spec = json.loads((ROOT / 'packs/distributions.json').read_text(encoding='utf-8'))['instances']['mb']
files = packwiz_files(spec, [row], {'options.txt': b'lang:zh_cn\n', 'config/acceptance.cfg': b'provided-pack-config=true\n'})
for name, data in files.items():
    target = hosted / name
    target.parent.mkdir(parents=True, exist_ok=True)
    target.write_bytes(data)

class QuietHandler(SimpleHTTPRequestHandler):
    def log_message(self, *args):
        pass

server = ThreadingHTTPServer(('127.0.0.1', 0), functools.partial(QuietHandler, directory=str(hosted)))
threading.Thread(target=server.serve_forever, daemon=True).start()
command = [str(args.java.resolve()), '-Djava.awt.headless=true', '-jar', str(ROOT / '.local/packwiz-installer-bootstrap.jar'),
           '--bootstrap-no-update', '--bootstrap-main-jar', str(ROOT / '.local/packwiz-installer.jar'),
           '-g', 'http://127.0.0.1:' + str(server.server_port) + '/pack.toml']

def install():
    result = subprocess.run(command, cwd=client, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, timeout=60)
    (root / 'installer.log').write_bytes(result.stdout)
    if result.returncode:
        raise SystemExit('Pinned packwiz import failed; inspect the isolated installer.log')

try:
    install()
    mod = client / row['path']
    assert hashlib.sha256(mod.read_bytes()).hexdigest() == row['sha256']
    assert (client / 'config/acceptance.cfg').read_bytes() == files['config/acceptance.cfg']
    (client / 'options.txt').write_bytes(b'player-setting=true\n')
    install()
    assert (client / 'options.txt').read_bytes() == b'player-setting=true\n'
    report = {'realPackwizImporter': True, 'generatedMetadataAccepted': True, 'selfHostedFileHashVerified': True,
              'configurationInstalled': True, 'existingPlayerSettingsPreserved': True, 'headless': True,
              'gameAcceptance': False, 'bootstrapVersion': '0.0.3', 'installerVersion': '0.5.14'}
    (ROOT / 'packs/portable-installer-acceptance.json').write_text(json.dumps(report, indent=2) + '\n', encoding='utf-8')
    print(json.dumps(report))
finally:
    server.shutdown()
