"""Export the pinned three-server baselines to standard portable distributions."""
import argparse
import json
from pathlib import Path
from pack_distribution import ROOT, build

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--instances', nargs='+', choices=['m3e', 'dc2', 'mb'], default=['m3e', 'dc2', 'mb'])
parser.add_argument('--output', type=Path, default=ROOT / 'artifacts/distributions')
parser.add_argument('--draft', action='store_true', help='Local review only; unhosted fallback references remain blocked')
args = parser.parse_args()
config = json.loads((ROOT / 'packs/distributions.json').read_text(encoding='utf-8'))
failed = False
for instance in args.instances:
    report = build(instance, config, args.output, args.draft)
    print(json.dumps({k: v for k, v in report.items() if k not in ('blockers', 'fallbackObjects')}, ensure_ascii=False), flush=True)
    failed |= not report['candidate']
raise SystemExit(1 if failed and not args.draft else 0)
