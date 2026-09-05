"""Locate pinned GTNH release assets, retaining only exact names and sizes.

Full file hash verification is still required by verify-pack-downloads.py.
"""
import concurrent.futures
import json
from pathlib import Path
import re
import subprocess
import urllib.parse
import zipfile

ROOT = Path(__file__).resolve().parents[1]
SOURCE = Path('D:/Desktop/魔金大帅/.minecraft/versions/MSE')
ALIASES = {'ae2fc': 'AE2FluidCraft-Rework', 'appliedenergistics2': 'Applied-Energistics-2-Unofficial',
           'thaumicexploration': 'Thaumic_Exploration', 'thaumcraftresearchtweaks': 'thaumcraft-research-tweaks',
           'ae2wct': 'WirelessCraftingTerminal', 'neenergistics': 'NotEnoughEnergistics', 'minetweaker3': 'CraftTweaker'}
EXTRA_REPOS = {'coretweaks': 'makamys/CoreTweaks', 'neodymium': 'makamys/Neodymium',
               'storageDrawers': 'GTNewHorizons/StorageDrawers'}


def gh(path):
    result = subprocess.run(['gh', 'api', path], stdout=subprocess.PIPE, stderr=subprocess.PIPE)
    return json.loads(result.stdout) if result.returncode == 0 else None


def main():
    audit_path = ROOT / 'packs/m3e-source-audit.json'
    audit = json.loads(audit_path.read_text(encoding='utf-8'))
    repositories = {}
    for page in range(1, 10):
        results = gh(f'orgs/GTNewHorizons/repos?per_page=100&page={page}')
        if results is None:
            raise ValueError('Could not read official GTNH repositories')
        for repo in results:
            repositories[re.sub('[^a-z0-9]', '', repo['name'].lower())] = repo['full_name']
        if len(results) < 100:
            break

    def resolve(row):
        with zipfile.ZipFile(SOURCE / row['path']) as archive:
            if 'mcmod.info' not in archive.namelist():
                return None
            try:
                mods = json.loads(archive.read('mcmod.info'))
                if isinstance(mods, dict):
                    mods = mods.get('modList', [])
            except ValueError:
                return None
        for mod in mods:
            name = ALIASES.get(mod.get('modid', '').lower(), mod.get('modid', ''))
            repo = repositories.get(re.sub('[^a-z0-9]', '', name.lower()))
            supplied = urllib.parse.urlsplit(mod.get('url') or '')
            if not repo and supplied.hostname == 'github.com' and len(supplied.path.strip('/').split('/')) == 2:
                repo = supplied.path.strip('/')
            repo = repo or EXTRA_REPOS.get(mod.get('modid', ''))
            if not repo:
                continue
            version = mod.get('version', '')
            if not version or '$' in version:
                continue
            release = gh('repos/' + repo + '/releases/tags/' + urllib.parse.quote(version, safe=''))
            releases = [release] if release else gh('repos/' + repo + '/releases?per_page=100') or []
            candidates = [(r, a) for r in releases for a in r['assets'] if a['name'] == row['path'].rsplit('/', 1)[-1] and a['size'] == row['size']]
            if len(candidates) != 1:
                continue
            release, asset = candidates[0]
            if asset.get('digest') and asset['digest'] != 'sha256:' + row['sha256']:
                continue
            return {'path': row['path'], 'sha256': row['sha256'], 'url': asset['browser_download_url'],
                    'releaseUrl': release['html_url'], 'assetId': asset['id'], 'repository': repo}
        return None

    rows = [r for r in audit['files'] if not r.get('sources')]
    with concurrent.futures.ThreadPoolExecutor(max_workers=3) as executor:
        resolved = [r for r in executor.map(resolve, rows) if r]
    output = ROOT / '.local/gtnh-exact-origins.json'
    output.write_text(json.dumps(resolved, ensure_ascii=False, indent=2), encoding='utf-8')
    print(json.dumps({'exactReleaseCandidates': len(resolved), 'unresolvedChecked': len(rows)}))


if __name__ == '__main__':
    main()
