"""Bind harness receipts to the exact implemented local contract and dispatch."""
import json
from pathlib import Path

from producer_context import load_envelope, run_identity


def operation_key(row):
    return row['capability_key'], row['surface'], row['operation']


def requirement_posture(assignment, contract):
    """Return the maturity and tier a governed row must carry.

    Omitted fields stay supported at the contract tier. A declared preview row
    is the GP preview floor: required at no tier, which the catalog records as
    required_tier release. Any other pair is refused. The caller still compares
    the context exactly, so an undeclared downgrade cannot pass.
    """
    maturity = assignment['maturity'] if 'maturity' in assignment else 'supported'
    required_tier = assignment['required_tier'] if 'required_tier' in assignment else contract['required_tier']
    if maturity not in {'supported', 'preview'} or not isinstance(required_tier, str) or not required_tier:
        raise ValueError('local harness maturity is not a governed posture')
    if maturity == 'supported' and required_tier != contract['required_tier']:
        raise ValueError('supported harness operation must keep the contract tier')
    if maturity == 'preview' and required_tier != 'release':
        raise ValueError('preview harness operation is required at no tier')
    return maturity, required_tier


def expected_requirements(contract, matrix, producer_sha):
    if contract['deployment_target'] != 'source-test-host':
        raise ValueError('protocol harness requires source-test-host execution')
    capabilities = {}
    for capability in matrix['capabilities']:
        key = capability['key']
        if key in capabilities:
            raise ValueError('local capability matrix repeats a capability')
        capabilities[key] = capability
    expected = {}
    declared = set()
    for assignment in contract['assignments']:
        key = operation_key(assignment)
        if key in declared:
            raise ValueError('local harness repeats an operation identity')
        declared.add(key)
        capability = capabilities.get(assignment['capability_key'])
        if capability is None:
            raise ValueError('local harness capability is absent from the source matrix')
        if not capability.get('maturity', {}).get('implemented'):
            continue
        maturity, required_tier = requirement_posture(assignment, contract)
        expected[key] = {
            **{field: assignment[field] for field in (
                'capability_key', 'surface', 'operation', 'test_ids', 'scenario_facets')},
            'canonical_client': contract['canonical_client'],
            'client_lane': contract['client_lane'],
            'client_version': f'source@{producer_sha}',
            'deployment_target': contract['deployment_target'],
            'fixture_revision': f'server-test-fixtures@{producer_sha}',
            'contract_revision': f"server-protocol-harness@{contract['revision']}+{producer_sha}",
            'auth_policy_revision': contract['auth_policy_revision'],
            'maturity': maturity,
            'required_tier': required_tier,
        }
    if not expected:
        raise ValueError('source matrix has no implemented harness operations')
    return expected


def bind_context(contract, args):
    if args.source_sha != args.producer_source_sha:
        raise ValueError('source-host candidate and producer SHAs must be identical')
    context = load_envelope(
        args.requirements_context, 'server-protocol-harness', args.source_sha,
        args.producer_source_sha, args.image_digest, args.candidate_cut_at,
    )
    matrix = json.loads(Path(args.matrix).read_text(encoding='utf-8'))
    expected = expected_requirements(contract, matrix, args.producer_source_sha)
    rows = context['requirements']
    if not isinstance(rows, list) or len(rows) != len(expected):
        raise ValueError('requirements context must contain the complete implemented harness roster')
    seen = set()
    for row in rows:
        if not isinstance(row, dict):
            raise ValueError('requirements context row must be an object')
        try:
            key = operation_key(row)
            duplicate = key in seen
        except (KeyError, TypeError) as error:
            raise ValueError('requirements context has invalid operation identity') from error
        if duplicate or key not in expected or row != expected[key]:
            raise ValueError('requirements context has duplicate, unknown or altered harness operation')
        seen.add(key)
    execution = run_identity(args.producer_source_sha, 'protocol-harness-certification.yml')
    return context, expected, execution
