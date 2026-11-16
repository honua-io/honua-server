#!/usr/bin/env python3
"""Exercise the retired-cache policy against committed Git changes."""

from pathlib import Path
import subprocess
import tempfile
import unittest

SCRIPT = Path(__file__).with_name("check-retired-cache.py")


class RetiredCacheTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.root = Path(self.directory.name)
        self.git("init", "-q")
        self.write("existing.txt", "Redis is being removed\nunchanged\n")
        self.commit()
        self.base = self.git("rev-parse", "HEAD").strip()

    def git(self, *args):
        return subprocess.check_output(["git", *args], cwd=self.root, text=True)

    def write(self, name, content):
        path = self.root / name
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(content)

    def commit(self):
        self.git("add", ".")
        self.git("-c", "user.name=Fixture", "-c", "user.email=fixture@example.test", "commit", "-qm", "fixture")

    def check(self, base=None):
        return subprocess.run(["python3", str(SCRIPT), "--base", base or self.base],
                              cwd=self.root, capture_output=True, text=True)

    def test_existing_mentions_and_removals_pass(self):
        self.write("existing.txt", "Redis is being removed\nchanged\n")
        self.commit()
        self.assertEqual(self.check().returncode, 0)
        self.write("existing.txt", "removed\n")
        self.commit()
        self.assertEqual(self.check().returncode, 0)

    def test_new_mentions_fail_in_source_config_and_docs(self):
        for path in ["src/cache.cs", "appsettings.json", "docs/cache.md", "space name.txt"]:
            self.write(path, "prefix\nStackExchange.ReDiS\n")
        self.commit()
        result = self.check()
        self.assertEqual(result.returncode, 1, result.stderr)
        for path in ["src/cache.cs", "appsettings.json", "docs/cache.md", "space name.txt"]:
            self.assertIn(f"{path}:2:", result.stdout)

    def test_history_notices_and_policy_sources_pass(self):
        for path in ["CHANGELOG.md", "docs/changelog/2026.md", "THIRD-PARTY-NOTICES.md",
                     "scripts/ci/check-retired-cache.py", "scripts/ci/check-retired-cache.test.py"]:
            self.write(path, "Redis\n")
        self.commit()
        self.assertEqual(self.check().returncode, 0)

    def test_ci_routing_data_mirroring_tests_passes(self):
        routing = [".github/ci-shards.json", "scripts/ci/fixtures/full-run-capacity-contracts.json",
                   "scripts/ci/fixtures/live-output-capacity-contracts.json",
                   "scripts/ci/test-shard-image-selection.py"]
        for path in routing:
            self.write(path, "Redis\n")
        self.commit()
        self.assertEqual(self.check().returncode, 0)

    def test_other_ci_files_are_not_routing_data(self):
        for path in [".github/workflows/ci-shards.yml", "scripts/ci/fixtures/other-contracts.json",
                     ".github/ci-shards.json.bak"]:
            self.write(path, "Redis\n")
        self.commit()
        result = self.check()
        self.assertEqual(result.returncode, 1, result.stderr)
        for path in [".github/workflows/ci-shards.yml", "scripts/ci/fixtures/other-contracts.json",
                     ".github/ci-shards.json.bak"]:
            self.assertIn(f"{path}:1:", result.stdout)

    def test_rename_cannot_move_routing_data_into_source(self):
        self.write(".github/ci-shards.json", "Redis\n")
        self.commit()
        base = self.git("rev-parse", "HEAD").strip()
        (self.root / "src").mkdir()
        self.git("mv", ".github/ci-shards.json", "src/shards.json")
        self.commit()
        result = self.check(base)
        self.assertEqual(result.returncode, 1, result.stderr)
        self.assertIn("src/shards.json:1:", result.stdout)

    def test_unrelated_words_and_identifiers_pass(self):
        self.write("src/locks.cs", "AcquireDistributedLockAsync();\n")
        self.write("LICENSE.txt", "Redistribution and use are permitted.\nredistribution\n")
        self.commit()
        result = self.check()
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)

    def test_retired_cache_identifier_tokens_fail(self):
        references = ["RedisCache", "AddStackExchangeRedisCache", "redisClient",
                      "REDIS_URL", "Redis7", "IRedisCache", "StackExchange.ReDiS"]
        for index, reference in enumerate(references):
            self.write(f"src/reference-{index}.txt", reference + "\n")
        self.commit()
        result = self.check()
        self.assertEqual(result.returncode, 1, result.stderr)
        for index in range(len(references)):
            self.assertIn(f"src/reference-{index}.txt:1:", result.stdout)

    def test_similar_names_are_not_exempt(self):
        self.write("src/ChangeLogRedis.cs", "Redis\n")
        self.write("docs/third-party-notices-guide.md", "Redis\n")
        self.commit()
        self.assertEqual(self.check().returncode, 1)

    def test_rename_cannot_reintroduce_history_into_source(self):
        self.write("CHANGELOG.md", "Redis\n")
        self.commit()
        base = self.git("rev-parse", "HEAD").strip()
        self.git("mv", "CHANGELOG.md", "shipping.txt")
        self.commit()
        self.assertEqual(self.check(base).returncode, 1)

    def test_added_lines_resembling_patch_headers_fail(self):
        self.write("patch.txt", "++Redis\n")
        self.commit()
        self.assertEqual(self.check().returncode, 1)

    def test_binary_content_cannot_hide_mentions(self):
        (self.root / "settings.bin").write_bytes(b"prefix\x00Redis\n")
        self.commit()
        self.assertEqual(self.check().returncode, 1)

    def test_wide_character_project_files_cannot_hide_dependencies(self):
        project = '<Project><ItemGroup><PackageReference Include="StackExchange.Redis" /></ItemGroup></Project>\n'
        encodings = ["utf-16", "utf-16-le", "utf-16-be", "utf-32-le", "utf-32-be"]
        for encoding in encodings:
            (self.root / f"{encoding}.csproj").write_bytes(project.encode(encoding))
        self.commit()
        result = self.check()
        self.assertEqual(result.returncode, 1, result.stderr)
        for encoding in encodings:
            self.assertIn(f"{encoding}.csproj:1:", result.stdout)

    def test_non_lf_separators_cannot_hide_mentions(self):
        for index, separator in enumerate(["\f", "\r", "\v", "\u0085", "\u2028", "\u2029"]):
            self.write(f"separator-{index}.txt", f"prefix{separator}Redis\n")
        self.commit()
        result = self.check()
        self.assertEqual(result.returncode, 1, result.stderr)
        for index in range(6):
            self.assertIn(f"separator-{index}.txt:1:", result.stdout)

    def test_literal_pathspecs_cannot_exempt_a_matching_filename(self):
        self.write("[ab].txt", "Redis\n")
        self.write("a.txt", "allowed\n")
        self.commit()
        result = self.check()
        self.assertEqual(result.returncode, 1, result.stderr)
        self.assertIn("[ab].txt:1:", result.stdout)

    def test_invalid_base_fails_closed(self):
        self.assertNotEqual(self.check("missing-ref").returncode, 0)

    def test_trunk_removals_do_not_count_as_branch_additions(self):
        self.write("existing.txt", "removed on trunk\n")
        self.commit()
        trunk = self.git("rev-parse", "HEAD").strip()
        self.git("checkout", "-q", "--detach", self.base)
        self.write("unrelated.txt", "allowed\n")
        self.commit()
        self.assertEqual(self.check(trunk).returncode, 0)
        self.write("new.txt", "Redis\n")
        self.commit()
        result = self.check(trunk)
        self.assertEqual(result.returncode, 1, result.stderr)
        self.assertIn("new.txt:1:", result.stdout)
        self.assertNotIn("existing.txt", result.stdout)


if __name__ == "__main__":
    unittest.main()
