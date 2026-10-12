#!/usr/bin/env python3
"""Affected local feedback using the canonical unit planner and shard router; not a PR gate."""
import argparse
import importlib.util
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import xml.etree.ElementTree as ET
import uuid

ROOT = Path(__file__).resolve().parents[2]
MONOLITH = 'tests/dotnet/Honua.Server.Tests/Honua.Server.Tests.csproj'


def planner(root):
    spec = importlib.util.spec_from_file_location('feedback_catalog', root / 'scripts/ci/test-projects.py')
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def changed_files(root, base):
    # Match test-projects.py: base-to-working-tree diff includes staged/unstaged changes.
    paths = subprocess.check_output(['git', 'diff', '--no-renames', '--name-only', '-z', base], cwd=root).decode().split('\0')
    paths += subprocess.check_output(['git', 'ls-files', '--others', '--exclude-standard', '-z'], cwd=root).decode().split('\0')
    return sorted(set(filter(None, paths)))


def route(root, changed):
    if not changed:
        return {'run_all': False, 'shards': [], 'reason': 'no_changes'}
    return json.loads(subprocess.check_output(
        ['bash', 'scripts/ci/honua-server-targeted-tests.sh', '--stdin'],
        input='\n'.join(changed), text=True, cwd=root))


def make_plan(catalog, unit_plan, routing, registry, changed):
    if not isinstance(routing, dict) or type(routing.get('run_all')) is not bool:
        raise ValueError('Canonical router must report a boolean run_all')
    if not isinstance(routing.get('shards'), list) or any(not isinstance(n, str) or not n for n in routing['shards']):
        raise ValueError('Canonical router must report a shard-name list')
    units = sorted(set(unit_plan['selectedProjects']))
    known = catalog['projects']
    shards = []
    skips = []
    requested = set(routing.get('shards', []))
    names = {s['shard_name'] for s in registry['shards']}
    # Router identifiers can be registry name or display shard_name.
    aliases = {s.get('name', s['shard_name']): s['shard_name'] for s in registry['shards']}
    requested = {aliases.get(n, n) for n in requested}
    if not routing.get('run_all') and requested - names:
        raise ValueError('Unknown routed shards: ' + ', '.join(sorted(requested - names)))
    for shard in registry['shards']:
        name = shard['shard_name']
        if not routing.get('run_all') and name not in requested:
            skips.append({'shard': name, 'reason': 'not selected by canonical router'})
            continue
        project = shard.get('csproj') or MONOLITH
        entry = known[project]
        if entry['role'] == 'architecture':
            raise ValueError('Architecture project cannot enter feedback selection')
        if project in units:
            skips.append({'shard': name, 'reason': 'selected isolated unit full suite covers this shard'})
            continue
        expression = shard.get('filter')
        if not expression:
            raise ValueError('Selected shard has no filter: ' + name)
        effective = '(' + expression + ')&Tier!=Slow'
        if entry['role'] not in {'unit', 'component'}:
            effective = '(' + effective + ')&Tier!=Fast'
        shards.append({'name': name, 'project': project, 'filter': expression,
                       'effectiveFilter': effective, 'resources': entry['resources'],
                       'timeoutMinutes': shard.get('test_timeout_minutes', shard.get('timeout_minutes')),
                       'maxCpuCount': shard.get('max_cpu_count')})
    projects = sorted(set(units) | {s['project'] for s in shards})
    for project in units:
        if known[project]['role'] != 'unit' or not known[project]['isolated']:
            raise ValueError('Unit planner selected a non-isolated project: ' + project)
    return {'schema': 'honua.local-test-feedback/v1', 'feedbackOnly': True,
            'validatesPullRequest': False, 'fullGatesRequired': True,
            'changedFiles': changed, 'projects': projects, 'units': units,
            'unitExecutions': [{'project': p, 'filter': None, 'scope': 'full suite',
                                'resources': known[p]['resources']} for p in units],
            'shards': shards,
            'resources': sorted({r for p in projects for r in known[p]['resources']}),
            'selectionReasons': unit_plan.get('selectionReasons', []) + [routing.get('reason', 'canonical router')],
            'skips': skips, 'emptySelection': not projects}


def require_tests(directory):
    reports = list(Path(directory).glob('*.trx'))
    total = 0
    executed = 0
    for report in reports:
        for node in ET.parse(report).getroot().iter():
            if node.tag.rsplit('}', 1)[-1] == 'Counters':
                total += int(node.attrib.get('total', '0'))
                executed += int(node.attrib.get('executed', '0'))
    if total <= 0 or executed <= 0:
        raise ValueError('No tests recorded in fresh TRX results: ' + str(directory))


