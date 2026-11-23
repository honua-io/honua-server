#!/usr/bin/env python3
"""Select expensive trailing-CI lanes. Scheduled/full CI remains comprehensive."""
import argparse
import json
from pathlib import Path
import re
import subprocess

import yaml

ROOT = Path(__file__).resolve().parents[2]
ACTION_PATH_PREFIX = '.github/actions/'
LOCAL_ACTION_USES_PREFIX = './.github/actions/'
MIN_ACTION_PATH_PARTS = 2

# Native publishing, container publishing and MCP live backends consume the
# runtime dependency graph. Keep all runtime inputs until a proven closure exists.
LANES = {
    'aot': r'(^src/|Aot|AOT|[.]rd[.]xml$|scene[-_]fixture[-_]publication)',
    'docker': r'(^src/|^docker/|(^|/)Dockerfile|^scripts/.*docker)',
    'postgres': r'(Postgres|PostgreSql|PostGIS|Postgis|Migrations?/|^src/Honua[.]Db/|^tests/dotnet/Honua[.]Db[.])',
    'browser': r'(^tests/(js|js-browser|shared)/|^src/Honua[.]Server/Features/(Protocols/|FeatureServer|MapServer|Ogc|Styles|Styling|Tiles)|^src/Honua[.]Protocols/|^src/Honua[.]Server/wwwroot/)',
    'python': r'(^tests/python/|^src/Honua[.]Protocols/|^src/Honua[.]Server/Features/(Protocols/|FeatureServer|Ogc))',
    'mcp': r'(^src/|^tests/mcp-certification/|Mcp|MCP|(^|/)mcp/)',
    'quickstart': r'(^samples/|^scripts/.*quickstart|^docker/|(^|/)Dockerfile)',
    'runtime': (
        r'(^src/|^docker/(worker-gdal|runtime)/|^tests/fixtures/'
        r'|^tests/baselines/client-compat/'
        r'|^docs/gis/(data/client-certification-matrix[.]v1[.]json|CROSS_CLIENT_CERTIFICATION_MATRIX[.]md)$'
        r'|^tests/dotnet/Honua[.]Server[.]Tests/(Features/(Geoprocessing/(Execution|Inference|LocalRunner)|Export/Writers)/'
        r'|Features/Eval/McpQueryWildcardPostgresIntegrationTests[.]cs$'
        r'|(Infrastructure|Hosting|Startup|Seed|TestData)/|DatabaseCollection[.]cs$|DatabaseShardCollections[.]cs$|Honua[.]Server[.]Tests[.]csproj$)'
        r'|^tests/dotnet/Honua[.]Db[.]Postgres[.]Tests/(Features/(Import|Metadata|Security)/|Shared/|DatabaseCollection[.]cs$|CoreMigrationTestFixture[.]cs$|TestCoreSchemaMigrations[.]cs$|Honua[.]Postgres[.]Tests[.]csproj$)'
        r'|^tests/dotnet/Honua[.]Core[.]Tests/(Features/Security/|Fixtures/|Honua[.]Core[.]Tests[.]csproj$)'
        r'|^tests/python/unit/(test_import_export_fidelity_proof|test_diff_baselines)[.]py$'
        r'|^scripts/client-compat/diff-baselines[.]py$'
        r'|^scripts/ci/fixtures/import-export-fidelity-baseline[.]json$'
        r'|^scripts/ci/(run-import-export-fidelity-proof[.]py|decode-vector-export-proof[.]py'
        r'|prepull-testcontainers-postgis[.]sh|prepull-pr-gate-proof-images[.]sh'
        r'|managed-test-build[.]py|server-test-shard-free-disk[.]sh)$)'
    ),
}

LANE_JOBS = {
    'aot': ('aot-build',),
    'docker': ('docker-build',),
    'postgres': ('postgres-compat',),
    'browser': ('js-integration-tests', 'esri-leaflet-browser-tests', 'maplibre-compat'),
    'python': ('python-integration-tests',),
    'mcp': ('mcp-certification',),
    'quickstart': ('quickstart-console-smoke',),
    'runtime': ('runtime-proofs',),
}

