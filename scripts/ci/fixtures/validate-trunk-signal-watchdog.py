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
    def run_watchdog(self, scheduled, **responses):
        harness = r'''
const vm = require('node:vm');
const input = JSON.parse(process.argv[1]);
const now = Date.parse('2026-11-11T12:00:00Z');
const calls = [];
const result = {status: 'completed', conclusion: 'success',
  updated_at: new Date(now - 3600000).toISOString(),
  html_url: 'https://example.test/result'};
const sandbox = {
  context: {repo: {owner: 'example', repo: 'server'}},
  Date: class extends Date { static now() { return now; } },
  process: {env: input.env},
  core: {info() {}, setFailed(message) { calls.push(['failed', message]); }},
  github: {async paginate(method, params) { return (await method(params)).data.jobs; }, rest: {
    actions: {async listJobsForWorkflowRun(params) {
      calls.push(['jobs', params]);
      return {data: {jobs: input.jobs[String(params.run_id)] || []}};
    }, async listWorkflowRuns(params) {
      calls.push(['query', params]);
      return {data: {workflow_runs: params.event === 'schedule' ? input.scheduled : (input.runs || [result])}};
    }},
    issues: {
      async listForRepo() { return {data: input.issues}; },
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
        payload = dict(scheduled=scheduled, issues=responses.get('issues', ()),
                       runs=responses.get('runs'), jobs=responses.get('jobs', {}),
                       env=STEP['env'], script=STEP['with']['script'])
        completed = subprocess.run(['node', '-e', harness, json.dumps(payload)],
                                   text=True, capture_output=True, check=True)
        return json.loads(completed.stdout)

    def test_queued_schedule_with_assigned_jobs_has_started(self):
        queued = {'id': 42, 'status': 'queued', 'run_started_at': '2026-11-11T11:00:00Z',
                  'updated_at': '2026-11-11T11:00:00Z'}
        calls = self.run_watchdog([queued], runs=[queued, self.recent_result()], jobs={
            '42': [{'status': 'in_progress', 'runner_id': 12, 'started_at': '2026-11-11T11:01:00Z'}],
        })
        self.assertFalse(any(kind == 'failed' for kind, _ in calls))
        self.assertEqual(sum(kind == 'jobs' for kind, _ in calls), 1)

    @staticmethod
    def recent_result():
        return {'status': 'completed', 'conclusion': 'success', 'updated_at': '2026-11-11T11:30:00Z'}

    def test_queued_job_timestamps_without_runner_do_not_prove_schedule_start(self):
        calls = self.run_watchdog([
            {'id': 42, 'status': 'queued', 'run_started_at': '2026-11-11T11:00:00Z'},
        ], jobs={'42': [{'status': 'queued', 'runner_id': 0, 'started_at': '2026-11-11T11:01:00Z'}]})
        self.assertTrue(any(kind == 'failed' and '14 h' in value for kind, value in calls))

    def test_stalled_child_queue_still_alerts_after_schedule_starts(self):
        queued = {'id': 42, 'status': 'queued', 'updated_at': '2026-11-11T11:00:00Z',
                  'run_started_at': '2026-11-11T11:00:00Z', 'html_url': 'https://example.test/full'}
        calls = self.run_watchdog([queued], runs=[queued, self.recent_result()], jobs={'42': [
            {'status': 'completed', 'runner_id': 12, 'started_at': '2026-11-11T11:01:00Z'},
            {'status': 'queued', 'runner_id': 0, 'started_at': '2026-11-11T11:02:00Z', 'name': 'Waiting shard'},
        ]})
        self.assertTrue(any(kind == 'failed' and 'Waiting shard' in value for kind, value in calls))
        self.assertFalse(any(kind == 'failed' and '14 h' in value for kind, value in calls))

    def test_cancellation_churn_still_alerts_with_fresh_result(self):
        calls = self.run_watchdog([
            {'status': 'completed', 'run_started_at': '2026-11-11T11:00:00Z'},
        ], runs=[self.recent_result()] + [{'status': 'completed', 'conclusion': 'cancelled'}] * 6)
        self.assertTrue(any(kind == 'failed' and '6 of the last 10' in value for kind, value in calls))

    def test_workflow_with_no_jobs_retains_queue_alert(self):
        calls = self.run_watchdog([
            {'status': 'completed', 'run_started_at': '2026-11-11T11:00:00Z'},
        ], runs=[self.recent_result(), {'id': 42, 'status': 'queued',
                                       'updated_at': '2026-11-11T11:00:00Z'}])
        self.assertTrue(any(kind == 'failed' and 'queued since' in value for kind, value in calls))

    def test_fresh_child_queue_does_not_inherit_old_workflow_timestamp(self):
        queued = {'id': 42, 'status': 'queued', 'updated_at': '2026-11-11T11:00:00Z'}
        calls = self.run_watchdog([queued], runs=[queued, self.recent_result()], jobs={'42': [
            {'status': 'completed', 'runner_id': 12, 'started_at': '2026-11-11T11:01:00Z'},
            {'status': 'queued', 'runner_id': 0, 'started_at': '2026-11-11T11:50:00Z'},
        ]})
        self.assertFalse(any(kind == 'failed' for kind, _ in calls))
        query = [value for kind, value in calls if kind == 'jobs']
        self.assertEqual(query[0]['filter'], 'latest')
        self.assertEqual(query[0]['per_page'], 100)

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
            {'status': 'pending', 'run_started_at': '2026-11-11T11:00:00Z'},
            {'status': 'waiting', 'run_started_at': '2026-11-11T11:00:00Z'},
            {'status': 'in_progress', 'run_started_at': None},
            {'status': 'in_progress', 'run_started_at': '2026-11-12T11:00:00Z'},
        ]:
            with self.subTest(run=run):
                calls = self.run_watchdog([run], issues=[{'number': 1, 'title': STEP['env']['ISSUE_TITLE']}])
                self.assertTrue(any(kind == 'failed' for kind, _ in calls))
                self.assertFalse(any(kind == 'update' for kind, _ in calls))

    def test_recent_scheduled_start_closes_existing_issue(self):
        calls = self.run_watchdog([
            {'status': 'in_progress', 'run_started_at': '2026-11-10T22:00:00Z'}
        ], issues=[{'number': 1, 'title': STEP['env']['ISSUE_TITLE']}])
        self.assertIn(['update', {'owner': 'example', 'repo': 'server', 'issue_number': 1, 'state': 'closed'}], calls)
        self.assertFalse(any(kind == 'failed' for kind, _ in calls))

    def test_idle_trunk_does_not_require_continuous_results(self):
        calls = self.run_watchdog([
            {'status': 'completed', 'run_started_at': '2026-11-11T09:00:00Z'}
        ], runs=[{'status': 'completed', 'conclusion': 'success', 'updated_at': '2026-11-11T09:00:00Z'}])
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
        ], runs=[
            {'status': 'completed', 'conclusion': 'success', 'updated_at': '2026-11-11T09:00:00Z'},
            {'status': 'in_progress'},
        ])
        self.assertTrue(any(kind == 'failed' and '2 h' in value for kind, value in calls))


if __name__ == '__main__':
    unittest.main()
