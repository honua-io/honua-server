"""Fail-closed data-only CITE context admission from the staged release denominator."""
import json
import re
import sys
from pathlib import Path

KEYS = {'capability_key', 'surface', 'operation', 'canonical_client', 'client_version',
        'client_lane', 'deployment_target', 'fixture_revision', 'contract_revision',
        'auth_policy_revision', 'scenario_facets', 'maturity', 'required_tier'}
sys.path.insert(0, str(Path(__file__).resolve().parents[2] / 'certification'))
from producer_context import load_envelope


def load_context(path, source_sha, producer_sha, image_digest, cut_at, surfaces, fixtures):
    if source_sha != producer_sha:
        raise ValueError('CITE requires candidate and producer from the same server source')
    value = load_envelope(path, 'honua-server-cite', source_sha, producer_sha, image_digest, cut_at)
    # The checked-out matrix, not input JSON, defines which of our fixed suite
    # mappings exists at this source. It supplies no execution verdict.
    root = Path(__file__).resolve().parents[3]
    matrix = json.loads((root / 'docs/gis/data/capability-matrix.v1.json').read_text())
    expected = {}
    for capability in matrix['capabilities']:
        for cite in capability.get('cite', []):
            suite = cite['suite']
            surface = re.sub(r'[^a-z0-9]+', '-', suite.lower()).strip('-')
            if surface in surfaces:
                expected[surface] = (capability['key'], suite)
    rows = value['requirements']
    if not isinstance(rows, list) or not rows:
        raise ValueError('requirements context requires nonempty rows')
    seen = set()
    for row in rows:
        if not isinstance(row, dict) or set(row) != KEYS:
            raise ValueError('CITE context row has unknown or missing fields')
        surface = row['surface']
        if surface not in expected or surface in seen:
            raise ValueError('CITE context row is unknown or duplicated')
        seen.add(surface)
        capability, suite = expected[surface]
        exact = {'capability_key': capability, 'surface': surface, 'operation': capability,
                 'canonical_client': 'OGC CITE', 'client_version': suite, 'client_lane': 'cite-' + surface,
                 'deployment_target': 'local-docker', 'fixture_revision': fixtures[surfaces[surface]] + '@{source_sha}',
                 'contract_revision': 'server-capability-matrix@' + source_sha,
                 'auth_policy_revision': 'anonymous-public-v1',
                 'scenario_facets': ['positive', 'negative', 'crs-axis', 'media-schema'],
                 'maturity': 'supported', 'required_tier': 'nightly'}
        if row != exact:
            raise ValueError('CITE context differs from the source-owned operation/fixture/facet contract')
    if seen != set(expected):
        raise ValueError('CITE context omits source-owned suite requirements')
    return {'revision': value['requirements_revision'], 'requirements': rows}
