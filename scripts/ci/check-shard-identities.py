#!/usr/bin/env python3
"""Require unique shard names and unique filters within each test assembly."""
import argparse
import json
from pathlib import Path
import sys

from shard_projects import shard_projects


def find_collisions(shards):
    if not isinstance(shards, list) or not shards:
        raise ValueError('shards must be a nonempty array')
    names = set()
    owners = {}
    errors = []
    for shard in shards:
        if not isinstance(shard, dict):
            raise ValueError('each shard must be an object')
        for field in ('name', 'filter'):
            if not isinstance(shard.get(field), str) or not shard[field].strip():
                raise ValueError(f'shard {field} must be a nonempty string')
        name = shard['name']
        if name in names:
            errors.append(f'duplicate shard name {name!r}')
        names.add(name)
        for project in shard_projects(shard):
            identity = (project, shard['filter'].strip())
            if identity in owners:
                errors.append(f'duplicate shard filter in {project}: {owners[identity]!r} and {name!r}: {identity[1]!r}')
            else:
                owners[identity] = name
    return errors


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('config', nargs='?', default='.github/ci-shards.json')
    args = parser.parse_args()
    try:
        config = json.loads(Path(args.config).read_text())
        errors = find_collisions(config['shards'])
    except (OSError, ValueError, KeyError, TypeError) as error:
        print(f'Invalid shard inventory: {error}', file=sys.stderr)
        return 1
    for error in errors:
        print(f'Shard inventory collision: {error}', file=sys.stderr)
    if not errors:
        print(f'Shard identities valid: {len(config["shards"])} shards')
    return int(bool(errors))


if __name__ == '__main__':
    sys.exit(main())
