#!/usr/bin/env python3
"""Annotate existing shard selections; uncertain ownership retains PostGIS."""
import argparse
import json
import sys
from pathlib import Path

MONOLITH = 'tests/dotnet/Honua.Server.Tests/Honua.Server.Tests.csproj'


def annotate(matrix, catalog):
    projects = catalog.get('projects', {}) if isinstance(catalog, dict) else {}
    if not isinstance(projects, dict):
        projects = {}
    result = []
    for shard in matrix:
        entry = projects.get(shard.get('csproj') or MONOLITH)
        free = (isinstance(entry, dict) and entry.get('role') in {'unit', 'component'}
                and type(entry.get('resources')) is list and entry['resources'] == [])
        result.append({**shard, 'needs_postgres': not free})
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--catalog', type=Path, required=True)
    parser.add_argument('--matrix-json', help='Small inline input; default reads JSON from stdin')
    args = parser.parse_args()
    # Missing or malformed metadata conservatively retains the existing service.
    try:
        catalog = json.loads(args.catalog.read_text())
    except (OSError, ValueError):
        catalog = {}
    matrix = json.loads(args.matrix_json) if args.matrix_json is not None else json.load(sys.stdin)
    print(json.dumps(annotate(matrix, catalog)))


if __name__ == '__main__':
    main()
