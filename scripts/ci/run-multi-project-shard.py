#!/usr/bin/env python3
"""Execute grouped assemblies with independent, fail-closed TRX checks."""
from datetime import datetime, timezone
import json
import os
from pathlib import Path
import signal
import subprocess
import time
import xml.etree.ElementTree as ET

from shard_projects import shard_projects

SCRIPT = Path(__file__).resolve().parent
NS = 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'


def utc_now():
    return datetime.now(timezone.utc).isoformat().replace('+00:00', 'Z')


def combine_results(paths, output):
    root = ET.Element('TestRun', xmlns=NS)
    sections = {name: ET.SubElement(root, name)
                for name in ('Results', 'TestDefinitions', 'TestEntries')}
    totals = dict.fromkeys(('total', 'executed', 'passed', 'failed'), 0)
    for path in paths:
        tree = ET.parse(path)
        for name, section in sections.items():
            source = tree.find(f'{{*}}{name}')
            if source is not None:
                section.extend(source)
        counters = tree.find('.//{*}ResultSummary/{*}Counters')
        for key in totals:
            totals[key] += int(counters.attrib[key])
    ET.SubElement(ET.SubElement(root, 'ResultSummary'), 'Counters',
                  **{key: str(value) for key, value in totals.items()})
    ET.ElementTree(root).write(output, encoding='utf-8', xml_declaration=True)


def execute(environment, remaining):
    with subprocess.Popen(['bash', str(SCRIPT / 'run-server-test-shard.sh')],
                          env=environment, start_new_session=True) as process:
        try:
            return process.wait(timeout=remaining)
        except subprocess.TimeoutExpired:
            try:
                os.killpg(process.pid, signal.SIGKILL)
            except ProcessLookupError:
                pass
            process.wait()
            return 124


def run_projects(projects, environment, directory):
    deadline = time.monotonic() + float(environment['HONUA_SERVER_TEST_TIMEOUT_MINUTES']) * 60
    attempts = []
    for stale in directory.glob("project-*.*"):
        stale.unlink()
    for index, project in enumerate(projects):
        remaining = deadline - time.monotonic()
        if remaining <= 0:
            return attempts, 124
        for suffix in ('.trx', '.timing.json'):
            (directory / f'project-{index}{suffix}').unlink(missing_ok=True)
        child = {**environment, 'HONUA_SERVER_TEST_CSPROJS': '',
                 'HONUA_SERVER_TEST_CSPROJ': project,
                 'HONUA_SERVER_TEST_RESULTS_DIR': str(directory),
                 'HONUA_SERVER_TEST_LOG_NAME': f'project-{index}',
                 'HONUA_SERVER_TEST_TIMEOUT_MINUTES': str(max(1, int(remaining / 60)))}
        code = execute(child, remaining)
        timing = directory / f'project-{index}.timing.json'
        record = json.loads(timing.read_text()) if timing.is_file() else {'status': 'missing'}
        if code != 0:
            record['status'] = 'failed'
        attempts.append(record)
        if code == 124:
            return attempts, code
    return attempts, 0


def execution_evidence(projects, environment, results):
    name = environment['HONUA_SERVER_TEST_LOG_NAME']
    output = results / f'{name}.trx'
    output.unlink(missing_ok=True)
    try:
        combine_results([results / name / f'project-{i}.trx' for i in range(len(projects))], output)
    except (OSError, ValueError, AttributeError, ET.ParseError):
        output.unlink(missing_ok=True)
    evidence = subprocess.run(['python3', str(SCRIPT / 'shard-execution-evidence.py'),
                               '--trx', str(output), '--label', environment['HONUA_SERVER_TEST_SHARD_NAME'],
                               '--project', json.dumps(projects)], capture_output=True, text=True, check=False)
    return evidence.returncode, json.loads(evidence.stdout)


def effective_filter(environment):
    expression = environment['HONUA_SERVER_TEST_FILTER']
    if environment.get('HONUA_SERVER_TEST_EXCLUDE_SLOW', 'true') == 'true':
        expression = f'({expression})&Tier!=Slow'
    return expression


def timing_record(environment, started, outcome):
    budget = float(environment['HONUA_SERVER_TEST_TIMEOUT_MINUTES']) * 60
    duration = time.monotonic() - started[0]
    utilization = duration / budget
    policy = Path(environment.get('HONUA_SERVER_TEST_SHARD_POLICY_FILE', '.github/ci-shards.json'))
    threshold = json.loads(policy.read_text())['shard_budget_policy']['warn_utilization']
    threshold = float(environment.get('HONUA_SERVER_TEST_HEADROOM_WARN_RATIO', threshold))
    passed, code, evidence = outcome
    return {'shard': environment['HONUA_SERVER_TEST_SHARD_NAME'],
            'log_name': environment['HONUA_SERVER_TEST_LOG_NAME'],
            'status': 'passed' if passed else 'failed', 'exit_code': 0 if passed else (code or 1),
            'started_at': started[1], 'completed_at': utc_now(), 'duration_seconds': duration,
            'timeout_minutes': budget / 60, 'timeout_seconds': budget,
            'timed_out': code == 124, 'headroom_ratio': utilization,
            'capacity_status': ('low_headroom' if utilization >= threshold else 'ok') if passed else 'not_assessed',
            'filter': effective_filter(environment),
            'projects': json.loads(environment['HONUA_SERVER_TEST_CSPROJS']), 'execution': evidence}


def main():
    environment = dict(os.environ)
    projects = shard_projects({'csprojs': json.loads(environment['HONUA_SERVER_TEST_CSPROJS'])})
    catalog = json.loads(Path('.github/test-projects.json').read_text())['projects']
    if any(catalog.get(p, {}).get('role') not in ('unit', 'component')
           or catalog[p].get('resources') != [] for p in projects):
        raise ValueError('Grouped shards require catalogued resource-free unit/component assemblies')
    results = Path(environment.get('HONUA_SERVER_TEST_RESULTS_DIR', './artifacts/test-results')).resolve()
    name = environment['HONUA_SERVER_TEST_LOG_NAME']
    directory = results / name
    directory.mkdir(parents=True, exist_ok=True)
    started = time.monotonic(), utc_now()
    attempts, code = run_projects(projects, environment, directory)
    passed = len(attempts) == len(projects) and all(a.get('status') == 'passed' for a in attempts)
    evidence_code, evidence = execution_evidence(projects, environment, results)
    outcome = passed and evidence_code == 0 and code == 0, code, evidence
    timing = timing_record(environment, started, outcome)
    (results / f'{name}.timing.json').write_text(json.dumps(timing) + '\n')
    return timing['exit_code']


if __name__ == '__main__':
    raise SystemExit(main())
