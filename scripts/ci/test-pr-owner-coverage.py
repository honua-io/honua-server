#!/usr/bin/env python3
"""Reject source paths whose every shard owner is over the PR time budget."""
import copy
import json
from collections import defaultdict
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]


def missing_owners(config):
    owners = defaultdict(list)
    for shard in config['shards']:
        for path in shard['paths']:
            owners[path].append(shard)
    limit = config['pr_time_budget']['max_shard_minutes']
    return {path for path, shards in owners.items()
            if all(shard['dispatch_rank'] > limit for shard in shards)}


def reject_loss(config):
    path = min(path for shard in config['shards'] for path in shard['paths'])
    broken = copy.deepcopy(config)
    for shard in broken['shards']:
        if path in shard['paths']:
            shard['dispatch_rank'] = config['pr_time_budget']['max_shard_minutes'] + 0.1
    if path not in missing_owners(broken):
        raise AssertionError('Accepted loss of the last budgeted source-path owner')


config = json.loads((ROOT / '.github/ci-shards.json').read_text())
uncovered = missing_owners(config)
assert not uncovered, f'Source paths without a budgeted PR owner: {sorted(uncovered)}'
reject_loss(config)
print('pr-owner-coverage=ok uncovered-paths=0')
