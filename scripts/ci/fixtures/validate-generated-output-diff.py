#!/usr/bin/env python3
"""Offline real-Git/Actions fixtures for generated-only admission (no builds)."""
import copy
import importlib.util
import io
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import tempfile
import unittest
from unittest.mock import patch
import zipfile

ROOT = Path(__file__).resolve().parents[3]
SPEC = importlib.util.spec_from_file_location('generated_output', ROOT / 'scripts/ci/generated-output-diff.py')
POLICY = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(POLICY)


def archive(proof, name='generated-output-proof.json'):
    stream = io.BytesIO()
    with zipfile.ZipFile(stream, 'w') as zipped:
        zipped.writestr(name, json.dumps(proof))
    return stream.getvalue()


class GeneratedOutputAdmission(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.repo = Path(self.temp.name)
        self.cwd = Path.cwd()
        os.chdir(self.repo)
        self.env = patch.dict(os.environ, GITHUB_EVENT_NAME='pull_request')
        self.env.start()
        self.git('init', '-b', 'trunk')
        for name in ('generated-files.sh', 'generated-output-diff.py'):
            target = self.repo / 'scripts/ci' / name
            target.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy(ROOT / 'scripts/ci' / name, target)
        # Reconstruct the before-image of the actual #5725 patch. Padding
        # outside its hunks is irrelevant to path/mode/whole-tree admission.
        diff = (ROOT / 'scripts/ci/fixtures/generated-output-5725.patch').read_text()
        self.paths = []
        for chunk in diff.split('diff --git ')[1:]:
            path = re.search(r'^\+\+\+ b/(.+)$', chunk, re.M)[1]
            self.paths.append(path)
            lines = []
            for hunk in re.split(r'(?m)^@@ ', chunk)[1:]:
                header, body = hunk.split('\n', 1)
                start = int(re.match(r'-(\d+)', header)[1])
                lines.extend(['\n'] * (start - 1 - len(lines)))
                lines.extend(line[1:] + '\n' for line in body.splitlines()
                             if line.startswith((' ', '-')))
            target = self.repo / path
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_text(''.join(lines))
        self.write('src/example.cs', 'source\n')
        self.write('scripts/generator.py', 'generator\n')
        self.write('.github/workflows/example.yml', 'workflow\n')
        self.source = self.commit()
        self.git('apply', str(ROOT / 'scripts/ci/fixtures/generated-output-5725.patch'))
        self.head = self.commit()
        self.event = dict(repository=dict(full_name=POLICY.REPOSITORY), pull_request=dict(
            base=dict(sha=self.source, ref='trunk', repo=dict(full_name=POLICY.REPOSITORY)),
            head=dict(sha=self.head, ref='arbitrary-name', repo=dict(full_name=POLICY.REPOSITORY))))
        self.run = dict(id=42, run_attempt=2, head_sha=self.source, head_branch='trunk',
                        event='schedule', path=POLICY.WORKFLOW, status='completed', conclusion='success',
                        repository=dict(full_name=POLICY.REPOSITORY), head_repository=dict(full_name=POLICY.REPOSITORY))
        self.proof = dict(source_sha=self.source, head_sha=self.head,
                          output_tree_sha=self.git('rev-parse', 'HEAD^{tree}'), run_id='42', run_attempt='2')
        self.artifact = dict(id=77, name='generated-output-proof-42-attempt-2', expired=False, size_in_bytes=512)

    def tearDown(self):
        self.env.stop()
        os.chdir(self.cwd)
        self.temp.cleanup()

    def git(self, *args):
        return subprocess.check_output(['git', *args], stderr=subprocess.DEVNULL, text=True).strip()

    def write(self, path, text):
        target = self.repo / path
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_text(text)

    def commit(self, amend=False):
        self.git('add', '.')
        self.git('-c', 'user.name=Mike McDougall', '-c', 'user.email=mike@honua.io',
                 'commit', *(('--amend', '--no-edit') if amend else ('-m', 'fixture')))
        return self.git('rev-parse', 'HEAD')

    def actions(self, endpoint):
        if '/workflows/' in endpoint:
            return json.dumps(dict(total_count=1, workflow_runs=[self.run])).encode()
        if '/runs/' in endpoint:
            return json.dumps(dict(total_count=1, artifacts=[self.artifact])).encode()
        if endpoint.endswith('/77/zip'):
            return archive(self.proof)
        raise AssertionError(endpoint)

    def verdict(self):
        with patch.object(POLICY, 'api', side_effect=self.actions):
            return POLICY.classify(self.event)

    def test_actual_four_file_5725_diff_takes_cheap_path(self):
        self.assertEqual(self.git('diff', '--name-only', self.source, self.head).splitlines(), self.paths)
        self.assertTrue(self.verdict())
        # Branch/author/trailer claims are unnecessary: this commit has none.
        self.assertNotIn('Generated-From', self.git('log', '-1', '--format=%B'))

    def test_source_generator_workflow_mixed_and_self_authored_policy_refused(self):
        for path in ('src/example.cs', 'scripts/generator.py', '.github/workflows/example.yml',
                     'scripts/ci/generated-output-diff.py', 'scripts/ci/generated-files.sh', 'unknown.json'):
            with self.subTest(path=path):
                self.git('reset', '--hard', self.head)
                self.write(path, 'candidate policy cannot authorize this\n')
                self.event['pull_request']['head']['sha'] = self.commit(amend=True)
                with patch.object(POLICY, 'api') as api:
                    self.assertFalse(POLICY.classify(self.event))
                    api.assert_not_called()

    def test_rename_delete_executable_and_symlink_fail_back(self):
        for kind in ('rename', 'delete', 'executable', 'symlink'):
            with self.subTest(kind=kind):
                self.git('reset', '--hard', self.head)
                target = self.repo / self.paths[2]
                if kind == 'rename':
                    self.git('mv', self.paths[2], 'docs/okf/capabilities/renamed.md')
                elif kind == 'delete':
                    target.unlink()
                elif kind == 'executable':
                    target.chmod(0o755)
                else:
                    target.unlink()
                    target.symlink_to('../../../src/example.cs')
                self.event['pull_request']['head']['sha'] = self.commit(amend=True)
                self.assertFalse(self.verdict())

    def test_fork_other_base_non_pr_and_stale_source_refused(self):
        original = copy.deepcopy(self.event)
        for field, value in (('head_repo', 'attacker/fork'), ('base_ref', 'candidate'),
                             ('base_sha', self.head), ('event', 'workflow_dispatch')):
            with self.subTest(field=field):
                self.event = copy.deepcopy(original)
                if field == 'head_repo': self.event['pull_request']['head']['repo']['full_name'] = value
                if field == 'base_ref': self.event['pull_request']['base']['ref'] = value
                if field == 'base_sha': self.event['pull_request']['base']['sha'] = value
                with patch.dict(os.environ, GITHUB_EVENT_NAME=value if field == 'event' else 'pull_request'):
                    self.assertFalse(self.verdict())

    def test_failed_foreign_untrusted_or_wrong_attempt_producer_refused(self):
        original = copy.deepcopy(self.run)
        for field, value in (('conclusion', 'failure'), ('event', 'pull_request'),
                             ('head_branch', 'attacker'), ('head_sha', self.head),
                             ('path', '.github/workflows/other.yml'), ('run_attempt', 3),
                             ('head_repository', dict(full_name='attacker/fork'))):
            with self.subTest(field=field):
                self.run = copy.deepcopy(original)
                self.run[field] = value
                self.assertFalse(self.verdict())

    def test_exact_source_head_tree_run_and_attempt_are_required(self):
        original = dict(self.proof)
        for field in original:
            with self.subTest(field=field):
                self.proof = dict(original, **{field: '0' * 40})
                self.assertFalse(self.verdict())

    def test_missing_expired_oversized_and_unsafe_archive_refused(self):
        original = dict(self.artifact)
        for field, value in (('expired', True), ('size_in_bytes', 16385), ('name', 'forged-proof')):
            with self.subTest(field=field):
                self.artifact = dict(original, **{field: value})
                self.assertFalse(self.verdict())
        self.assertFalse(POLICY.proof_matches(archive(self.proof, '../proof.json'), self.run,
                                            self.source, self.head, self.proof['output_tree_sha']))

    def test_publication_race_reuses_completed_run_without_regeneration(self):
        pending = dict(self.run, status='in_progress', conclusion=None)
        calls = []
        def actions(endpoint):
            if '/workflows/' in endpoint:
                calls.append(endpoint)
                return json.dumps(dict(total_count=1, workflow_runs=[pending if len(calls) == 1 else self.run])).encode()
            return self.actions(endpoint)
        with patch.object(POLICY, 'api', side_effect=actions), patch.object(POLICY.time, 'sleep') as sleep:
            self.assertTrue(POLICY.classify(self.event))
            sleep.assert_called_once()
            self.assertEqual(len(calls), 2)

    def test_runtime_api_failure_publishes_false_and_exits_successfully(self):
        event = self.repo / 'event.json'
        event.write_text(json.dumps(self.event))
        output = self.repo / 'output'
        with patch.dict(os.environ, GITHUB_EVENT_PATH=str(event), GITHUB_OUTPUT=str(output)), \
                patch.object(POLICY, 'api', side_effect=RuntimeError('unavailable')), \
                patch('sys.argv', ['policy', '--github-output']):
            POLICY.main()
        self.assertEqual(output.read_text(), 'generated_only=false\n')

    def test_candidate_policy_cannot_replace_workflow_invoked_base_policy(self):
        import yaml
        doc = yaml.safe_load((ROOT / '.github/workflows/pr-gate.yml').read_text())
        step = next(s for s in doc['jobs']['format']['steps'] if s.get('id') == 'generated-only')
        self.write('scripts/ci/generated-output-diff.py', 'raise SystemExit("CANDIDATE EXECUTED")\n')
        self.commit(amend=True)
        # API unavailable yields false; the branch's replaced executable is never run.
        fake = self.repo / 'bin'
        fake.mkdir()
        (fake / 'gh').write_text('#!/bin/sh\nexit 1\n')
        (fake / 'gh').chmod(0o755)
        event = self.repo / 'event.json'; event.write_text(json.dumps(self.event))
        output = self.repo / 'output'
        result = subprocess.run(['bash', '-e', '-c', step['run']], capture_output=True, text=True,
                                env=dict(os.environ, TRUSTED_BASE_SHA=self.source, RUNNER_TEMP=str(self.repo),
                                         GITHUB_EVENT_PATH=str(event), GITHUB_OUTPUT=str(output),
                                         PATH=str(fake)+':'+os.environ['PATH']))
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertNotIn('CANDIDATE EXECUTED', result.stdout + result.stderr)
        self.assertNotIn('generated_only=true', output.read_text())


class WorkflowCompletion(unittest.TestCase):
    def test_named_jobs_keep_completion_and_every_product_step_is_gated(self):
        import yaml
        gate = yaml.safe_load((ROOT / '.github/workflows/pr-gate.yml').read_text())
        for job in ('format', 'pr-gate', 'ci-router-validation', 'required'):
            self.assertNotIn('if', gate['jobs'][job]) if job != 'required' else self.assertEqual(gate['jobs'][job]['if'], 'always()')
        self.assertEqual(gate['jobs']['required']['name'], 'PR Gate')
        self.assertEqual(gate['jobs']['required']['needs'], ['pr-gate', 'format', 'ci-router-validation'])
        # Evaluate actual Actions expressions for the expensive steps. True and
        # false/absent classification must respectively skip and preserve them.
        for verdict in ('true', 'false', ''):
            values = {'env.REVIEW_FIRST_MODE': 'observe', 'github.event_name': 'pull_request',
                      'github.run_attempt': 1, 'steps.docs-only.outputs.docs_only': 'false',
                      'steps.generated-only.outputs.generated_only': verdict,
                      'github.event.pull_request.head.repo.full_name': POLICY.REPOSITORY,
                      'github.repository': POLICY.REPOSITORY}
            def evaluate(condition):
                for variable in sorted(values, key=len, reverse=True):
                    condition = condition.replace(variable, repr(values[variable]))
                condition = condition.replace('&&', ' and ').replace('||', ' or ')
                return eval(condition, {'__builtins__': {}}, {})
            for job in ('format', 'pr-gate'):
                for step in gate['jobs'][job]['steps']:
                    if ('dotnet ' in step.get('run', '') or step.get('uses', '').startswith(('./.github/actions/', 'docker/'))
                            or 'scripts/ci/server-boot-smoke.sh' in step.get('run', '')):
                        self.assertEqual(bool(evaluate(step['if'])), verdict != 'true', step.get('name'))
        selection = gate['jobs']['affected-shards-select']
        self.assertIn('steps.generated-only.outputs.generated_only', selection['outputs']['skip'])
        for name in ('ogc-api-building-block-conformance', 'normalize-derived-artifacts'):
            doc = yaml.safe_load((ROOT / f'.github/workflows/{name}.yml').read_text())
            job = doc['jobs']['conformance' if name.startswith('ogc') else 'generate']
            classified = False
            for step in job['steps']:
                if step.get('id') == 'generated-only':
                    classified = True
                elif classified:
                    self.assertIn("steps.generated-only.outputs.generated_only != 'true'", step.get('if', ''))
            self.assertTrue(classified)
        normal = yaml.safe_load((ROOT / '.github/workflows/normalize-derived-artifacts.yml').read_text())
        self.assertEqual(normal['jobs']['produce']['if'], "needs.generate.outputs.generated_only != 'true'")
        consumer = yaml.safe_load((ROOT / '.github/workflows/normalize-derived-artifacts-consumer.yml').read_text())
        steps = consumer['jobs']['validate']['steps']
        resolve = next(s for s in steps if s.get('id') == 'artifact')
        self.assertIn('`${base}:scripts/ci/generated-output-diff.py`', resolve['with']['script'])
        for step in steps[steps.index(resolve)+1:]:
            self.assertIn("steps.artifact.outputs.generated_only != 'true'", step['if'])
        # The stable aggregate really exits success only for successful named
        # paths; it continues rejecting failed/skipped ordinary verification.
        aggregate = gate['jobs']['required']['steps'][0]['run']
        for result, expected in (('success', 0), ('failure', 1), ('skipped', 1)):
            run = subprocess.run(['bash', '-c', aggregate], capture_output=True,
                                 env=dict(os.environ, BUILD_AND_TEST_RESULT=result, FORMAT_RESULT='success', CI_ROUTER_RESULT='success'))
            self.assertEqual(run.returncode, expected)

    def test_producer_records_identity_after_validation_and_publication(self):
        text = (ROOT / '.github/workflows/generated-files-on-trunk.yml').read_text()
        self.assertLess(text.index('Validate authored inputs'), text.index('publish-generated-files.sh'))
        self.assertLess(text.index('publish-generated-files.sh'), text.index('Record validated generated-output identity'))
        self.assertIn('generated-output-proof-${{ github.run_id }}-attempt-${{ github.run_attempt }}', text)
        self.assertIn("output_tree_sha=git('HEAD^{tree}')", text)
        self.assertNotIn('pull_request:', text)

    def test_codeql_cannot_start_for_any_of_the_four_paths(self):
        import yaml
        import fnmatch
        doc = yaml.safe_load((ROOT / '.github/workflows/codeql.yml').read_text())
        triggers = doc.get('on', doc.get(True))
        filters = triggers['pull_request']['paths']
        diff = (ROOT / 'scripts/ci/fixtures/generated-output-5725.patch').read_text()
        for path in re.findall(r'^\+\+\+ b/(.+)$', diff, re.M):
            self.assertFalse(any(fnmatch.fnmatchcase(path, pattern) for pattern in filters), path)


if __name__ == '__main__':
    unittest.main(verbosity=2)
