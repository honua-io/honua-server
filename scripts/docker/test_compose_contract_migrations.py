"""Keep the manual upgrade's contract approval scoped to reviewed migrations."""
import json
from pathlib import Path
import subprocess
import sys
import unittest
from unittest.mock import patch

from compose_contract_migrations import migrate_reviewed_contracts


SCRIPT = 'Honua.Server.Migrations.183_RemoveUnusedFeatureIndexes.sql'


def summary(code='contract-approval-required', exit_code=5):
    return dict(format='honua.migrate-only/v1', code=code, exitCode=exit_code,
                schemaVerified=False, pendingScripts=[dict(script=SCRIPT, classification='contract-annotated')],
                contractApproval=dict(nonce='reviewed-plan-nonce'))


def result(data, returncode=5):
    return subprocess.CompletedProcess([], returncode, 'startup log\n' + json.dumps(data), '')


class ContractMigrationTests(unittest.TestCase):
    def migrate(self):
        migrate_reviewed_contracts(['docker', 'compose'], {'PRIVATE': 'secret'})

    def test_refuses_then_approves_exact_nonce_and_requires_success(self):
        success = summary('success', 0)
        success['schemaVerified'] = True
        with patch('compose_contract_migrations.subprocess.run', side_effect=[result(summary()), result(success, 0)]) as run:
            self.migrate()
        first, second = [call.args[0] for call in run.call_args_list]
        self.assertNotIn('HONUA_APPROVE_CONTRACT_MIGRATIONS=reviewed-plan-nonce', first)
        self.assertIn('HONUA_APPROVE_CONTRACT_MIGRATIONS=reviewed-plan-nonce', second)
        self.assertIn('HONUA_MIGRATE_ONLY=true', first)
        self.assertIn('HONUA_APPROVE_CONTRACT_MIGRATIONS=', first)
        for command in (first, second):
            self.assertIn('Database__MigrationSafety__ContractApplyPolicy=Gate', command)
        self.assertEqual({'PRIVATE': 'secret'}, run.call_args.kwargs['env'])

    def test_unknown_script_classification_or_missing_nonce_never_gets_approval(self):
        unknown = summary()
        unknown['pendingScripts'][0]['script'] = 'Honua.Server.Migrations.999_DropCustomers.sql'
        unclassified = summary()
        unclassified['pendingScripts'][0]['classification'] = 'unclassified'
        missing = summary()
        missing['contractApproval']['nonce'] = ''
        for data in (unknown, unclassified, missing):
            with self.subTest(data=data), patch('compose_contract_migrations.subprocess.run', return_value=result(data)) as run:
                with self.assertRaises(AssertionError):
                    self.migrate()
                self.assertEqual(1, run.call_count)

    def test_verified_first_run_needs_no_approval(self):
        success = dict(format='honua.migrate-only/v1', code='success', exitCode=0, schemaVerified=True)
        with patch('compose_contract_migrations.subprocess.run', return_value=result(success, 0)) as run:
            self.migrate()
        self.assertEqual(1, run.call_count)

    def test_other_refusals_and_unannotated_contracts_name_the_cause(self):
        refusal = summary('database-unavailable')
        unannotated = summary()
        unannotated['pendingScripts'][0]['classification'] = 'contract-unannotated'
        for data, cause in ((refusal, 'database-unavailable'), (unannotated, SCRIPT)):
            with self.subTest(cause=cause), patch('compose_contract_migrations.subprocess.run', return_value=result(data)) as run:
                with self.assertRaisesRegex(AssertionError, cause):
                    self.migrate()
                self.assertEqual(1, run.call_count)

    def test_rejection_holds_under_optimized_python(self):
        script = ('import sys; sys.path.insert(0, sys.argv[1]); import compose_contract_migrations as m\n'
                  'try:\n    m.reviewed_nonce({"code": "other"})\nexcept AssertionError:\n    sys.exit(0)\nsys.exit(1)')
        here = str(Path(__file__).resolve().parent)
        self.assertEqual(0, subprocess.run([sys.executable, '-O', '-c', script, here]).returncode)

    def test_changed_reviewed_script_never_gets_approval(self):
        with patch('compose_contract_migrations.subprocess.run', return_value=result(summary())) as run, \
                patch('compose_contract_migrations.Path.read_bytes', return_value=b'changed SQL'):
            with self.assertRaisesRegex(AssertionError, 'reviewed migration bytes'):
                self.migrate()
        self.assertEqual(1, run.call_count)

    def test_expand_scripts_can_accompany_the_reviewed_contract(self):
        data = summary()
        data['pendingScripts'].append(dict(script='Honua.Server.Migrations.184_AddIndex.sql', classification='expand'))
        success = dict(format='honua.migrate-only/v1', exitCode=0, schemaVerified=True)
        with patch('compose_contract_migrations.subprocess.run', side_effect=[result(data), result(success, 0)]):
            self.migrate()

    def test_malformed_failed_or_unverified_migration_never_passes(self):
        invalid = [subprocess.CompletedProcess([], 5, 'PRIVATE OUTPUT', 'PRIVATE STDERR'),
                   result(summary(), 1), result(dict(format='other', exitCode=5)),
                   result(dict(format='honua.migrate-only/v1', exitCode=0, schemaVerified=False), 0)]
        for response in invalid:
            with self.subTest(response=response), patch('compose_contract_migrations.subprocess.run', return_value=response) as run:
                with self.assertRaises(AssertionError) as caught:
                    self.migrate()
                self.assertNotIn('PRIVATE', str(caught.exception))
                self.assertEqual(1, run.call_count)

    def test_failed_or_unverified_approved_run_stops_upgrade(self):
        for response in (result(summary()), result(dict(format='honua.migrate-only/v1', exitCode=0, schemaVerified=False), 0)):
            with patch('compose_contract_migrations.subprocess.run', side_effect=[result(summary()), response]):
                with self.assertRaises(AssertionError):
                    self.migrate()


if __name__ == '__main__':
    unittest.main()
