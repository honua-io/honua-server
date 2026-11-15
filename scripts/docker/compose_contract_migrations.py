"""Run the manual lifecycle's reviewed contract migration before starting Honua."""
import hashlib
import json
from pathlib import Path
import subprocess


CONTRACT_APPROVAL_REQUIRED = 5
MAX_NONCE_LENGTH = 256

REVIEWED_CONTRACTS = {
    'Honua.Server.Migrations.183_RemoveUnusedFeatureIndexes.sql':
        '8fa24e8ba3c540117e5ce6061374ac6df6525cf91f195bddbcede38f65fbb9dd',
}
MIGRATIONS = Path(__file__).resolve().parents[2] / 'src/Honua.Server/Migrations'


def migration_summary(result):
    for line in reversed(result.stdout.splitlines()):
        try:
            summary = json.loads(line)
        except json.JSONDecodeError:
            continue
        if isinstance(summary, dict) and summary.get('format') == 'honua.migrate-only/v1':
            assert summary.get('exitCode') == result.returncode, 'migration summary exit code mismatch'
            return summary
    raise AssertionError('migrate-only returned no supported summary')


def reviewed_nonce(summary):
    assert summary.get('code') == 'contract-approval-required', 'migration refusal was not contract approval'
    pending = summary.get('pendingScripts')
    assert isinstance(pending, list) and pending, 'migration plan has no pending scripts'
    contracts = []
    for script in pending:
        assert isinstance(script, dict), 'invalid pending migration'
        if script.get('classification') == 'expand':
            continue
        name = script.get('script')
        assert script.get('classification') == 'contract-annotated' and name in REVIEWED_CONTRACTS, \
            'pending contract migration requires explicit review'
        path = MIGRATIONS / name.removeprefix('Honua.Server.Migrations.')
        assert hashlib.sha256(path.read_bytes()).hexdigest() == REVIEWED_CONTRACTS[name], \
            'reviewed migration bytes changed'
        contracts.append(name)
    assert contracts, 'migration plan has no reviewed contract'
    approval = summary.get('contractApproval')
    assert isinstance(approval, dict), 'migration plan has no approval challenge'
    nonce = approval.get('nonce')
    assert isinstance(nonce, str) and nonce.strip() and len(nonce) <= MAX_NONCE_LENGTH, 'invalid approval challenge'
    return nonce


def migrate_reviewed_contracts(compose, env):
    command = compose + ['run', '--rm', '--no-deps', '-T', '-e', 'HONUA_MIGRATE_ONLY=true',
                         '-e', 'Database__MigrationSafety__ContractApplyPolicy=Gate']
    result = subprocess.run(command + ['-e', 'HONUA_APPROVE_CONTRACT_MIGRATIONS=', 'honua'], env=env, text=True,
                            stdout=subprocess.PIPE, stderr=subprocess.PIPE, timeout=600)
    summary = migration_summary(result)
    if result.returncode == CONTRACT_APPROVAL_REQUIRED:
        nonce = reviewed_nonce(summary)
        result = subprocess.run(command + ['-e', f'HONUA_APPROVE_CONTRACT_MIGRATIONS={nonce}', 'honua'],
                                env=env, text=True, stdout=subprocess.PIPE, stderr=subprocess.PIPE, timeout=600)
        summary = migration_summary(result)
    assert result.returncode == 0 and summary.get('schemaVerified') is True, \
        'candidate migrate-only did not finish with a verified schema'
