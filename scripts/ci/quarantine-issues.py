#!/usr/bin/env python3
"""Persist bounded quarantine as one automation-owned issue per project/method."""
import argparse
from datetime import datetime, timezone
import hashlib
import json
import os
from pathlib import Path
import subprocess

from flake_quarantine import (MAX_AGE, MAX_QUARANTINED, SCHEMA, active_entries,
                              read_cases, retry_replacements, validate_identity)

LABEL = 'ci-flake'
ACTIVE_LABEL = 'ci-flake-quarantine'
PREFIX = 'Automated flaky-test evidence. Fix the test before closing this issue.\n\n```json\n'


def api(endpoint, payload=None):
    command = ['gh', 'api', endpoint]
    if payload is not None:
        command += ['--method', 'PATCH' if 'state' in payload else 'POST', '--input', '-']
    result = subprocess.run(command, input=json.dumps(payload) if payload is not None else None,
                            capture_output=True, text=True, check=True)
    return json.loads(result.stdout)


def issues(label, state='open'):
    repository = os.environ['GITHUB_REPOSITORY']
    found = []
    for page in range(1, 101):
        batch = api(f'repos/{repository}/issues?labels={label}&state={state}&per_page=100&page={page}')
        found.extend(issue for issue in batch if 'pull_request' not in issue)
        if len(batch) < 100:
            return found
    raise ValueError('Issue inventory exceeded bounded pagination')


def decode(issue):
    if issue['user']['login'] != 'github-actions[bot]':
        raise ValueError('Quarantine issue must be automation-owned')
    body = issue.get('body') or ''
    if not body.startswith(PREFIX) or not body.endswith('\n```\n'):
        raise ValueError('Malformed quarantine issue metadata')
    metadata = json.loads(body[len(PREFIX):-5])
    if metadata.get('schema') != SCHEMA:
        raise ValueError('Unknown quarantine issue schema')
    entry = {key: metadata[key] for key in ('project', 'test', 'quarantined_at', 'expires_at')}
    entry['issue'] = issue['number']
    return entry


def current_entries():
    return active_entries([decode(issue) for issue in issues(ACTIVE_LABEL)])


def title(entry):
    digest = hashlib.sha256((entry['project'] + '\n' + entry['test']).encode()).hexdigest()
    return f'ci: flaky test [{digest}]'


def verified_tests(path, candidate):
    stem = path.name.removesuffix('.flake-candidates.json')
    first = path.with_name(stem + '.first-attempt.xml')
    retry = path.with_name(stem + '.retry-attempt.xml')
    for name, evidence in (('first', first), ('retry', retry)):
        if evidence.stat().st_size > 64 * 1024 * 1024:
            raise ValueError('Retry evidence exceeds size limit')
        if hashlib.sha256(evidence.read_bytes()).hexdigest() != candidate[name + '_sha256']:
            raise ValueError('Retry evidence digest mismatch')
    retry_replacements(first, retry)
    failed = {case[0] for case in read_cases(first) if case[2] == 'Failed'}
    tests = candidate['tests']
    if not isinstance(tests, list) or len(tests) > 1000 or set(tests) != failed:
        raise ValueError('Candidates must exactly identify the verified failed methods')
    return tests


def candidate_entries(directory):
    entries = {}
    files = list(directory.rglob('*.flake-candidates.json'))
    if len(files) > 1000:
        raise ValueError('Too many candidate files')
    for path in files:
        if path.stat().st_size > 65536:
            raise ValueError('Candidate exceeds size limit')
        candidate = json.loads(path.read_text())
        if candidate.get('schema') != 'honua.flake-candidates/v1':
            raise ValueError('Unknown candidate schema')
        for test in verified_tests(path, candidate):
            entry = dict(project=candidate['project'], test=test)
            validate_identity(entry)
            entries[title(entry)] = entry
    if len(entries) > 1000:
        raise ValueError('Too many candidate tests')
    return entries


def ensure_labels():
    repository = os.environ['GITHUB_REPOSITORY']
    for name in (LABEL, ACTIVE_LABEL):
        subprocess.run(['gh', 'label', 'create', name, '--repo', repository, '--color', 'fbca04', '--force'],
                       capture_output=True, text=True, check=True)


def issue_body(entry, run_url):
    now = datetime.now(timezone.utc)
    metadata = dict(schema=SCHEMA, **entry, quarantined_at=now.isoformat(),
                    expires_at=(now + MAX_AGE).isoformat(), evidence=run_url)
    return PREFIX + json.dumps(metadata, sort_keys=True) + '\n```\n'


def publish(directory):
    candidates = candidate_entries(directory)
    if not candidates:
        return
    ensure_labels()
    inventory = issues(LABEL, 'all')
    known = {issue['title']: issue for issue in inventory if issue['user']['login'] == 'github-actions[bot]'}
    active = current_entries()
    active_keys = {title(entry) for entry in active}
    repository = os.environ['GITHUB_REPOSITORY']
    run_url = os.environ['QUARANTINE_EVIDENCE_URL']
    for key, entry in sorted(candidates.items()):
        if key in active_keys:
            continue
        labels = [LABEL]
        if len(active_keys) < MAX_QUARANTINED:
            labels.append(ACTIVE_LABEL)
            active_keys.add(key)
        payload = dict(title=key, body=issue_body(entry, run_url), labels=labels)
        existing = known.get(key)
        endpoint = f'repos/{repository}/issues'
        if existing:
            endpoint += f'/{existing["number"]}'
            payload['state'] = 'open'
        api(endpoint, payload)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    sub = parser.add_subparsers(dest='mode', required=True)
    snapshot = sub.add_parser('snapshot')
    snapshot.add_argument('--output', required=True, type=Path)
    publication = sub.add_parser('publish')
    publication.add_argument('--results', required=True, type=Path)
    options = parser.parse_args()
    if options.mode == 'snapshot':
        options.output.write_text(json.dumps(dict(schema=SCHEMA, entries=current_entries())) + '\n')
    else:
        publish(options.results)


if __name__ == '__main__':
    main()
