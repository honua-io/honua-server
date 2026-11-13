#!/usr/bin/env python3
"""Persist bounded quarantine as one automation-owned issue per project/method."""
import argparse
from datetime import datetime, timezone
import hashlib
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import time

from flake_quarantine import (MAX_AGE, MAX_QUARANTINED, SCHEMA, active_entries,
                              read_cases, retry_replacements, validate_identity)

ISSUES_PER_PAGE = 100
MAX_ISSUE_PAGES = 100
MAX_CANDIDATE_TESTS = 1000
MAX_CANDIDATE_FILES = 1000
MAX_CANDIDATE_BYTES = 64 * 1024
MAX_EVIDENCE_BYTES = 64 * 1024 * 1024
API_READ_ATTEMPTS = 3
API_TIMEOUT_SECONDS = 60
API_RETRY_BACKOFF_SECONDS = 10
MAX_DIAGNOSTIC_CHARS = 500
TOKEN_PATTERN = re.compile(r'\b(gh[pousr]_[A-Za-z0-9]+|github_pat_[A-Za-z0-9_]+)')

LABEL = 'ci-flake'
ACTIVE_LABEL = 'ci-flake-quarantine'
PREFIX = 'Automated flaky-test evidence. Fix the test before closing this issue.\n\n```json\n'


class GitHubApiError(RuntimeError):
    """The GitHub API stayed unavailable; the message carries gh's sanitized stderr."""


def sanitized(stderr):
    return TOKEN_PATTERN.sub('[redacted]', ' '.join(stderr.split()))[-MAX_DIAGNOSTIC_CHARS:]


def api(endpoint, payload=None):
    """Reads retry with backoff; writes run once so a lost response cannot file a duplicate issue."""
    command = ['gh', 'api', endpoint]
    if payload is not None:
        command += ['--method', 'PATCH' if 'state' in payload else 'POST', '--input', '-']
    attempts = API_READ_ATTEMPTS if payload is None else 1
    for attempt in range(1, attempts + 1):
        try:
            result = subprocess.run(command, input=json.dumps(payload) if payload is not None else None,
                                    capture_output=True, text=True, timeout=API_TIMEOUT_SECONDS)
        except subprocess.TimeoutExpired:
            failure = f'timed out after {API_TIMEOUT_SECONDS}s'
        else:
            if result.returncode == 0:
                return json.loads(result.stdout)
            failure = f'exit {result.returncode}: {sanitized(result.stderr)}'
        if attempt < attempts:
            time.sleep(API_RETRY_BACKOFF_SECONDS * attempt)
    raise GitHubApiError(f'gh api {endpoint} failed after {attempts} attempt(s): {failure}')


def issues(label, state='open'):
    repository = os.environ['GITHUB_REPOSITORY']
    found = []
    for page in range(1, MAX_ISSUE_PAGES + 1):
        batch = api(f'repos/{repository}/issues?labels={label}&state={state}&per_page={ISSUES_PER_PAGE}&page={page}')
        found.extend(issue for issue in batch if 'pull_request' not in issue)
        if len(batch) < ISSUES_PER_PAGE:
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
        if evidence.stat().st_size > MAX_EVIDENCE_BYTES:
            raise ValueError('Retry evidence exceeds size limit')
        if hashlib.sha256(evidence.read_bytes()).hexdigest() != candidate[name + '_sha256']:
            raise ValueError('Retry evidence digest mismatch')
    retry_replacements(first, retry)
    failed = {case[0] for case in read_cases(first) if case[2] == 'Failed'}
    tests = candidate['tests']
    if not isinstance(tests, list) or len(tests) > MAX_CANDIDATE_TESTS or set(tests) != failed:
        raise ValueError('Candidates must exactly identify the verified failed methods')
    return tests


def candidate_entries(directory):
    entries = {}
    files = list(directory.rglob('*.flake-candidates.json'))
    if len(files) > MAX_CANDIDATE_FILES:
        raise ValueError('Too many candidate files')
    for path in files:
        if path.stat().st_size > MAX_CANDIDATE_BYTES:
            raise ValueError('Candidate exceeds size limit')
        candidate = json.loads(path.read_text())
        if candidate.get('schema') != 'honua.flake-candidates/v1':
            raise ValueError('Unknown candidate schema')
        for test in verified_tests(path, candidate):
            entry = dict(project=candidate['project'], test=test)
            validate_identity(entry)
            entries[title(entry)] = entry
    if len(entries) > MAX_CANDIDATE_TESTS:
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
    try:
        if options.mode == 'snapshot':
            options.output.write_text(json.dumps(dict(schema=SCHEMA, entries=current_entries())) + '\n')
        else:
            publish(options.results)
    except GitHubApiError as error:
        print(f'::error title=GitHub API unavailable for flaky-test quarantine::{error}', file=sys.stderr)
        sys.exit(1)


if __name__ == '__main__':
    main()
