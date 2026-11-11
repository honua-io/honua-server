#!/usr/bin/env python3
"""Execute the watchdog against isolated Actions and issue API responses."""

import json
from pathlib import Path
import subprocess
import unittest

import yaml

ROOT = Path(__file__).resolve().parents[3]
DEFINITION = yaml.load(
    (ROOT / '.github/workflows/trunk-signal-watchdog.yml').read_text(),
    Loader=yaml.BaseLoader,
)
STEP = DEFINITION['jobs']['watch']['steps'][0]


class WatchdogTests(unittest.TestCase):
    def run_watchdog(self, scheduled, *, recent_result=True, existing=False, runs=None):
        harness = r'''
const vm = require('node:vm');
const input = JSON.parse(process.argv[1]);
const now = Date.parse('2026-11-11T12:00:00Z');
const calls = [];
const result = {status: 'completed', conclusion: 'success',
  updated_at: new Date(now - (input.recent_result ? 1 : 3) * 3600000).toISOString(),
  html_url: 'https://example.test/result'};
const sandbox = {
  context: {repo: {owner: 'example', repo: 'server'}},
  Date: class extends Date { static now() { return now; } },
  process: {env: input.env},
  core: {info() {}, setFailed(message) { calls.push(['failed', message]); }},
  github: {rest: {
    actions: {async listWorkflowRuns(params) {
      calls.push(['query', params]);
      return {data: {workflow_runs: params.event === 'schedule' ? input.scheduled : (input.runs || [result])}};
    }},
    issues: {
      async listForRepo() { return {data: input.existing ? [{number: 1, title: input.env.ISSUE_TITLE}] : []}; },
      async create(params) { calls.push(['create', params]); },
      async createComment(params) { calls.push(['comment', params]); },
      async update(params) { calls.push(['update', params]); },
    },
  }},
};
vm.runInNewContext('(async () => {' + input.script + '})()', sandbox)
  .then(() => process.stdout.write(JSON.stringify(calls)))
  .catch(error => { console.error(error); process.exitCode = 1; });
'''
        payload = dict(scheduled=scheduled, recent_result=recent_result,
                       existing=existing, runs=runs, env=STEP['env'], script=STEP['with']['script'])
        completed = subprocess.run(['node', '-e', harness, json.dumps(payload)],
                                   text=True, capture_output=True, check=True)
        return json.loads(completed.stdout)

    def test_full_crons_are_twice_daily_off_hour(self):
        ci = yaml.load((ROOT / '.github/workflows/ci.yml').read_text(), Loader=yaml.BaseLoader)
        self.assertEqual(ci['on']['schedule'], [{'cron': '23 9 * * *'}, {'cron': '23 21 * * *'}])

    def test_missing_schedule_fails_despite_recent_affected_result(self):
        calls = self.run_watchdog([])
        self.assertTrue(any(kind == 'failed' and '14 h' in value for kind, value in calls))
        query = [value for kind, value in calls if kind == 'query' and value.get('event') == 'schedule']
        self.assertEqual(len(query), 1)
        self.assertEqual(query[0]['branch'], 'trunk')
        self.assertTrue(any(kind == 'create' for kind, _ in calls))

    def test_only_a_real_recent_start_recovers(self):
        for run in [
            {'status': 'completed', 'run_started_at': '2026-11-10T21:59:59Z'},
            {'status': 'queued', 'run_started_at': '2026-11-11T11:00:00Z'},
            {'status': 'in_progress', 'run_started_at': None},
            {'status': 'in_progress', 'run_started_at': '2026-11-12T11:00:00Z'},
        ]:
            with self.subTest(run=run):
                calls = self.run_watchdog([run], existing=True)
                self.assertTrue(any(kind == 'failed' for kind, _ in calls))
                self.assertFalse(any(kind == 'update' for kind, _ in calls))

    def test_recent_scheduled_start_closes_existing_issue(self):
        calls = self.run_watchdog([
            {'status': 'in_progress', 'run_started_at': '2026-11-10T22:00:00Z'}
        ], existing=True)
        self.assertIn(['update', {'owner': 'example', 'repo': 'server', 'issue_number': 1, 'state': 'closed'}], calls)
        self.assertFalse(any(kind == 'failed' for kind, _ in calls))

    def test_idle_trunk_does_not_require_continuous_results(self):
        calls = self.run_watchdog([
            {'status': 'completed', 'run_started_at': '2026-11-11T09:00:00Z'}
        ], recent_result=False)
        self.assertFalse(any(kind == 'failed' for kind, _ in calls))

    def test_latest_completion_and_current_queue_attempt_are_used(self):
        calls = self.run_watchdog([
            {'status': 'completed', 'run_started_at': '2026-11-11T11:00:00Z'}
        ], runs=[
            {'status': 'completed', 'conclusion': 'success', 'updated_at': '2026-11-11T08:00:00Z'},
            {'status': 'completed', 'conclusion': 'success', 'updated_at': '2026-11-11T11:00:00Z'},
            {'status': 'queued', 'created_at': '2026-11-10T09:00:00Z', 'updated_at': '2026-11-11T11:59:00Z'},
        ])
        self.assertFalse(any(kind == 'failed' for kind, _ in calls))

    def test_fresh_schedule_does_not_hide_stale_result(self):
        calls = self.run_watchdog([
            {'status': 'completed', 'run_started_at': '2026-11-11T11:00:00Z'}
        ], recent_result=False, runs=[
            {'status': 'completed', 'conclusion': 'success', 'updated_at': '2026-11-11T09:00:00Z'},
            {'status': 'in_progress'},
        ])
        self.assertTrue(any(kind == 'failed' and '2 h' in value for kind, value in calls))


if __name__ == '__main__':
    unittest.main()
