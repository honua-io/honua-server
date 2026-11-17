#!/usr/bin/env python3
"""Exercise the router's actual jq schema against malformed override priorities."""

import copy
import json
import re
import subprocess
import unittest
from pathlib import Path


ROOT = Path(__file__).resolve().parents[3]
SCRIPT = (ROOT / "scripts/ci/validate-ci-router.sh").read_text()
SCHEMA = re.search(r"validate_shard_structure\(\) \{\njq -e '(.*?)' ", SCRIPT, re.S).group(1)
CONFIG = json.loads((ROOT / ".github/ci-shards.json").read_text())


def accepts(config):
    result = subprocess.run(
        ["jq", "-e", SCHEMA], input=json.dumps(config), text=True, capture_output=True
    )
    return result.returncode == 0


class ShardStructureTests(unittest.TestCase):
    def test_override_priority_accepts_only_boolean_or_absent(self):
        for value in (True, False, "true", 1, [], {}, None):
            with self.subTest(value=value):
                config = copy.deepcopy(CONFIG)
                config["targeted_override_prefixes"][-1]["prioritize"] = value
                self.assertEqual(accepts(config), isinstance(value, bool))
        config = copy.deepcopy(CONFIG)
        for override in config["targeted_override_prefixes"]:
            override.pop("prioritize", None)
        self.assertTrue(accepts(config))


if __name__ == "__main__":
    unittest.main()
