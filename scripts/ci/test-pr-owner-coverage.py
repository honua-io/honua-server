#!/usr/bin/env python3
"""Reject source paths that lose their last budgeted PR shard owner."""
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


def verify(config, debt):
    uncovered = missing_owners(config)
    regressions = uncovered - set(debt)
    assert not regressions, f'Source paths without a budgeted PR owner: {sorted(regressions)}'
    return uncovered


def reject_loss(config, debt):
    covered = {path for shard in config['shards'] for path in shard['paths']} - set(debt)
    path = min(covered)
    broken = copy.deepcopy(config)
    for shard in broken['shards']:
        if path in shard['paths']:
            shard['dispatch_rank'] = config['pr_time_budget']['max_shard_minutes'] + 0.1
    try:
        verify(broken, debt)
    except AssertionError:
        return
    raise AssertionError('Accepted loss of the last budgeted source-path owner')


config = json.loads((ROOT / '.github/ci-shards.json').read_text())
debt = json.loads((ROOT / 'scripts/ci/fixtures/pr-owner-coverage-debt.json').read_text())['paths']
assert len(set(debt)) == len(debt), 'Duplicate coverage debt path'
uncovered = verify(config, debt)
assert uncovered == set(debt), (
    'Debt list is stale: '
    f'extra={sorted(set(debt) - uncovered)} '
    f'missing={sorted(uncovered - set(debt))}')
reject_loss(config, debt)
print(f'pr-owner-coverage=ok existing-uncovered-paths={len(uncovered)} regressions=0')
