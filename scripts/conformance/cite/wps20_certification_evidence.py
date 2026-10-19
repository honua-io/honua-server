"""Validate WPS ETS facets and separate supplemental axis evidence at normalization."""
from __future__ import annotations

import hashlib
import json
import sys
import xml.etree.ElementTree as ET
from datetime import datetime
from dataclasses import asdict
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from wps20_axis_probe import REQUEST, validate_response
from parse_wps20_results import parse_result, evaluate, PROFILE_CLASSES

ETS_SOURCE = 'e2acc691440fad98d32e873a6b7237c9d759b8df'
FACET_METHODS = {
    'positive': ['ValidEchoProcess', 'ValidGetCapabilitiesViaGETKVP', 'ValidDescribeProcessViaGETKVP'],
    'negative': ['ValidServiceName', 'ValidServiceVersion'],
    'media-schema': ['VerifyProcessXMLEncoding', 'VerifyProcessDataTypeXMLEncoding'],
}


def _read(path: Path) -> dict:
    value = json.loads(path.read_text())
    if not isinstance(value, dict):
        raise ValueError(f'{path.name} must be an object')
    return value


def _time(value: str) -> datetime:
    result = datetime.fromisoformat(value.replace('Z', '+00:00'))
    if result.tzinfo is None:
        raise ValueError('WPS evidence timestamps require timezone')
    return result


def evidence(directory: Path, source_sha: str, producer_sha: str, image_digest: str,
             started_at: str, completed_at: str) -> dict:
    provenance = _read(directory / 'honua-server-provenance.json')
    if (provenance.get('testedHonuaGitSha') != source_sha
            or provenance.get('checkedOutHonuaGitSha') != producer_sha
            or provenance.get('serverImageRevision') != source_sha
            or provenance.get('serverBuildMode') != 'prebuilt'
            or not str(provenance.get('requestedServerImage', '')).endswith('@' + image_digest)
            or not any(str(value).endswith('@' + image_digest) for value in provenance.get('serverImageRepoDigests', []))):
        raise ValueError('WPS actual container provenance does not match the exact candidate')
    summary = _read(directory / 'cite-wps20-summary.json')
    if (summary.get('sourceCommit') != ETS_SOURCE or summary.get('profile') != 'basic-async'
            or summary.get('status') != 'passed' or summary.get('etsExitCode') != 0
            or summary.get('accountingErrors') != []):
        raise ValueError('WPS upstream ETS result is incomplete or failed')
    # Resolve the exact runner-selected XML within the retained raw directory;
    # reject ambiguity instead of selecting the newest report at normalization.
    results = list((directory / 'raw').rglob('testng-results.xml'))
    if len(results) != 1:
        raise ValueError('WPS raw evidence requires exactly one TestNG result')
    _, classes, raw, unmatched, configuration, accounting = parse_result(results[0])
    selected = evaluate(classes, 'basic-async')
    expected = {'selectedClasses': list(PROFILE_CLASSES['basic-async']),
                'selectedTotals': asdict(selected), 'rawTotals': asdict(raw),
                'unmatchedTotals': asdict(unmatched),
                'configurationIssueTotals': asdict(configuration),
                'classes': {key: asdict(value) for key, value in classes.items()}}
    if accounting or not selected.clean() or not raw.clean():
        raise ValueError('WPS raw ETS accounting or method outcomes are not clean')
    if any(summary.get(key) != value for key, value in expected.items()):
        raise ValueError('WPS ETS summary does not match retained raw method accounting')
    root = ET.parse(results[0]).getroot()
    methods = {}
    for cls in root.findall('.//class'):
        if not str(cls.get('name', '')).endswith('.BasicTests'):
            continue
        for method in cls.findall('test-method'):
            if method.get('is-config', 'false').lower() == 'true':
                continue
            name = method.get('name')
            if name in methods:
                raise ValueError('WPS facet evidence contains duplicate method identities')
            methods[name] = method.get('status')
    for names in FACET_METHODS.values():
        if any(methods.get(name) != 'PASS' for name in names):
            raise ValueError('WPS required positive, negative, or XML-schema method did not pass')
    supplement = _read(directory / 'crs84-supplement.json')
    request = (directory / 'crs84-request.xml').read_bytes()
    response = (directory / 'crs84-response.xml').read_bytes()
    if (supplement.get('schema') != 'honua.wps-crs84-supplement/v1'
            or supplement.get('result') != 'pass' or supplement.get('error') is not None
            or request != REQUEST
            or supplement.get('request') != {'method': 'POST', 'path': '/wps', 'sha256': hashlib.sha256(request).hexdigest()}):
        raise ValueError('WPS supplemental HTTP request proof is missing or mismatched')
    observed = supplement.get('response', {})
    if observed.get('sha256') != hashlib.sha256(response).hexdigest() or observed.get('bytes') != len(response):
        raise ValueError('WPS supplemental response bytes do not match the observation')
    validate_response(observed.get('status'), observed.get('media_type'), response)
    if not (_time(started_at) <= _time(supplement['started_at']) <= _time(supplement['completed_at']) <= _time(completed_at)):
        raise ValueError('WPS supplemental HTTP observation is outside this execution')
    elapsed = supplement.get('elapsed_seconds')
    if isinstance(elapsed, bool) or not isinstance(elapsed, (int, float)) or not 0 <= elapsed <= 20:
        raise ValueError('WPS supplemental HTTP timing did not meet the declared bound')
    return {'upstream_ets': {'source_sha': ETS_SOURCE, 'facets': FACET_METHODS,
                             'raw_sha256': hashlib.sha256(results[0].read_bytes()).hexdigest()},
            'supplemental_crs_axis': supplement,
            'excluded': ['canonical durable geoprocessing', 'upstream ETS axis-order certification']}
