#!/usr/bin/env python3
"""Report server shards whose dispatch_rank drifted from measured full-run job minutes.

PR Gate sizes its time budget and heavy placement from `dispatch_rank`
(.github/ci-shards.json). The only other rank check compares ranks with the committed
record (scripts/ci/fixtures/measured-dispatch-ranks.json), so a stale record passes.
This check reads the latest completed CI full runs on trunk and keeps one tracking
issue open while any finding stands:

- total drift: sum of ranks / sum of median measured minutes outside the band;
- grown shard: median at least GROWTH_RATIO x and GROWTH_MINUTES over its rank;
- estimated shard: absent from the measured record but already sampled.

Too few measured shards (a failed or cancelled run window) neither opens nor closes it.
"""
import argparse
from datetime import datetime
import json
from pathlib import Path
import re
import statistics
import subprocess
import sys

RATIO_LOW, RATIO_HIGH = 0.77, 1.30
MIN_COVERAGE = 0.8
GROWTH_RATIO, GROWTH_MINUTES = 1.5, 3.0
MIN_SAMPLES = 2
RUN_WINDOW = 3
MAX_RUN_PAGES = 10
MAX_ISSUE_PAGES = 20
DEFINITION_KEYS = ('filter', 'csproj', 'csprojs')
JOB_NAME = re.compile(r'Server Tests \((.+)\)$')
MARKER = '<!-- shard-rank-drift -->'
TITLE = 'ci: server shard dispatch_rank drifted from measured full-run job minutes'
LABELS = ['area/ci']
OWNER_LANE = 's-ci-shards'
API_TIMEOUT_SECONDS = 120
PER_PAGE = 100
RAW = 'Accept: application/vnd.github.raw+json'


class GitHub:
    """gh api for one repository; writes send the payload on stdin and run once."""

    def __init__(self, repo):
        self.repo = repo

    def _run(self, command, payload=None):
        result = subprocess.run(command, input=None if payload is None else json.dumps(payload),
                                capture_output=True, text=True, timeout=API_TIMEOUT_SECONDS, check=True)
        return json.loads(result.stdout or 'null')

    def read(self, path):
        return self._run(['gh', 'api', f'repos/{self.repo}/{path}'])

    def raw(self, path):
        """Raw media type: file contents beyond the 1 MB inline limit."""
        return self._run(['gh', 'api', '-H', RAW, f'repos/{self.repo}/{path}'])

    def write(self, path, payload, method='POST'):
        return self._run(['gh', 'api', f'repos/{self.repo}/{path}', '--method', method, '--input', '-'], payload)


def is_full_run(run, repo):
    return (run['event'] in ('schedule', 'workflow_dispatch')
            and run.get('display_title', '').startswith('CI full')
            and run['head_repository']['full_name'] == repo)


def full_runs(github):
    """The newest RUN_WINDOW completed CI full runs on trunk; affected runs outnumber them, so page."""
    runs = []
    for page in range(1, MAX_RUN_PAGES + 1):
        batch = github.read(f'actions/workflows/ci.yml/runs?branch=trunk&status=completed'
                            f'&per_page={PER_PAGE}&page={page}')['workflow_runs']
        runs += [run for run in batch if is_full_run(run, github.repo)]
        if len(runs) >= RUN_WINDOW or len(batch) < PER_PAGE:
            break
    return runs[:RUN_WINDOW]


def definitions(config):
    """Each shard's test selection: a sample counts only when it matches the current one."""
    return {shard['name']: tuple(json.dumps(shard.get(key), sort_keys=True) for key in DEFINITION_KEYS)
            for shard in config['shards']}


def head_definitions(github, sha):
    return definitions(github.raw(f'contents/.github/ci-shards.json?ref={sha}'))


def matching_samples(samples, at_head, current):
    return {name: minutes for name, minutes in samples.items()
            if name in current and at_head.get(name) == current[name]}


def job_seconds(job):
    started, completed = (datetime.fromisoformat(job[key].replace('Z', '+00:00'))
                          for key in ('started_at', 'completed_at'))
    return (completed - started).total_seconds()


def shard_samples(jobs):
    """Passing `Server Tests (<shard>)` job minutes keyed by shard name."""
    samples = {}
    for job in jobs:
        match = JOB_NAME.match(job['name'])
        if match and job.get('conclusion') == 'success' and job.get('completed_at'):
            samples.setdefault(match.group(1), []).append(job_seconds(job) / 60)
    return samples


def run_jobs(github, run_id):
    jobs, page = [], 1
    while True:
        batch = github.read(f'actions/runs/{run_id}/jobs?filter=latest&per_page={PER_PAGE}&page={page}')['jobs']
        jobs += batch
        if len(batch) < PER_PAGE:
            return jobs
        page += 1


def merge_samples(per_run):
    merged = {}
    for samples in per_run:
        for name, minutes in samples.items():
            merged.setdefault(name, []).extend(minutes)
    return merged


def measured_rows(ranks, samples):
    """Rank, median and sample count for each current shard with at least MIN_SAMPLES samples."""
    return [dict(name=name, rank=ranks[name], median=statistics.median(samples[name]), samples=len(samples[name]))
            for name in sorted(ranks) if len(samples.get(name, ())) >= MIN_SAMPLES]


