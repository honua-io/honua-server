#!/usr/bin/env python3
"""Select expensive trailing-CI lanes. Scheduled/full CI remains comprehensive."""
import argparse
import json
from pathlib import Path
import re
import subprocess

ROOT = Path(__file__).resolve().parents[2]
# Native publishing, container publishing and MCP live backends consume the
# runtime dependency graph. Keep all runtime inputs until a proven closure exists.
# AOT publishes only src/Honua.Server, so project files outside src/ (test
# projects, the custom-code harness) cannot change it.
LANES = {
    'aot': r'(^src/|Aot|AOT|[.]rd[.]xml$|scene[-_]fixture[-_]publication)',
    'docker': r'(^src/|^docker/|(^|/)Dockerfile|^scripts/.*docker)',
    'postgres': r'(Postgres|PostgreSql|PostGIS|Postgis|Migrations?/|^src/Honua[.]Db/|^tests/dotnet/Honua[.]Db[.])',
    'browser': r'(^tests/(js|js-browser|shared)/|^src/Honua[.]Server/Features/(Protocols/|FeatureServer|MapServer|Ogc|Styles|Styling|Tiles)|^src/Honua[.]Protocols/|^src/Honua[.]Server/wwwroot/)',
    'python': r'(^tests/python/|^src/Honua[.]Protocols/|^src/Honua[.]Server/Features/(Protocols/|FeatureServer|Ogc))',
    'mcp': r'(^src/|^tests/mcp-certification/|Mcp|MCP|(^|/)mcp/)',
    'quickstart': r'(^samples/|^scripts/.*quickstart|^docker/|(^|/)Dockerfile)',
    # Full CI / Runtime proofs: GP execution, import/export fidelity and the
    # PostGIS store profile build. Server.Tests paths: the proof tests and
    # the helper folders they import.
    'runtime': (r'(^src/|^docker/(worker-gdal|runtime)/'
                r'|^tests/dotnet/Honua[.]Server[.]Tests/([^/]+[.](csproj|json|props|targets)$|(TestData|Infrastructure|Admin|Seed)/'
                r'|Features/(Geoprocessing|Export|Admin)/)'
                r'|^tests/dotnet/Honua[.](Core|Db[.]Postgres)[.]Tests/|^tests/(fixtures|baselines|config|integration)/'
                r'|^tests/python/unit/test_(import_export_fidelity_proof|diff_baselines)[.]py$|^scripts/client-compat/'
                r'|^scripts/ci/(run-import-export-fidelity-proof[.]py|decode-vector-export-proof[.]py'
                r'|fixtures/import-export-fidelity-baseline[.]json|managed-test-build[.]py|prepull-'
                r'|server-test-shard-free-disk[.]sh))'),
}
# ci.yml jobs each lane gates; a composite action selects the lanes whose jobs use it.
LANE_JOBS = {
    'aot': ['aot-build'], 'docker': ['docker-build'], 'postgres': ['postgres-compat'],
    'browser': ['js-integration-tests', 'esri-leaflet-browser-tests', 'maplibre-compat'],
    'python': ['python-integration-tests'], 'mcp': ['mcp-certification', 'mcp-llm-smoke'],
    'quickstart': ['quickstart-console-smoke'], 'runtime': ['runtime-proofs'],
}
ACTION_PATH = re.compile(r'^[.]github/actions/([^/]+)/')
ACTION_USE = re.compile(r'uses:\s*[.]/[.]github/actions/([A-Za-z0-9_.-]+)')
JOB_HEADER = re.compile(r'^  ([A-Za-z0-9_-]+):\s*$', re.MULTILINE)
# Changes to common runtime/contracts/build inputs or shared seeds invalidate
# every compatibility assumption. Unknown top-level inputs also fail closed. The canonical shard
# router independently promotes unscopable dispatches to full CI.
SHARED = re.compile(r'(^src/(Honua[.]Core|Honua[.]ServiceDefaults|Honua[.]Hosting)/|^src/Honua[.]Server/(Hosting|Configuration|Infrastructure|Startup)/|^src/Honua[.]Server/EndpointRegistry[^/]*[.]cs$|^tests/dotnet/Honua[.]TestKit[./]|^tests/seed/|^Directory[.]|^global[.]json$|^NuGet[.]config$|^Honua[.]sln|^eng/|^build/)')
KNOWN = re.compile(r'^(src/|tests/|test/|docs/|scripts/|[.]github/|docker/|samples/|README[.]md$)')


def job_actions(root):
    """Map each ci.yml job to the composite actions it uses, nested uses included."""
    text = (root / '.github/workflows/ci.yml').read_text()
    jobs_text = text[text.index('\njobs:\n'):]
    headers = list(JOB_HEADER.finditer(jobs_text))
    direct = {match.group(1): set(ACTION_USE.findall(jobs_text[match.end():end.start() if end else None]))
              for match, end in zip(headers, headers[1:] + [None])}
    nested = {path.parent.name: set(ACTION_USE.findall(path.read_text()))
              for path in (root / '.github/actions').glob('*/action.y*ml')}
    closure = {}
    for job, actions in direct.items():
        pending, seen = list(actions), set()
        while pending:
            action = pending.pop()
            if action not in seen:
                seen.add(action)
                pending.extend(nested.get(action, ()))
        closure[job] = seen
    return closure, set(nested)


def action_lanes(path, root):
    """Lanes whose jobs use the changed action, or None when that is unknown."""
    try:
        closure, known = job_actions(root)
    except (OSError, ValueError):
        return None
    action = ACTION_PATH.match(path).group(1)
    if action not in known:
        return None
    return {lane for lane, jobs in LANE_JOBS.items() if any(action in closure.get(job, ()) for job in jobs)}


def select(paths, root=ROOT):
    if not paths or any(SHARED.search(p) or not KNOWN.search(p) for p in paths):
        return dict.fromkeys(LANES, True)
    result = {lane: any(re.search(pattern, p) for p in paths) for lane, pattern in LANES.items()}
    for path in filter(ACTION_PATH.match, paths):
        lanes = action_lanes(path, root)
        if lanes is None:
            return dict.fromkeys(LANES, True)
        result.update(dict.fromkeys(lanes, True))
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
