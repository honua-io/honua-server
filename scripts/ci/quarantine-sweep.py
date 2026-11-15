#!/usr/bin/env python3
"""Coalesce completion events without losing unpublished quarantine evidence."""
import argparse
from concurrent.futures import ThreadPoolExecutor
from datetime import datetime, timedelta, timezone
import importlib.util
import json
import os
from pathlib import Path
import re
import stat
import subprocess
import tempfile
import zipfile

SPEC = importlib.util.spec_from_file_location('quarantine_publisher', Path(__file__).with_name('quarantine-issues.py'))
PUBLISHER = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(PUBLISHER)
SCHEMA = 'honua.quarantine-cursor/v1'
CURSOR_PREFIX = 'flake-quarantine-cursor-'
WORKFLOW = '.github/workflows/flake-quarantine.yml'
SOURCES = {'.github/workflows/ci.yml', '.github/workflows/pr-gate.yml'}
ARTIFACT = re.compile(r'^(server-test-results-|pr-gate-affected-shard-)[a-zA-Z0-9_-]+$')
ATTEMPT = re.compile(r'^(.*?)(?:-attempt-([0-9]+))?$')
MAX_PAGES = 1000
INVENTORY_WORKERS = 8
PAGE_SIZE = 100
MAX_BATCH_RUNS = 25
MAX_ZIP_BYTES = 256 * 1024 * 1024
MAX_EXPANDED_BYTES = 1024 * 1024 * 1024
PROOF_SUFFIXES = ('.flake-candidates.json', '.first-attempt.xml', '.retry-attempt.xml')


def api(path):
    return PUBLISHER.api(f'repos/{os.environ["GITHUB_REPOSITORY"]}/{path}')


