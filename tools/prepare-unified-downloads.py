"""Prepare a content-only revision that serves every pinned file through the hub."""
import argparse
import copy
import hashlib
import json
from pathlib import Path


def prepare(source: Path, output: Path, public_base: str):
    original = json.loads(source.read_text(encoding='utf-8-sig'))
    candidate = copy.deepcopy(original)
    changed = []
    for row in candidate['files'] + [candidate['runtime']['archive']]:
        destination = public_base.rstrip('/') + '/objects/sha256/' + row['sha256']
        if row.get('officialOnly'):
            changed.append({'path': row['path'], 'sha256': row['sha256'], 'size': row['size'],
                            'authorSources': row['sources']})
            row['distributionBasis'] = 'Pinned unmodified pack component; bundled through operator download service; original attribution retained'
        row['officialOnly'] = False
        row['sources'] = [destination]
    candidate['bundles'] = []
    output.mkdir(parents=True, exist_ok=False)
    (output / 'input.json').write_text(json.dumps(candidate, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
    receipt = {'instance': original['instance'], 'baseVersion': original['version'],
               'baseSequence': original['sequence'], 'baseManifestSha256': hashlib.sha256(source.read_bytes()).hexdigest(),
               'changedDownloadRoutes': changed, 'fileBytesUnchanged': True, 'runtimeUnchanged': True,
               'request': 'Bundle required mods in the complete client and serve differential files from the same FRP origin',
               'authorSourcesAreProvenanceOnly': True}
    (output / 'source-change.json').write_text(json.dumps(receipt, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
    print(json.dumps(receipt, ensure_ascii=True))


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--manifest', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--public-base', required=True)
    args = parser.parse_args()
    prepare(args.manifest, args.output, args.public_base)
