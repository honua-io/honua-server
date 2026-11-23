#!/usr/bin/env python3
"""Report server shards whose dispatch_rank drifted from measured full-run job minutes.

PR Gate sizes its time budget and heavy placement from `dispatch_rank`
(.github/ci-shards.json). The only other rank check compares ranks with the committed
record (scripts/ci/fixtures/measured-dispatch-ranks.json), so a stale record passes
forever: for two days in November 2026 the ranks summed to ~1.9x the measured minutes
and PR Gate deferred shards to trunk. This check reads the latest completed CI full runs
on trunk and keeps one tracking issue open while any finding stands:

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
JOB_NAME = re.compile(r'Server Tests \((.+)\)$')
MARKER = '<!-- shard-rank-drift -->'
TITLE = 'ci: server shard dispatch_rank drifted from measured full-run job minutes'
LABELS = ['area/ci']
OWNER_LANE = 's-ci-shards'
API_TIMEOUT_SECONDS = 120


def api(endpoint, payload=None, method=None):
    """One gh api call; writes send the payload on stdin."""
    command = ['gh', 'api', endpoint]
    if payload is not None:
        command += ['--method', method or 'POST', '--input', '-']
    result = subprocess.run(command, input=None if payload is None else json.dumps(payload),
                            capture_output=True, text=True, timeout=API_TIMEOUT_SECONDS, check=True)
    return json.loads(result.stdout or 'null')


def full_runs(call, repo):
    """The newest RUN_WINDOW completed CI full runs on trunk (scheduled or full dispatch)."""
    page = call(f'repos/{repo}/actions/workflows/ci.yml/runs?branch=trunk&status=completed&per_page=100')
    runs = [run for run in page['workflow_runs']
            if run['event'] in ('schedule', 'workflow_dispatch')
            and run.get('display_title', '').startswith('CI full')
            and run['head_repository']['full_name'] == repo]
    return runs[:RUN_WINDOW]


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


def run_jobs(call, repo, run_id):
    jobs, page = [], 1
    while True:
        batch = call(f'repos/{repo}/actions/runs/{run_id}/jobs?filter=latest&per_page=100&page={page}')['jobs']
        jobs += batch
        if len(batch) < 100:
            return jobs
        page += 1


def merge_samples(per_run):
    merged = {}
    for samples in per_run:
        for name, minutes in samples.items():
            merged.setdefault(name, []).extend(minutes)
    return merged


def medians(ranks, samples):
    """Median measured minutes for each current shard with at least MIN_SAMPLES samples."""
    return {name: statistics.median(samples[name]) for name in ranks
            if len(samples.get(name, ())) >= MIN_SAMPLES}


def total_drift(ranks, measured):
    if not ranks or len(measured) < MIN_COVERAGE * len(ranks):
        return dict(status='insufficient', measured=len(measured), shards=len(ranks))
    rank_sum = sum(ranks[name] for name in measured)
    measured_sum = sum(measured.values())
    ratio = rank_sum / measured_sum if measured_sum else float('inf')
    status = 'ok' if RATIO_LOW <= ratio <= RATIO_HIGH else 'drift'
    return dict(status=status, measured=len(measured), shards=len(ranks), ratio=round(ratio, 2),
                rank_minutes=round(rank_sum, 1), measured_minutes=round(measured_sum, 1))


def grown_shards(ranks, measured, samples):
    return [dict(name=name, rank=ranks[name], median=round(median, 1), samples=len(samples[name]))
            for name, median in sorted(measured.items())
            if median >= GROWTH_RATIO * ranks[name] and median - ranks[name] >= GROWTH_MINUTES]


def estimated_shards(ranks, recorded, measured, samples):
    return [dict(name=name, rank=ranks[name], median=round(median, 1), samples=len(samples[name]))
            for name, median in sorted(measured.items()) if name not in recorded]


def analyze(config, record, samples):
    ranks = {shard['name']: float(shard.get('dispatch_rank') or 0.0) for shard in config['shards']}
    measured = medians(ranks, samples)
    return dict(total=total_drift(ranks, measured),
                grown=grown_shards(ranks, measured, samples),
                estimated=estimated_shards(ranks, record.get('ranks', {}), measured, samples))


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


def tracking_issue(call, repo):
    issues = call(f'repos/{repo}/issues?state=open&labels=area/ci&per_page=100')
    return next((issue for issue in issues
                 if 'pull_request' not in issue and MARKER in (issue.get('body') or '')), None)


def reconcile(call, repo, report, body):
    """Open, update or close the one tracking issue; returns the action taken."""
    if report['total']['status'] == 'insufficient':
        return 'skipped'
    issue = tracking_issue(call, repo)
    if has_findings(report):
        if issue is None:
            created = call(f'repos/{repo}/issues', dict(title=TITLE, body=body, labels=LABELS))
            return f"opened #{created['number']}"
        if issue['body'] != body:
            call(f"repos/{repo}/issues/{issue['number']}", dict(body=body), 'PATCH')
            return f"updated #{issue['number']}"
        return f"unchanged #{issue['number']}"
    if issue is None:
        return 'clean'
    call(f"repos/{repo}/issues/{issue['number']}/comments", dict(body='Ranks are back in band:\n\n' + body))
    call(f"repos/{repo}/issues/{issue['number']}", dict(state='closed', state_reason='completed'), 'PATCH')
    return f"closed #{issue['number']}"


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument('--repo', required=True)
    parser.add_argument('--config', type=Path, default=Path('.github/ci-shards.json'))
    parser.add_argument('--record', type=Path, default=Path('scripts/ci/fixtures/measured-dispatch-ranks.json'))
    parser.add_argument('--dry-run', action='store_true', help='report only; never touch issues')
    args = parser.parse_args(argv)
    runs = full_runs(api, args.repo)
    samples = merge_samples(shard_samples(run_jobs(api, args.repo, run['id'])) for run in runs)
    report = analyze(json.loads(args.config.read_text()), json.loads(args.record.read_text()), samples)
    body = render(report, runs)
    print(body)
    print('issue:', 'dry run' if args.dry_run else reconcile(api, args.repo, report, body))
    return 0


if __name__ == '__main__':
    sys.exit(main())
