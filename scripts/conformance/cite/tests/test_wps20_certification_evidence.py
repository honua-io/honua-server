import copy
import hashlib
import json
import sys
import tempfile
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parents[1]))
from wps20_certification_evidence import ETS_SOURCE, FACET_METHODS, evidence
from wps20_axis_probe import REQUEST
from parse_wps20_results import parse_result, evaluate, write_outputs
from test_wps20_axis_probe import RESPONSE

SHA = 'a' * 40
DIGEST = 'sha256:' + 'b' * 64
START = '2026-10-13T00:00:00Z'
END = '2026-10-13T00:01:00Z'


class WpsEvidenceTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.root = Path(self.tmp.name)
        self.write('honua-server-provenance.json', {'testedHonuaGitSha': SHA, 'checkedOutHonuaGitSha': SHA,
                   'serverImageRevision': SHA, 'serverBuildMode': 'prebuilt',
                   'requestedServerImage': 'ghcr.io/honua-io/honua-server@' + DIGEST,
                   'serverImageRepoDigests': ['ghcr.io/honua-io/honua-server@' + DIGEST]})
        self.write('cite-wps20-summary.json', {'sourceCommit': ETS_SOURCE, 'profile': 'basic-async',
                   'status': 'passed', 'etsExitCode': 0, 'accountingErrors': []})
        (self.root / 'raw').mkdir()
        methods = ''.join(f'<test-method name="{name}" status="PASS"/>' for names in FACET_METHODS.values() for name in names)
        (self.root / 'raw/testng-results.xml').write_text(f'<testng-results><suite><test><class name="org.opengis.cite.wps20.BasicTests">{methods}</class><class name="org.opengis.cite.wps20.Asynchronous"><test-method name="syntheticAsyncControl" status="PASS"/></class></test></suite></testng-results>')
        result, classes, raw, unmatched, configuration, errors = parse_result(self.root / 'raw/testng-results.xml')
        write_outputs(result, classes, raw, evaluate(classes, 'basic-async'), unmatched,
                      configuration, errors, 'basic-async', 0,
                      self.root / 'summary.md', self.root / 'cite-wps20-summary.json')
        (self.root / 'crs84-request.xml').write_bytes(REQUEST)
        (self.root / 'crs84-response.xml').write_bytes(RESPONSE)
        self.write('crs84-supplement.json', {'schema': 'honua.wps-crs84-supplement/v1', 'result': 'pass', 'error': None,
                   'started_at': START, 'completed_at': END, 'elapsed_seconds': 0.2,
                   'request': {'method': 'POST', 'path': '/wps', 'sha256': hashlib.sha256(REQUEST).hexdigest()},
                   'response': {'status': 200, 'media_type': 'application/xml', 'bytes': len(RESPONSE), 'sha256': hashlib.sha256(RESPONSE).hexdigest()}})

    def write(self, name, value):
        (self.root / name).write_text(json.dumps(value))

    def read(self, name):
        return json.loads((self.root / name).read_text())

    def verify(self):
        return evidence(self.root, SHA, SHA, DIGEST, START, END)

    def test_four_facets_keep_upstream_and_supplement_separate(self):
        proof = self.verify()
        self.assertEqual(set(proof['upstream_ets']['facets']), {'positive', 'negative', 'media-schema'})
        self.assertEqual(proof['supplemental_crs_axis']['result'], 'pass')
        self.assertIn('canonical durable geoprocessing', proof['excluded'])

    def test_missing_failed_or_skipped_upstream_facet_never_passes(self):
        path = self.root / 'raw/testng-results.xml'
        original = path.read_text()
        for names in FACET_METHODS.values():
            for outcome in ['SKIP', 'FAIL', 'UNKNOWN']:
                path.write_text(original.replace(f'name="{names[0]}" status="PASS"', f'name="{names[0]}" status="{outcome}"'))
                with self.subTest(method=names[0], status=outcome), self.assertRaises(ValueError): self.verify()
        path.write_text(original.replace('name="ValidEchoProcess"', 'name="not-governed"'))
        with self.assertRaises(ValueError): self.verify()

    def test_raw_accounting_cannot_be_hidden_by_passed_summary(self):
        path = self.root / 'raw/testng-results.xml'
        original = path.read_text()
        changes = [
            original.replace('name="syntheticAsyncControl" status="PASS"', 'name="syntheticAsyncControl" status="SKIP"'),
            original.replace('<testng-results>', '<testng-results total="999" passed="999">'),
            original.replace('</test>', '<class name="unknown"><test-method name="unclassified" status="PASS"/></class></test>'),
            original.replace('</class>', '<test-method name="setup" is-config="true" status="FAIL"/></class>', 1),
            original.replace('status="PASS"/>', 'status="PASS"/><test-method name="extra" status="PASS"/>', 1),
        ]
        for changed in changes:
            path.write_text(changed)
            with self.subTest(raw=changed), self.assertRaises(ValueError):
                self.verify()
        path.write_text(original)
        summary = self.read('cite-wps20-summary.json')
        summary['selectedTotals']['total'] += 1
        self.write('cite-wps20-summary.json', summary)
        with self.assertRaisesRegex(ValueError, 'summary'):
            self.verify()

    def test_actual_candidate_provenance_must_match(self):
        original = self.read('honua-server-provenance.json')
        for field in ['testedHonuaGitSha', 'checkedOutHonuaGitSha', 'serverImageRevision', 'requestedServerImage', 'serverBuildMode']:
            value = copy.deepcopy(original)
            value[field] = 'wrong'
            self.write('honua-server-provenance.json', value)
            with self.subTest(field=field), self.assertRaises(ValueError): self.verify()

    def test_swapped_response_cannot_pass_even_with_recomputed_hash(self):
        changed = RESPONSE.replace(b'-123.5 37.25', b'37.25 -123.5')
        (self.root / 'crs84-response.xml').write_bytes(changed)
        receipt = self.read('crs84-supplement.json')
        receipt['response'].update(sha256=hashlib.sha256(changed).hexdigest(), bytes=len(changed))
        self.write('crs84-supplement.json', receipt)
        with self.assertRaisesRegex(ValueError, 'order'): self.verify()

    def test_absent_or_stale_observation_refuses(self):
        receipt = self.read('crs84-supplement.json')
        receipt['started_at'] = '2026-10-12T00:00:00Z'
        self.write('crs84-supplement.json', receipt)
        with self.assertRaisesRegex(ValueError, 'outside'): self.verify()
        (self.root / 'crs84-supplement.json').unlink()
        with self.assertRaises(OSError): self.verify()


if __name__ == '__main__': unittest.main()
