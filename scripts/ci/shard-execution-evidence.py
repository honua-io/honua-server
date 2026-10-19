#!/usr/bin/env python3
"""Require fresh executed shard cases and retain measured TRX provenance."""
from __future__ import annotations

import argparse
import hashlib
import json
import os
from pathlib import Path
import runpy
import subprocess
import sys
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[2]
TRIPWIRE = ROOT / 'scripts/ci/assert-trx-executed.py'


def digest(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def observe(trx: Path, label: str, project: str) -> dict:
    evidence = {
        'schema': 'honua.shard-execution/v1', 'status': 'failed',
        'sourceSha': subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=ROOT, text=True).strip(),
        'sourceDirty': bool(subprocess.check_output(['git', 'status', '--porcelain', '--untracked-files=no'], cwd=ROOT, text=True)),
        'project': project, 'workflowRunId': os.environ.get('GITHUB_RUN_ID'),
        'workflowRunAttempt': os.environ.get('GITHUB_RUN_ATTEMPT'), 'workflowJob': os.environ.get('GITHUB_JOB'),
        'trxFile': trx.name, 'trxSha256': None, 'counts': None,
        'runnerSha256': digest(ROOT / 'scripts/ci/run-server-test-shard.sh'),
        'verifierSha256': digest(Path(__file__)), 'tripwireSha256': digest(TRIPWIRE),
    }
    guard = runpy.run_path(str(TRIPWIRE), run_name='shard_tripwire')
    try:
        # Read only the exact owned file, never a wildcard or an earlier attempt's directory.
        raw = trx.read_bytes()
        evidence['trxSha256'] = hashlib.sha256(raw).hexdigest()
        results = guard['read_results'](trx)
        outcomes = guard['summarise'](results)
        counts = {'total': len(results), 'passed': outcomes.get('Passed', 0),
                  'failed': outcomes.get('Failed', 0), 'skipped': outcomes.get('NotExecuted', 0),
                  'executed': sum(n for outcome, n in outcomes.items() if outcome != 'NotExecuted')}
        evidence['counts'] = counts
        # Check the declared VSTest counters against actual result rows, so a summary alone
        # cannot substitute for cases and contradictory totals cannot be published as proof.
        counters = ET.fromstring(raw).find('.//{*}ResultSummary/{*}Counters')
        if counters is None:
            raise ValueError('TRX has no result counters')
        declared = {key: int(counters.attrib[key]) for key in ('total', 'executed', 'passed', 'failed')}
        if declared != {key: counts[key] for key in declared}:
            raise ValueError('TRX counters disagree with actual result rows')
        guard['check'](label, [trx], 1, None)
        evidence['status'] = 'passed'
    except (OSError, ValueError, KeyError, ET.ParseError, guard['TripwireError']) as exc:
        # Keep all observed counts/hash even on refusal; never turn absent TRX into zero success.
        evidence['problem'] = str(exc).splitlines()[0]
    return evidence


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--trx', required=True, type=Path)
    parser.add_argument('--label', required=True)
    parser.add_argument('--project', required=True)
    args = parser.parse_args()
    result = observe(args.trx, args.label, args.project)
    print(json.dumps(result, sort_keys=True))
    if result['status'] != 'passed':
        print('::error::HONUA_SHARD_NO_EXECUTION ' + result['problem'], file=sys.stderr)
        return 1
    return 0


if __name__ == '__main__':
    sys.exit(main())