def total_drift(rows, shard_count):
    if not shard_count or len(rows) < MIN_COVERAGE * shard_count:
        return dict(status='insufficient', measured=len(rows), shards=shard_count)
    rank_sum = sum(row['rank'] for row in rows)
    measured_sum = sum(row['median'] for row in rows)
    ratio = rank_sum / measured_sum if measured_sum else float('inf')
    status = 'ok' if RATIO_LOW <= ratio <= RATIO_HIGH else 'drift'
    return dict(status=status, measured=len(rows), shards=shard_count, ratio=round(ratio, 2),
                rank_minutes=round(rank_sum, 1), measured_minutes=round(measured_sum, 1))


def grown(row):
    return row['median'] >= GROWTH_RATIO * row['rank'] and row['median'] - row['rank'] >= GROWTH_MINUTES


def rounded(rows):
    return [dict(row, median=round(row['median'], 1)) for row in rows]


def analyze(config, record, samples):
    ranks = {shard['name']: float(shard.get('dispatch_rank') or 0.0) for shard in config['shards']}
    rows = measured_rows(ranks, samples)
    recorded = record.get('ranks', {})
    return dict(total=total_drift(rows, len(ranks)),
                grown=rounded([row for row in rows if grown(row)]),
                estimated=rounded([row for row in rows if row['name'] not in recorded]))


def has_findings(report):
    return report['total']['status'] == 'drift' or bool(report['grown'] or report['estimated'])


def shard_table(rows):
    lines = ['| Shard | dispatch_rank | Median minutes | Samples |', '|---|---:|---:|---:|']
    return lines + [f"| {row['name']} | {row['rank']} | {row['median']} | {row['samples']} |" for row in rows]


def render(report, runs):
    total = report['total']
    lines = [MARKER, f'Owner lane: {OWNER_LANE}', '',
             f"Full runs: {', '.join(run['html_url'] for run in runs)}", '',
             f"Total: {total['status']} — {total['measured']}/{total['shards']} shards measured"]
    if 'ratio' in total:
        lines[-1] += (f"; ranks {total['rank_minutes']} min vs measured {total['measured_minutes']} min "
                      f"(ratio {total['ratio']}, band {RATIO_LOW}-{RATIO_HIGH})")
    sections = (('Shards over their rank (#9546)', report['grown']),
                ('Shards still on a contract estimate (#9547)', report['estimated']))
    for heading, rows in sections:
        if rows:
            lines += ['', f'### {heading}', '', *shard_table(rows)]
    lines += ['', 'Refresh: gate-model.md "Measured ranks" (median passing job minutes per shard).']
    return '\n'.join(lines) + '\n'


def tracking_issue(github):
    """The open tracker, searched across every page of open area/ci issues."""
    for page in range(1, MAX_ISSUE_PAGES + 1):
        batch = github.read(f'issues?state=open&labels=area/ci&per_page={PER_PAGE}&page={page}')
        issue = next((item for item in batch
                      if 'pull_request' not in item and MARKER in (item.get('body') or '')), None)
        if issue is not None or len(batch) < PER_PAGE:
            return issue
    raise RuntimeError(f'over {MAX_ISSUE_PAGES * PER_PAGE} open area/ci issues; tracker lookup incomplete')


def reconcile(github, report, body):
    """Open, update or close the one tracking issue; returns the action taken."""
    if report['total']['status'] == 'insufficient':
        return 'skipped'
    issue = tracking_issue(github)
    if has_findings(report):
        if issue is None:
            created = github.write('issues', dict(title=TITLE, body=body, labels=LABELS))
            return f"opened #{created['number']}"
        if issue['body'] != body:
            github.write(f"issues/{issue['number']}", dict(body=body), 'PATCH')
            return f"updated #{issue['number']}"
        return f"unchanged #{issue['number']}"
    if issue is None:
        return 'clean'
    github.write(f"issues/{issue['number']}/comments", dict(body='Ranks are back in band:\n\n' + body))
    github.write(f"issues/{issue['number']}", dict(state='closed', state_reason='completed'), 'PATCH')
    return f"closed #{issue['number']}"


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument('--repo', required=True)
    parser.add_argument('--config', type=Path, default=Path('.github/ci-shards.json'))
    parser.add_argument('--record', type=Path, default=Path('scripts/ci/fixtures/measured-dispatch-ranks.json'))
    parser.add_argument('--dry-run', action='store_true', help='report only; never touch issues')
    args = parser.parse_args(argv)
    github, config = GitHub(args.repo), json.loads(args.config.read_text())
    current = definitions(config)
    runs = full_runs(github)
    samples = merge_samples(matching_samples(shard_samples(run_jobs(github, run['id'])),
                                             head_definitions(github, run['head_sha']), current)
                            for run in runs)
    report = analyze(config, json.loads(args.record.read_text()), samples)
    body = render(report, runs)
    print(body)
    print('issue:', 'dry run' if args.dry_run else reconcile(github, report, body))
    return 0


if __name__ == '__main__':
    sys.exit(main())
