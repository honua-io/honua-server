#!/usr/bin/env python3
"""Read-only generated-output admission, executed from the immutable PR base.

An allowlisted diff alone is insufficient. Only a completed canonical nightly
producer's artifact can attest the exact output commit/tree and source parent.
Every unknown/error returns false, keeping ordinary checks enabled. No candidate
script, allowlist, branch name, author, label or commit trailer supplies policy.
"""
from __future__ import annotations

import argparse
import io
import json
import os
from pathlib import Path
import re
import subprocess
import time
import zipfile

REPOSITORY = 'honua-io/honua-server'
WORKFLOW = '.github/workflows/generated-files-on-trunk.yml'
SHA = re.compile(r'[0-9a-f]{40}')


def git(*args: str) -> bytes:
    return subprocess.check_output(['git', *args], stderr=subprocess.DEVNULL, timeout=30)


def api(endpoint: str) -> bytes:
    return subprocess.check_output(['gh', 'api', endpoint], stderr=subprocess.DEVNULL, timeout=25)


def allowed_paths(base: str) -> list[str]:
    # Parse the deliberately literal canonical array as data; never eval shell.
    source = git('show', f'{base}:scripts/ci/generated-files.sh').decode('utf-8')
    lines = [line.strip() for line in source.splitlines()
             if line.strip() and not line.lstrip().startswith('#')]
    if lines[0] != 'GENERATED_FILES=(' or lines[-1] != ')':
        raise ValueError('canonical output policy is not a literal array')
    paths = lines[1:-1]
    if not paths or any(not re.fullmatch(r'[A-Za-z0-9_.-]+(?:/[A-Za-z0-9_.-]+)+', p)
                        or any(part in ('.', '..') for part in p.split('/')) for p in paths):
        raise ValueError('invalid canonical output policy')
    return paths


def eligible_diff(base: str, head: str) -> bool:
    # One publication commit directly on the validated base; read commit bytes
    # rather than rev-list, which hides parents at shallow boundaries.
    parents = [line[7:] for line in git('cat-file', '-p', head).decode('utf-8').splitlines()
               if line.startswith('parent ')]
    if parents != [base]:
        return False
    paths = allowed_paths(base)
    fields = git('diff', '--no-ext-diff', '--no-renames', '--raw', '--full-index',
                 '-z', base, head, '--').split(b'\0')
    if fields[-1] != b'' or len(fields) < 3 or len(fields) > 801:
        return False
    fields.pop()
    if len(fields) % 2:
        return False
    for header, raw_path in zip(fields[::2], fields[1::2]):
        old_mode, new_mode, _, _, status = header.decode('ascii').lstrip(':').split()
        path = raw_path.decode('utf-8')
        if status not in ('A', 'M') or new_mode != '100644':
            return False
        if old_mode != ('000000' if status == 'A' else '100644'):
            return False
        if not any(path == p or path.startswith(p + '/') for p in paths):
            return False
    return True


def canonical_run(run: dict, source: str) -> bool:
    return (run.get('path') == WORKFLOW and run.get('head_sha') == source
            and run.get('head_branch') == 'trunk'
            and run.get('event') in ('schedule', 'workflow_dispatch')
            and run.get('status') == 'completed' and run.get('conclusion') == 'success'
            and run.get('repository', {}).get('full_name') == REPOSITORY
            and run.get('head_repository', {}).get('full_name') == REPOSITORY
            and type(run.get('id')) is int and run['id'] > 0
            and type(run.get('run_attempt')) is int and run['run_attempt'] > 0)


def proof_matches(archive: bytes, run: dict, source: str, head: str, tree: str) -> bool:
    if not 0 < len(archive) <= 16384:
        return False
    with zipfile.ZipFile(io.BytesIO(archive)) as zipped:
        entries = zipped.infolist()
        if (len(entries) != 1 or entries[0].filename != 'generated-output-proof.json'
                or not 0 < entries[0].file_size <= 4096):
            return False
        proof = json.loads(zipped.read(entries[0]))
    return proof == dict(source_sha=source, head_sha=head, output_tree_sha=tree,
                         run_id=str(run['id']), run_attempt=str(run['run_attempt']))


def producer_proof(source: str, head: str, tree: str) -> bool:
    endpoint = f'repos/{REPOSITORY}/actions'
    # Publication starts PR workflows just before the producer uploads its
    # receipt and concludes. Wait at most 90s for that existing run, never
    # dispatch regeneration or execute candidate code. An expired/missing proof
    # (including older producers without this artifact) costs an ordinary gate.
    deadline = time.monotonic() + 90
    while True:
        page = json.loads(api(f'{endpoint}/workflows/generated-files-on-trunk.yml/runs?head_sha={source}&per_page=20'))
        runs = page['workflow_runs']
        if page['total_count'] > len(runs):
            return False
        for run in runs:
            if not canonical_run(run, source):
                continue
            data = json.loads(api(f'{endpoint}/runs/{run["id"]}/artifacts?per_page=100'))
            if data['total_count'] > len(data['artifacts']):
                return False
            expected = f'generated-output-proof-{run["id"]}-attempt-{run["run_attempt"]}'
            artifacts = [item for item in data['artifacts'] if item['name'] == expected]
            if len(artifacts) != 1:
                continue
            artifact = artifacts[0]
            if (artifact.get('expired') is not False or type(artifact.get('id')) is not int
                    or not 0 < artifact.get('size_in_bytes', 0) <= 16384):
                continue
            archive = api(f'{endpoint}/artifacts/{artifact["id"]}/zip')
            if proof_matches(archive, run, source, head, tree):
                return True
        if not any(run.get('status') != 'completed' and run.get('head_sha') == source
                   and run.get('path') == WORKFLOW for run in runs):
            return False
        if time.monotonic() >= deadline:
            return False
        time.sleep(min(10, max(0, deadline - time.monotonic())))


def classify(event: dict) -> bool:
    pr = event.get('pull_request', {})
    base, head = pr.get('base', {}), pr.get('head', {})
    if (os.environ.get('GITHUB_EVENT_NAME') != 'pull_request'
            or event.get('repository', {}).get('full_name') != REPOSITORY
            or base.get('repo', {}).get('full_name') != REPOSITORY
            or head.get('repo', {}).get('full_name') != REPOSITORY
            or base.get('ref') != 'trunk'
            or not SHA.fullmatch(base.get('sha', '')) or not SHA.fullmatch(head.get('sha', ''))):
        return False
    source, output = base['sha'], head['sha']
    if not eligible_diff(source, output):
        return False
    tree = git('rev-parse', f'{output}^{{tree}}').decode().strip()
    return producer_proof(source, output, tree)


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument('--github-output', action='store_true')
    args = parser.parse_args()
    verdict = False
    try:
        verdict = classify(json.loads(Path(os.environ['GITHUB_EVENT_PATH']).read_text()))
    except Exception as error:
        print(f'::notice::Generated-output admission unavailable ({type(error).__name__}); ordinary checks remain enabled.')
    answer = f'generated_only={str(verdict).lower()}\n'
    print(answer, end='')
    if args.github_output:
        with open(os.environ['GITHUB_OUTPUT'], 'a') as output:
            output.write(answer)
    if verdict and os.environ.get('GITHUB_STEP_SUMMARY'):
        with open(os.environ['GITHUB_STEP_SUMMARY'], 'a') as summary:
            summary.write('Validated generated-only output: exact successful nightly producer proof; product checks skipped.\n')


if __name__ == '__main__':
    main()