def inventory(path, key):
    found = {}
    page = 1
    total = None

    def read(number):
        response = api(f'{path}{"&" if "?" in path else "?"}per_page={PAGE_SIZE}&page={number}')
        if response['total_count'] > MAX_PAGES * PAGE_SIZE:
            raise ValueError('Artifact inventory exceeds bounded sweep capacity; checkpoint unchanged')
        return response

    with ThreadPoolExecutor(max_workers=INVENTORY_WORKERS) as executor:
        while page <= MAX_PAGES:
            remaining = max(1, ((total or 0) + PAGE_SIZE - 1) // PAGE_SIZE - page + 1)
            stop = min(page + INVENTORY_WORKERS, page + remaining, MAX_PAGES + 1)
            responses = list(executor.map(read, range(page, stop)))
            for response in responses:
                if total is None:
                    total = response['total_count']
                elif total != response['total_count']:
                    raise ValueError('Artifact inventory changed during discovery; checkpoint unchanged')
                batch = response[key]
                found.update((item['id'], item) for item in batch)
                if len(batch) < PAGE_SIZE:
                    if len(found) != total:
                        raise ValueError('Artifact inventory is incomplete; checkpoint unchanged')
                    print(f'Inventoried {path}: {len(found)} unique entries through page {page}', flush=True)
                    return list(found.values())
                page += 1
            print(f'Inventoried {path}: {len(found)} unique entries through page {page - 1}', flush=True)
    raise ValueError('Artifact inventory exceeded bounded pagination; checkpoint unchanged')


def download(artifact, destination):
    if artifact['size_in_bytes'] > MAX_ZIP_BYTES:
        raise ValueError('Evidence archive exceeds download limit')
    endpoint = f'repos/{os.environ["GITHUB_REPOSITORY"]}/actions/artifacts/{artifact["id"]}/zip'
    archive = destination / 'evidence.zip'
    with archive.open('wb') as output:
        subprocess.run(['gh', 'api', endpoint], stdout=output, check=True, timeout=120)
    if archive.stat().st_size > MAX_ZIP_BYTES:
        raise ValueError('Evidence archive exceeds download limit')
    with zipfile.ZipFile(archive) as zipped:
        extract_proof(zipped, destination)
    archive.unlink()


def extract_proof(zipped, destination):
    members = zipped.infolist()
    if len(members) > 10000 or sum(member.file_size for member in members) > MAX_EXPANDED_BYTES:
        raise ValueError('Evidence archive exceeds extraction limit')
    for member in members:
        path = Path(member.filename)
        if path.is_absolute() or '..' in path.parts or stat.S_ISLNK(member.external_attr >> 16):
            raise ValueError('Unsafe evidence archive member')
        if member.filename.endswith((*PROOF_SUFFIXES, 'quarantine-cursor.json')):
            zipped.extract(member, destination)


def trusted(run, paths):
    return (run.get('path', '').split('@')[0] in paths
            and (run.get('head_repository') or {}).get('full_name') == os.environ['GITHUB_REPOSITORY'])


def cursor_from_run(run):
    if (not trusted(run, {WORKFLOW}) or run['event'] not in {'workflow_run', 'schedule', 'workflow_dispatch'}
            or run.get('head_branch') != os.environ['QUARANTINE_DEFAULT_BRANCH']):
        raise ValueError('Untrusted publication checkpoint run')
    artifacts = inventory(f'actions/runs/{run["id"]}/artifacts', 'artifacts')
    cursors = [item for item in artifacts if item['name'].startswith(CURSOR_PREFIX) and not item['expired']]
    if not cursors:
        jobs = inventory(f'actions/runs/{run["id"]}/jobs?filter=latest', 'jobs')
        if jobs and all(job['conclusion'] == 'skipped' for job in jobs):
            return None  # Filtered completions can succeed without publishing.
        if run.get('display_title') == 'Quarantine evidence sweep':
            raise ValueError('Publication checkpoint missing or expired; refusing to discard its backlog')
        # A successful FIFO event says nothing about other failed/overflowed
        # events. Reconcile every retained artifact during migration.
        return dict(schema=SCHEMA, since='1970-01-01T00:00:00Z', artifact_ids=[])
    with tempfile.TemporaryDirectory() as directory:
        root = Path(directory)
        download(max(cursors, key=lambda item: item['id']), root)
        cursor = json.loads((root / 'quarantine-cursor.json').read_text())
    validate_cursor(cursor)
    return cursor


def load_cursor():
    for page in range(1, MAX_PAGES + 1):
        response = api(f'actions/workflows/flake-quarantine.yml/runs?status=success&per_page=100&page={page}')
        for run in response['workflow_runs']:
            if run.get('head_branch') != os.environ['QUARANTINE_DEFAULT_BRANCH']:
                continue  # A branch dispatch cannot supply the trusted cursor.
            cursor = cursor_from_run(run)
            if cursor is not None:
                return cursor
        if len(response['workflow_runs']) < 100:
            # First installation backfills the policy's 14-day evidence window.
            return dict(schema=SCHEMA, since=(datetime.now(timezone.utc) - timedelta(days=14)).isoformat(), artifact_ids=[])
    raise ValueError('Checkpoint discovery exceeded bounded pagination; checkpoint unchanged')


def timestamp(value):
    return datetime.fromisoformat(value.replace('Z', '+00:00'))


def validate_cursor(cursor):
    if cursor.get('schema') != SCHEMA:
        raise ValueError('Unknown quarantine checkpoint schema')
    since = timestamp(cursor['since'])
    ids = cursor['artifact_ids']
    if since.tzinfo is None or not isinstance(ids, list) or len(ids) > MAX_PAGES * 100:
        raise ValueError('Invalid quarantine checkpoint')
    if any(type(identity) is not int or identity < 1 for identity in ids):
        raise ValueError('Invalid checkpoint artifact identity')


def candidates(artifacts, seen):
    grouped = {}
    for artifact in artifacts:
        source = artifact.get('workflow_run') or {}
        if (not ARTIFACT.fullmatch(artifact['name']) or artifact['expired']
                or source.get('head_repository_id') != source.get('repository_id') or not source.get('id')):
            continue
        grouped.setdefault(source['id'], []).append(artifact)
    # Keep seen artifacts in each group while selecting the newest attempt:
    # an unseen, superseded artifact must never replay after a newer attempt.
    return {identity: items for identity, items in grouped.items() if any(item['id'] not in seen for item in items)}


def newest_shards(artifacts):
    shards = {}
    for artifact in artifacts:
        shard, attempt = ATTEMPT.fullmatch(artifact['name']).groups()
        rank = (int(attempt or 0), artifact['created_at'], artifact['id'])
        if shard not in shards or rank > shards[shard][0]:
            shards[shard] = (rank, artifact)
    return [item[1] for item in shards.values()]


def publish_run(run, artifacts, seen):
    selected = [item for item in newest_shards(artifacts) if item['id'] not in seen]
    if not selected:
        return
    with tempfile.TemporaryDirectory() as directory:
        root = Path(directory)
        for artifact in selected:
            destination = root / str(artifact['id'])
            destination.mkdir()
            download(artifact, destination)
            _, attempt = ATTEMPT.fullmatch(artifact['name']).groups()
            os.environ['QUARANTINE_EVIDENCE_URL'] = run['html_url'] + (f'/attempts/{attempt}' if attempt else '')
            os.environ['QUARANTINE_EVIDENCE_CREATED_AT'] = artifact['created_at']
            PUBLISHER.publish(destination)


def sweep(checkpoint):
    cursor = load_cursor()
    validate_cursor(cursor)
    artifacts = inventory('actions/artifacts', 'artifacts')
    retained = {item['id'] for item in artifacts}
    seen = set(cursor['artifact_ids']) & retained
    grouped = candidates(artifacts, seen)
    completed = 0
    for identity, items in sorted(grouped.items()):
        run = api(f'actions/runs/{identity}')
        if run['status'] != 'completed':
            continue  # Revisit the same uploaded artifacts when the source finishes.
        if (trusted(run, SOURCES) and run['conclusion'] in {'success', 'failure'}
                and timestamp(run['updated_at']) >= timestamp(cursor['since'])):
            publish_run(run, items, seen)
            completed += 1
        seen.update(item['id'] for item in items)
        if completed >= MAX_BATCH_RUNS:
            break  # The next event or scheduled sweep resumes the remaining backlog.
    cursor['artifact_ids'] = sorted(seen)
    checkpoint.write_text(json.dumps(cursor, sort_keys=True) + '\n')
    print(f'Published {completed} source run(s); checkpoint records {len(seen)} retained artifacts')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--checkpoint', required=True, type=Path)
    options = parser.parse_args()
    sweep(options.checkpoint)


if __name__ == '__main__':
    main()
