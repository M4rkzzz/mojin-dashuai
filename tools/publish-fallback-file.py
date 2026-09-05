"""Publish one exact file from the supplied baseline; notices are retained automatically."""
import argparse
import hashlib
import json
from pathlib import Path
from fallback_publication import OPERATOR_BASIS, publish
from pack_distribution import ROOT

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('instance', choices=['m3e', 'dc2', 'mb'])
parser.add_argument('path', help='Exact relative path in the source audit')
parser.add_argument('--file', type=Path, required=True)
parser.add_argument('--basis', default=OPERATOR_BASIS, help='Optional additional source information')
parser.add_argument('--notice', type=Path, help='Optional accompanying notice; defaults to embedded notices')
parser.add_argument('--publish', action='store_true')
args = parser.parse_args()
audit = json.loads((ROOT / f'packs/{args.instance}-source-audit.json').read_text(encoding='utf-8'))
matches = [r for r in audit['files'] if r['path'] == args.path]
if len(matches) != 1:
    raise SystemExit('Expected one pinned audit entry')
row = matches[0]
data = args.file.read_bytes()
if len(data) != row['size'] or hashlib.sha256(data).hexdigest() != row['sha256']:
    raise SystemExit('Fallback differs from the pinned file')
if args.publish:
    publish([{'instance': args.instance, 'row': row, 'file': args.file, 'basis': args.basis,
              'notice': args.notice.read_bytes() if args.notice else None}])
else:
    print(json.dumps({'instance': args.instance, 'path': row['path'], 'size': row['size'],
                      'url': 'https://launcher.boshan.uk/objects/' + row['sha256'] + '.jar', 'published': False}))
