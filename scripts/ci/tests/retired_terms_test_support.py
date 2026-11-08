import importlib.util
import json
from pathlib import Path
import tempfile
import unittest


CHECKER = Path(__file__).resolve().parents[1] / "check-retired-terms.py"
spec = importlib.util.spec_from_file_location("retired_terms", CHECKER)
checker = importlib.util.module_from_spec(spec)
spec.loader.exec_module(checker)


class RetiredTermsTestCase(unittest.TestCase):
    def setUp(self):
        self.unpatched_git = checker.git
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.root = Path(self.directory.name)
        self.policy = self.root / "policy.json"
        self.policy.write_text(json.dumps({"version": 1, "rules": [
            {"id": "legacy", "literal": "retired phrase", "replacement": "canonical phrase"}
        ], "reviewRules": []}))
        self.allowlist = self.root / "allowlist.json"
        self.run_git("init", "-q")
        self.write("page.md", "retired phrase already exists\nunchanged\n")
        self.run_git("add", ".")
        self.run_git("-c", "user.name=Test", "-c", "user.email=test@example.invalid",
                     "commit", "-qm", "baseline")

    def run_git(self, *arguments):
        return checker.git(self.root, *arguments)

    def write(self, name, text):
        path = self.root / name
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(text)

    def check(self):
        return checker.VocabularyCheck(self.root, self.policy, self.allowlist)

    def scan(self):
        return self.check().check_changes("HEAD")

