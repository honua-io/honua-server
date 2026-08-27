import importlib.util
import json
import unittest
from pathlib import Path


ROOT = Path(__file__).resolve().parents[3]
SPEC = importlib.util.spec_from_file_location(
    "deployment_capability_profile",
    ROOT / "scripts/deployment/generate-capability-profile.py",
)
MODULE = importlib.util.module_from_spec(SPEC)
assert SPEC.loader
SPEC.loader.exec_module(MODULE)


class DeploymentCapabilityProfileTests(unittest.TestCase):
    def setUp(self):
        self.catalog = {
            "serve.wfs": {"key": "serve.wfs", "edition": "Community"},
            "editing.featureserver-edits": {"key": "editing.featureserver-edits", "edition": "Pro"},
            "identity.multi-provider": {"key": "identity.multi-provider", "edition": "Enterprise"},
        }

    def test_selection_is_validated_deduplicated_and_deterministic(self):
        keys = MODULE.parse_capabilities(
            ["serve.wfs,editing.featureserver-edits", "serve.wfs"], self.catalog
        )
        self.assertEqual(keys, ["editing.featureserver-edits", "serve.wfs"])
        with self.assertRaisesRegex(MODULE.ProfileError, "unknown capability"):
            MODULE.parse_capabilities(["serve.unknown"], self.catalog)
        with self.assertRaisesRegex(MODULE.ProfileError, "malformed capability"):
            MODULE.parse_capabilities(["serve.wfs\nINJECT=true"], self.catalog)

    def test_edition_and_capacity_are_independent_decisions(self):
        profile = MODULE.build_profile(
            ["identity.multi-provider", "serve.wfs"], 8, self.catalog
        )
        self.assertEqual(profile["requiredEdition"], "Enterprise")
        self.assertEqual(profile["capacitySuggestion"]["band"], "Team")
        self.assertEqual(profile["capacitySuggestion"]["annualPriceUsd"], 24000)
        self.assertFalse(profile["security"]["grantsEntitlements"])

    def test_private_band_requires_quote_for_paid_editions(self):
        profile = MODULE.build_profile(["editing.featureserver-edits"], 26, self.catalog)
        self.assertEqual(profile["capacitySuggestion"]["band"], "Private")
        self.assertIsNone(profile["capacitySuggestion"]["annualPriceUsd"])
        self.assertTrue(profile["capacitySuggestion"]["quoteRequired"])

    def test_outputs_round_trip_exactly_selected_capabilities(self):
        selected = ["editing.featureserver-edits", "serve.wfs"]
        profile = MODULE.build_profile(selected, 2, self.catalog)
        expected = ",".join(selected)

        canonical = json.loads(MODULE.render_profile(profile, "json"))
        compose = json.loads(MODULE.render_profile(profile, "compose"))
        helm = json.loads(MODULE.render_profile(profile, "helm"))
        dotenv = dict(
            line.split("=", 1)
            for line in MODULE.render_profile(profile, "env").splitlines()
            if line and not line.startswith("#")
        )

        self.assertEqual(canonical["capabilities"], selected)
        self.assertEqual(canonical["schemaVersion"], "1.0.0")
        self.assertEqual(canonical["$schema"], "https://honua.io/schemas/deployment-profile.v1.schema.json")
        self.assertEqual(compose["services"]["honua"]["environment"]["DeploymentProfile__EnabledCapabilities"], expected)
        self.assertEqual(helm["config"]["env"]["DeploymentProfile__EnabledCapabilities"], expected)
        self.assertEqual(dotenv["DeploymentProfile__EnabledCapabilities"], expected)

    def test_community_profile_does_not_suggest_a_license_charge(self):
        profile = MODULE.build_profile(["serve.wfs"], 40, self.catalog)
        self.assertEqual(profile["requiredEdition"], "Community")
        self.assertEqual(profile["capacitySuggestion"]["annualPriceUsd"], 0)
        self.assertFalse(profile["capacitySuggestion"]["quoteRequired"])

    def test_descriptive_keys_cannot_enter_a_deployment_profile(self):
        catalog = dict(self.catalog)
        catalog["provider.example"] = {
            "key": "provider.example", "edition": "Enterprise",
            "status": MODULE.EXPERIMENTAL_STATUS,
        }

        # No route resolves to a descriptive key, so a profile enabling one serves nothing while
        # still deriving requiredEdition -- and therefore a price band -- from its edition.
        with self.assertRaisesRegex(MODULE.ProfileError, "descriptive-only capability"):
            MODULE.parse_capabilities(["provider.example"], catalog)
        with self.assertRaisesRegex(MODULE.ProfileError, "descriptive-only capability"):
            MODULE.parse_capabilities(["serve.wfs,provider.example"], catalog)

        self.assertEqual(MODULE.parse_capabilities(["serve.wfs"], catalog), ["serve.wfs"])

    def test_committed_catalog_descriptive_keys_are_rejected(self):
        # Contract test against the shipped artifact: whatever CapabilityKeyCatalog marks
        # descriptive must stay un-deployable, so the two cannot drift apart.
        catalog = MODULE.load_catalog()
        descriptive = sorted(
            key for key, item in catalog.items()
            if item.get("status") == MODULE.EXPERIMENTAL_STATUS
        )
        self.assertTrue(descriptive, "committed catalog carries no descriptive keys to exercise")
        for key in descriptive:
            with self.assertRaisesRegex(MODULE.ProfileError, "descriptive-only capability"):
                MODULE.parse_capabilities([key], catalog)


if __name__ == "__main__":
    unittest.main()
