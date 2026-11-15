#!/usr/bin/env python3
"""Exercise migration admission against diverged git histories."""

from pathlib import Path
import subprocess
import hashlib
import tempfile
import unittest

SCRIPT = Path(__file__).with_name("check-migration-numbers.py")
SERVER = "src/Honua.Server/Migrations"
POSTGRES = "src/Honua.Db/Postgres/Migrations"


class MigrationNumberTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.root = Path(self.directory.name)
        self.git("init", "-q")
        self.add(SERVER, "001_Initial.sql")
        self.base = self.commit()
        self.git("branch", "trunk")

    def git(self, *args):
        return subprocess.check_output(["git", *args], cwd=self.root, text=True).strip()

    def add(self, directory, name, content="SELECT 1;", claim=True):
        path = self.root / directory / name
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(content)
        pin = self.root / "eng/schema/migration-hashes" / directory / (name + ".sha256")
        pin.parent.mkdir(parents=True, exist_ok=True)
        pin.write_text(hashlib.sha256(content.encode()).hexdigest() + "\n")
        if claim:
            marker = self.root / "eng/schema/migration-numbers" / directory / (name.split("_")[0] + ".txt")
            marker.parent.mkdir(parents=True, exist_ok=True)
            marker.write_text(f"{directory}/{name}\n")

    def commit(self):
        self.git("add", "-A")
        self.git("-c", "user.name=Fixture", "-c", "user.email=fixture@example.test", "commit", "-qm", "fixture")
        return self.git("rev-parse", "HEAD")

    def check(self):
        return subprocess.run(
            ["python3", str(SCRIPT), "--base", self.base, "--head", "HEAD", "--trunk", "trunk"],
            cwd=self.root, capture_output=True, text=True,
        )

    def test_unique_new_number_passes(self):
        self.add(SERVER, "002_New.sql")
        self.commit()
        self.assertEqual(self.check().returncode, 0)

    def test_missing_number_claim_fails(self):
        self.add(SERVER, "002_New.sql", claim=False)
        self.commit()
        result = self.check()
        self.assertEqual(result.returncode, 1)
        self.assertIn("002.txt", result.stdout)

    def test_number_claim_cannot_be_deleted(self):
        (self.root / "eng/schema/migration-numbers" / SERVER / "001.txt").unlink()
        self.commit()
        self.assertIn("claims are immutable", self.check().stdout)

    def test_concurrent_admitted_migrations_conflict_when_first_lands(self):
        self.add(SERVER, "002_First.sql")
        first = self.commit()
        self.assertEqual(self.check().returncode, 0)
        self.git("checkout", "-q", "--detach", self.base)
        self.add(SERVER, "002_Second.sql")
        self.commit()
        self.assertEqual(self.check().returncode, 0)
        result = subprocess.run(["git", "merge", "--no-commit", "--no-ff", first], cwd=self.root,
                                capture_output=True, text=True,
                                env={**__import__("os").environ, "GIT_COMMITTER_NAME": "Fixture", "GIT_COMMITTER_EMAIL": "fixture@example.test"})
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("002.txt", self.git("diff", "--name-only", "--diff-filter=U"))

    def test_reserved_number_without_sql_reports_the_claim_and_next_free_number(self):
        marker = self.root / "eng/schema/migration-numbers" / SERVER / "003.txt"
        marker.parent.mkdir(parents=True, exist_ok=True)
        marker.write_text(f"{SERVER}/003_Reserved.sql\n")
        self.base = self.commit()
        self.git("branch", "-f", "trunk", self.base)
        self.add(SERVER, "003_Competing.sql", claim=False)
        self.commit()
        result = self.check()
        self.assertEqual(result.returncode, 1, result.stderr)
        self.assertIn("003.txt", result.stdout)
        self.assertIn("already used by", result.stdout)
        self.assertIn("next free number: 004", result.stdout)
        self.assertNotIn("claims are immutable", result.stdout)

    def test_cutover_reservation_blocks_reuse_before_old_pr_lands(self):
        marker = self.root / "eng/schema/migration-numbers" / SERVER / "002.txt"
        marker.write_text(f"{SERVER}/002_AlreadyOpen.sql\n")
        self.base = self.commit()
        self.git("branch", "-f", "trunk", self.base)
        self.add(SERVER, "002_Competing.sql")
        self.commit()
        result = self.check()
        self.assertEqual(result.returncode, 1, result.stderr)
        self.assertIn("claims are immutable", result.stdout)

    def test_atomic_cutover_forces_old_ledger_writers_to_resolve(self):
        ledger = self.root / "eng/schema/schema-migration-hashes.v1.json"
        ledger.write_text('{"old": "hash"}\n')
        old_base = self.commit()
        ledger.unlink()
        cutover = self.commit()
        self.git("checkout", "-q", "--detach", old_base)
        ledger.write_text('{"old": "hash", "new": "hash"}\n')
        self.commit()
        result = subprocess.run(["git", "-c", "user.name=Fixture", "-c",
                                 "user.email=fixture@example.test", "merge", "--no-commit",
                                 "--no-ff", cutover], cwd=self.root, capture_output=True, text=True)
        self.assertNotEqual(result.returncode, 0)
        self.assertIn(str(ledger.relative_to(self.root)),
                      self.git("diff", "--name-only", "--diff-filter=U"))

    def test_paired_sql_and_pin_modification_fails(self):
        self.add(SERVER, "001_Initial.sql", "SELECT 2;")
        self.commit()
        result = self.check()
        self.assertEqual(result.returncode, 1)
        self.assertIn("hash pins are immutable", result.stdout)

    def test_existing_pin_deletion_fails(self):
        (self.root / "eng/schema/migration-hashes" / SERVER / "001_Initial.sql.sha256").unlink()
        self.commit()
        self.assertIn("hash pins are immutable", self.check().stdout)

    def test_existing_pin_replaced_with_symlink_fails(self):
        self.add(SERVER, "001_Initial.sql", "SELECT 2;")
        pin = self.root / "eng/schema/migration-hashes" / SERVER / "001_Initial.sql.sha256"
        target = pin.with_name("replacement.sha256")
        target.write_text(pin.read_text())
        pin.unlink()
        pin.symlink_to(target.name)
        self.commit()
        self.assertIn("hash pins are immutable", self.check().stdout)

    def test_existing_pin_rename_fails(self):
        pin = self.root / "eng/schema/migration-hashes" / SERVER / "001_Initial.sql.sha256"
        pin.rename(pin.with_name("001_Renamed.sql.sha256"))
        self.commit()
        self.assertIn("hash pins are immutable", self.check().stdout)

    def test_newer_trunk_pins_do_not_force_stale_branch_refresh(self):
        self.git("checkout", "-q", "trunk")
        self.add(SERVER, "002_Landed.sql")
        self.commit()
        self.git("checkout", "-q", "--detach", self.base)
        self.add(SERVER, "003_Pending.sql")
        self.commit()
        self.assertEqual(self.check().returncode, 0)

    def test_modified_pin_imported_from_newer_trunk_fails(self):
        self.git("checkout", "-q", "trunk")
        self.add(SERVER, "002_Landed.sql")
        self.commit()
        self.git("checkout", "-q", "--detach", self.base)
        self.add(SERVER, "002_Landed.sql", "SELECT 2;")
        self.commit()
        self.assertIn("hash pins are immutable", self.check().stdout)

    def test_same_number_in_other_migration_namespace_passes(self):
        self.add(POSTGRES, "001_Raster.sql")
        self.commit()
        self.assertEqual(self.check().returncode, 0)

    def test_duplicate_new_numbers_fail(self):
        for name in ("002_First.sql", "002_Second.sql", "003_Third.sql"):
            self.add(SERVER, name)
        self.commit()
        result = self.check()
        self.assertEqual(result.returncode, 1, result.stderr)
        self.assertIn("next free number: 004", result.stdout)

    def test_newer_trunk_collision_reports_next_after_all_pending_numbers(self):
        self.git("checkout", "-q", "trunk")
        self.add(SERVER, "002_Landed.sql")
        self.commit()
        self.git("checkout", "-q", "--detach", self.base)
        self.add(SERVER, "002_Pending.sql")
        self.add(SERVER, "004_AlsoPending.sql")
        self.commit()
        result = self.check()
        self.assertEqual(result.returncode, 1, result.stderr)
        self.assertIn("002_Landed.sql", result.stdout)
        self.assertIn("next free number: 005", result.stdout)

    def test_same_filename_on_diverged_trunk_is_also_a_collision(self):
        self.git("checkout", "-q", "trunk")
        self.add(SERVER, "002_New.sql", "SELECT 2;")
        self.commit()
        self.git("checkout", "-q", "--detach", self.base)
        self.add(SERVER, "002_New.sql", "SELECT 3;")
        self.commit()
        result = self.check()
        self.assertEqual(result.returncode, 1, result.stderr)
        self.assertIn("next free number: 003", result.stdout)


if __name__ == "__main__":
    unittest.main()
