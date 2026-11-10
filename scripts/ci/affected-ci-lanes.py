#!/usr/bin/env python3
"""Select expensive trailing-CI lanes. Scheduled/full CI remains comprehensive."""
import argparse
import json
import re
import subprocess

LANES = {
    'aot': r'(^eng/aot/|Aot|AOT|[.]rd[.]xml$|[.]csproj$|^src/Honua[.]Server/Program[.]cs$)',
    'docker': r'(^docker/|(^|/)Dockerfile|^scripts/.*docker|^src/Honua[.]Server/Program[.]cs$)',
    'postgres': r'(Postgres|PostgreSql|PostGIS|Postgis|Migrations?/|^src/Honua[.]Db/|^tests/dotnet/Honua[.]Db[.])',
    'browser': r'(^tests/(js|js-browser|shared)/|^src/Honua[.]Server/Features/(Protocols/|FeatureServer|MapServer|Ogc|Styles|Styling|Tiles)|^src/Honua[.]Protocols/|^src/Honua[.]Server/wwwroot/)',
    'python': r'(^tests/python/|^src/Honua[.]Protocols/|^src/Honua[.]Server/Features/(Protocols/|FeatureServer|Ogc))',
    'mcp': r'(^src/Honua[.]Ai/|^tests/mcp-certification/|Mcp|MCP|(^|/)mcp/)',
    'quickstart': r'(^samples/|^scripts/.*quickstart|^docker/|(^|/)Dockerfile)',
}
# Changes to common runtime/contracts/build inputs or shared seeds invalidate
# every compatibility assumption. Unknown top-level inputs also fail closed. The canonical shard
# router independently promotes unscopable dispatches to full CI.
SHARED = re.compile(r'(^[.]github/actions/|^src/(Honua[.]Core|Honua[.]ServiceDefaults|Honua[.]Hosting)/|^src/Honua[.]Server/(Hosting|Configuration|Infrastructure|Startup)/|^src/Honua[.]Server/EndpointRegistry[^/]*[.]cs$|^tests/dotnet/Honua[.]TestKit[./]|^tests/seed/|^Directory[.]|^global[.]json$|^NuGet[.]config$|^Honua[.]sln|^eng/|^build/)')
KNOWN = re.compile(r'^(src/|tests/|test/|docs/|scripts/|[.]github/|docker/|samples/|README[.]md$)')


def select(paths):
    if not paths or any(SHARED.search(p) or not KNOWN.search(p) for p in paths):
        return dict.fromkeys(LANES, True)
    result = {lane: any(re.search(pattern, p) for p in paths) for lane, pattern in LANES.items()}
    if result['aot'] or result['mcp']:
        result['postgres'] = True
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--range', required=True)
    parser.add_argument('--selective', choices=['true', 'false'], required=True)
    args = parser.parse_args()
    result = dict.fromkeys(LANES, True)
    if args.selective == 'true':
        try:
            paths = subprocess.check_output(['git', 'diff', '--no-renames', '--name-only', args.range], text=True).splitlines()
            result = select(paths)
        except subprocess.CalledProcessError:
            pass  # Missing diff evidence must never suppress a compatibility lane.
    print('lanes=' + json.dumps(result, separators=(',', ':')))


if __name__ == '__main__':
    main()