def execute(root, plan, run=subprocess.run, results_directory=None):
    if not plan['projects']:
        print('No affected tests selected; no validation verdict was produced.', file=sys.stderr)
        return
    results_root = (Path(results_directory) if results_directory else root / 'tests/TestResults/local-feedback').resolve()
    results = results_root / uuid.uuid4().hex
    results.mkdir(parents=True, exist_ok=False)
    (results / 'plan.json').write_text(json.dumps(plan, indent=2) + '\n')
    status = {'feedbackOnly': True, 'validatesPullRequest': False, 'fullGatesRequired': True,
              'status': 'running', 'source': plan.get('source'), 'commands': []}
    def invoke(command, **kwargs):
        index = len(status['commands'])
        log = results / ('command-' + str(index) + '.log')
        status['commands'].append({'command': command, 'log': str(log), 'status': 'running'})
        print('Feedback command log: ' + str(log), flush=True)
        with log.open('w') as output:
            run(command, stdout=output, stderr=subprocess.STDOUT, **kwargs)
        status['commands'][-1]['status'] = 'passed'
    # PATH dotnet preserves the supervisor/Flow admission wrapper.
    try:
        (results / 'result.json').write_text(json.dumps(status, indent=2) + '\n')
        with tempfile.TemporaryDirectory(prefix='.honua-local-feedback-', dir=root) as temporary:
            temporary = Path(temporary)
            selection = temporary / 'feedback.slnf'
            selection.write_text(json.dumps({'solution': {'path': str(root / 'Honua.sln'), 'projects': plan['projects']}}))
            invoke(['dotnet', 'build', str(selection), '--configuration', 'Release',
                 '-p:TreatWarningsAsErrors=true', '-p:UseSharedCompilation=false'], cwd=root, check=True)
            for index, project in enumerate(plan['units']):
                directory = results / ('unit-' + str(index))
                directory.mkdir()
                invoke(['dotnet', 'test', project, '--no-build', '--no-restore', '--configuration', 'Release',
                     '--logger', 'trx;LogFileName=feedback.trx', '--results-directory', str(directory)], cwd=root, check=True)
                require_tests(directory)
            for index, shard in enumerate(plan['shards']):
                directory = results / ('shard-' + str(index))
                directory.mkdir()
                env = os.environ.copy()
                env.update({'HONUA_SERVER_TEST_SHARD_NAME': shard['name'], 'HONUA_SERVER_TEST_CSPROJ': shard['project'],
                            'HONUA_SERVER_TEST_FILTER': shard['filter'], 'HONUA_SERVER_TEST_LOG_NAME': 'feedback',
                            'HONUA_SERVER_TEST_CONFIGURATION': 'Release', 'HONUA_SERVER_TEST_RESULTS_DIR': str(directory),
                            'HONUA_SERVER_TEST_EXCLUDE_SLOW': 'true', 'HONUA_SERVER_TEST_EXCLUDE_FAST': 'true'})
                for key, value in [('HONUA_SERVER_TEST_TIMEOUT_MINUTES', shard['timeoutMinutes']),
                                   ('HONUA_SERVER_TEST_MAX_CPU_COUNT', shard['maxCpuCount'])]:
                    if value is not None:
                        env[key] = str(value)
                    else:
                        env.pop(key, None)
                invoke(['bash', 'scripts/ci/run-server-test-shard.sh'], cwd=root, check=True, env=env)
                require_tests(directory)
        status['status'] = 'passed'
    except (Exception, KeyboardInterrupt) as error:
        status['status'] = 'cancelled' if isinstance(error, KeyboardInterrupt) else 'failed'
        status['error'] = str(error)
        if status['commands'] and status['commands'][-1]['status'] == 'running':
            status['commands'][-1]['status'] = 'failed'
        raise
    finally:
        (results / 'result.json').write_text(json.dumps(status, indent=2) + '\n')
    return results


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('operation', choices=['plan', 'run'])
    parser.add_argument('--root', type=Path, default=ROOT)
    parser.add_argument('--base', default='origin/trunk')
    parser.add_argument('--dry-run', action='store_true')
    parser.add_argument('--results-directory', type=Path, help='Parent for a unique retained feedback run directory')
    args = parser.parse_args()
    root = args.root.resolve()
    module = planner(root)
    catalog = json.loads((root / '.github/test-projects.json').read_text())
    module.validate(root, catalog)
    changed = changed_files(root, args.base)
    unit_plan = module.plan(root, catalog, changed)
    routing = route(root, changed)
    result = make_plan(catalog, unit_plan, routing, json.loads((root / '.github/ci-shards.json').read_text()), changed)
    result['source'] = {'base': args.base,
                        'baseHead': subprocess.check_output(['git', 'rev-parse', args.base], cwd=root, text=True).strip(),
                        'head': subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=root, text=True).strip(),
                        'workingTreeIncluded': True}
    # Check the combined forward closure, not just the direct test project names.
    for project in result['projects']:
        for dependency in module.closure(root, project):
            if catalog['projects'].get(dependency, {}).get('role') == 'architecture':
                raise ValueError('Architecture dependency cannot enter feedback: ' + dependency)
    print(json.dumps(result, indent=2), flush=True)
    if args.operation == 'run' and not args.dry_run:
        execute(root, result, results_directory=args.results_directory)
        print('Feedback execution finished. Full PR gates remain required.')


if __name__ == '__main__':
    try:
        main()
    except (ValueError, OSError, KeyError, ET.ParseError, subprocess.CalledProcessError) as error:
        print(str(error), file=sys.stderr)
        sys.exit(2)
