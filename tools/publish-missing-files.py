"""Automatically host missing upstream files from the supplied client baselines."""
import argparse
import json
from fallback_publication import publish_missing
from pack_distribution import ROOT

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--instances', nargs='+', choices=['m3e', 'dc2', 'mb'], default=['m3e', 'dc2', 'mb'])
args = parser.parse_args()
publish_missing(json.loads((ROOT / 'packs/distributions.json').read_text(encoding='utf-8')), args.instances)