# Changes to common runtime/contracts/build inputs or shared seeds invalidate
# every compatibility assumption. Unknown top-level inputs also fail closed. The canonical shard
# router independently promotes unscopable dispatches to full CI.
SHARED = re.compile(r'(^src/(Honua[.]Core|Honua[.]ServiceDefaults|Honua[.]Hosting)/|^src/Honua[.]Server/(Hosting|Configuration|Infrastructure|Startup)/|^src/Honua[.]Server/EndpointRegistry[^/]*[.]cs$|^tests/dotnet/Honua[.]TestKit[./]|^tests/seed/|^Directory[.]|^global[.]json$|^NuGet[.]config$|^Honua[.]sln|^eng/|^build/)')
KNOWN = re.compile(r'^(src/|tests/|test/|docs/|scripts/|[.]github/|docker/|samples/|certification/|README[.]md$)')
# Honua.Ai embeds skills/** as resources, so a skill edit is a src/Honua.Ai edit.
# certification/ feeds the roster workflow and unit and server tests only.
EMBEDDED_SOURCE = (('skills/', 'src/Honua.Ai/skills/'),)


def _load_yaml_mapping(path):
    try:
        data = yaml.load(path.read_text(encoding='utf-8'), Loader=yaml.BaseLoader)
    except (OSError, UnicodeError, yaml.YAMLError):
        return None
    return data if isinstance(data, dict) else None


def _load_action_manifest(repo_root, action_name):
    action_dir = repo_root / '.github/actions' / action_name
    for filename in ('action.yml', 'action.yaml'):
        candidate = action_dir / filename
        if candidate.is_file():
            return _load_yaml_mapping(candidate)
    return None


def _local_action_uses(steps):
    if not isinstance(steps, list):
        return []
    actions = []
    for step in steps:
        if not isinstance(step, dict):
            continue
        uses = step.get('uses')
        if isinstance(uses, str) and uses.startswith(LOCAL_ACTION_USES_PREFIX):
            name = uses[len(LOCAL_ACTION_USES_PREFIX):].strip('/').split('/', 1)[0]
            if name:
                actions.append(name)
    return actions


def _expand_action_uses(direct_actions, repo_root):
    discovered = set()
    queue = list(direct_actions)
    while queue:
        name = queue.pop()
        if name in discovered:
            continue
        manifest = _load_action_manifest(repo_root, name)
        if manifest is None:
            return None
        discovered.add(name)
        runs = manifest.get('runs')
        if isinstance(runs, dict):
            queue.extend(_local_action_uses(runs.get('steps')))
    return discovered


def _lane_action_map(repo_root):
    workflow = _load_yaml_mapping(repo_root / '.github/workflows/ci.yml')
    if workflow is None or not isinstance(workflow.get('jobs'), dict):
        return None
    jobs = workflow['jobs']
    lane_actions = {}
    for lane, job_ids in LANE_JOBS.items():
        direct = []
        for job_id in job_ids:
            job = jobs.get(job_id)
            if not isinstance(job, dict):
                return None
            direct.extend(_local_action_uses(job.get('steps')))
        expanded = _expand_action_uses(direct, repo_root)
        if expanded is None:
            return None
        lane_actions[lane] = expanded
    return lane_actions


def _changed_action_name(path):
    rest = path[len(ACTION_PATH_PREFIX):]
    parts = [part for part in rest.split('/') if part]
    if len(parts) < MIN_ACTION_PATH_PARTS:
        return None
    return parts[0]


def _resolve_action_lanes(action_paths, repo_root):
    lane_actions = _lane_action_map(repo_root)
    if lane_actions is None:
        return None
    selected = set()
    for path in action_paths:
        name = _changed_action_name(path)
        if name is None or _load_action_manifest(repo_root, name) is None:
            return None
        for lane, actions in lane_actions.items():
            if name in actions:
                selected.add(lane)
    return selected


def _as_source_path(path):
    for prefix, source in EMBEDDED_SOURCE:
        if path.startswith(prefix):
            return source + path[len(prefix):]
    return path


def select(paths, repo_root=ROOT):
    if not paths:
        return dict.fromkeys(LANES, True)
    paths = [_as_source_path(p) for p in paths]
    action_paths = [p for p in paths if p.startswith(ACTION_PATH_PREFIX)]
    other_paths = [p for p in paths if not p.startswith(ACTION_PATH_PREFIX)]
    if any(SHARED.search(p) or not KNOWN.search(p) for p in other_paths):
        return dict.fromkeys(LANES, True)
    result = {lane: any(re.search(pattern, p) for p in other_paths) for lane, pattern in LANES.items()}
    if action_paths:
        action_lanes = _resolve_action_lanes(action_paths, Path(repo_root))
        if action_lanes is None:
            return dict.fromkeys(LANES, True)
        for lane in action_lanes:
            result[lane] = True
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
