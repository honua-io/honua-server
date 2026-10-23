"""Negative controls for candidate binding and the installed proof's value oracle."""
import copy
import importlib.util
from pathlib import Path
import tempfile
import unittest
from unittest.mock import Mock

spec = importlib.util.spec_from_file_location("installed", Path(__file__).with_name("metadata-release-installed.py"))
installed = importlib.util.module_from_spec(spec)
spec.loader.exec_module(installed)


class CandidateBindingTests(unittest.TestCase):
    def parse(self, server):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "manifest.yaml"
            path.write_text("components:\n  honua-server:\n" + server + "  honua-console:\n    sha: \"" + "b" * 40 + "\"\n")
            return installed.pinned_server(path)

    def test_exact_source_and_digest_are_bound(self):
        candidate = self.parse('    sha: "' + 'a' * 40 + '"\n    image: "ghcr.io/honua-io/honua-server:nightly-test"\n    digest: "sha256:' + 'c' * 64 + '"\n')
        self.assertEqual(candidate["reference"], "ghcr.io/honua-io/honua-server@sha256:" + "c" * 64)
        self.assertEqual(candidate["sha"], "a" * 40)

    def test_floating_or_missing_digest_is_rejected(self):
        for digest in ('', '    digest: "latest"\n', '    digest: "sha256:abc"\n'):
            with self.subTest(digest=digest), self.assertRaises(ValueError):
                self.parse('    sha: "' + 'a' * 40 + '"\n    image: "ghcr.io/honua-io/honua-server:nightly"\n' + digest)

    def test_missing_server_sha_cannot_borrow_next_component_sha(self):
        with self.assertRaises(ValueError):
            self.parse('    image: "ghcr.io/honua-io/honua-server:nightly"\n    digest: "sha256:' + 'c' * 64 + '"\n')


class FeatureOracleTests(unittest.TestCase):
    def setUp(self):
        # These are fixture inputs, never observations captured from the server under test.
        coordinates = [(-122.4194, 37.7749), (-122.2711, 37.8044), (0, 51.4779), (-75, 0), (179.5, .5), (0, 86)]
        names = ["Harbor City", "Baytown", "Meridian Marker", "Equator Station", "Dateline Post", "Polar Outpost"]
        populations = [1000000, 430000, 0, 0, 0, 0]
        self.data = {"spatialReference": {"wkid": 4326}, "features": [
            {"attributes": {"name": name, "population": pop}, "geometry": {"x": xy[0], "y": xy[1]}}
            for name, pop, xy in zip(names, populations, coordinates)]}
        self.schema = {"fields": [{"name": "name"}, {"name": "population"}]}

    def verify(self, data=None, schema=None, edited=False):
        harness = installed.Harness({}, Path("/unused"))
        harness.request = Mock(side_effect=[data or self.data, schema or self.schema, None])
        return harness.features(edited=edited)

    def test_independent_fixture_values_pass(self):
        self.assertTrue(self.verify()["coordinatesAndValuesVerified"])

    def test_wrong_ordinate_value_srid_and_duplicate_feature_are_rejected(self):
        mutations = [
            lambda d: d["features"][0]["geometry"].update(x=-122.0),
            lambda d: d["features"][0]["attributes"].update(population=999999),
            lambda d: d["spatialReference"].update(wkid=3857),
            lambda d: d["features"].__setitem__(1, copy.deepcopy(d["features"][0])),
        ]
        for mutate in mutations:
            with self.subTest(mutation=mutate):
                data = copy.deepcopy(self.data)
                mutate(data)
                with self.assertRaises(AssertionError):
                    self.verify(data)

    def test_lost_committed_edit_is_rejected(self):
        with self.assertRaises(AssertionError):
            self.verify(edited=True)
        self.data["features"][0]["attributes"]["population"] = 1000007
        self.verify(edited=True)

    def test_exposed_candidate_schema_is_rejected(self):
        with self.assertRaises(AssertionError):
            self.verify(schema={"fields": [{"name": "owner_email"}]})


if __name__ == "__main__":
    unittest.main()
