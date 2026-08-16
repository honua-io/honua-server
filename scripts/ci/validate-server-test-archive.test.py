#!/usr/bin/env python3

from __future__ import annotations

import importlib.util
import io
import tarfile
import tempfile
import unittest
from pathlib import Path


SCRIPT = Path(__file__).with_name("validate-server-test-archive.py")
SPEC = importlib.util.spec_from_file_location("validate_server_test_archive", SCRIPT)
assert SPEC and SPEC.loader
MODULE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(MODULE)


class ArchiveValidationTests(unittest.TestCase):
    def write_archive(self, entries: list[tuple[str, bytes, str]]) -> Path:
        # Retain the TemporaryDirectory object explicitly for Windows cleanup.
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        path = Path(temporary.name) / "payload.tar.gz"
        with tarfile.open(path, mode="w:gz") as archive:
            for name, data, kind in entries:
                info = tarfile.TarInfo(name)
                if kind == "file":
                    info.size = len(data)
                    archive.addfile(info, io.BytesIO(data))
                elif kind == "dir":
                    info.type = tarfile.DIRTYPE
                    archive.addfile(info)
                elif kind == "symlink":
                    info.type = tarfile.SYMTYPE
                    info.linkname = "target"
                    archive.addfile(info)
                elif kind == "hardlink":
                    info.type = tarfile.LNKTYPE
                    info.linkname = "target"
                    archive.addfile(info)
                else:
                    raise AssertionError(kind)
        return path

    def test_accepts_regular_bounded_payload(self) -> None:
        path = self.write_archive([
            ("./tests/", b"", "dir"),
            ("./tests/bin/test.dll", b"abc", "file"),
        ])
        self.assertEqual(MODULE.validate_archive(str(path), 100, 10), (1, 3))

    def test_accepts_gnu_tar_root_directory_marker(self) -> None:
        path = self.write_archive([
            (".", b"", "dir"),
            ("./tests/bin/test.dll", b"a", "file"),
        ])
        self.assertEqual(MODULE.validate_archive(str(path), 100, 10), (1, 1))

    def test_rejects_non_directory_root_marker(self) -> None:
        path = self.write_archive([(".", b"x", "file")])
        with self.assertRaisesRegex(ValueError, "root marker"):
            MODULE.validate_archive(str(path), 100, 10)

    def test_rejects_parent_traversal(self) -> None:
        path = self.write_archive([("../escape", b"x", "file")])
        with self.assertRaisesRegex(ValueError, "unsafe path"):
            MODULE.validate_archive(str(path), 100, 10)

    def test_rejects_absolute_and_windows_paths(self) -> None:
        for name in ("/tmp/escape", "C:/escape", "tests\\escape"):
            with self.subTest(name=name):
                path = self.write_archive([(name, b"x", "file")])
                with self.assertRaisesRegex(ValueError, "unsafe path"):
                    MODULE.validate_archive(str(path), 100, 10)

    def test_rejects_noncanonical_internal_parent_path(self) -> None:
        path = self.write_archive([("tests/bin/../escape.dll", b"x", "file")])
        with self.assertRaisesRegex(ValueError, "unsafe path"):
            MODULE.validate_archive(str(path), 100, 10)

    def test_rejects_symlink(self) -> None:
        path = self.write_archive([("tests/link", b"", "symlink")])
        with self.assertRaisesRegex(ValueError, "non-regular"):
            MODULE.validate_archive(str(path), 100, 10)

    def test_rejects_hardlink(self) -> None:
        path = self.write_archive([("tests/hard", b"", "hardlink")])
        with self.assertRaisesRegex(ValueError, "non-regular"):
            MODULE.validate_archive(str(path), 100, 10)

    def test_rejects_duplicate_normalized_path(self) -> None:
        path = self.write_archive([
            ("tests/bin/test.dll", b"a", "file"),
            ("tests/bin/test.dll", b"b", "file"),
        ])
        with self.assertRaisesRegex(ValueError, "duplicate path"):
            MODULE.validate_archive(str(path), 100, 10)

    def test_rejects_entry_count_overflow(self) -> None:
        path = self.write_archive([
            ("tests/bin/one.dll", b"a", "file"),
            ("tests/bin/two.dll", b"b", "file"),
        ])
        with self.assertRaisesRegex(ValueError, "entry-count"):
            MODULE.validate_archive(str(path), 100, 1)

    def test_rejects_unpacked_size_overflow(self) -> None:
        path = self.write_archive([("tests/bin/test.dll", b"abcd", "file")])
        with self.assertRaisesRegex(ValueError, "unpacked-byte"):
            MODULE.validate_archive(str(path), 3, 10)

    def test_rejects_control_character(self) -> None:
        path = self.write_archive([("tests/bin/bad\nname.dll", b"x", "file")])
        with self.assertRaisesRegex(ValueError, "unsafe path"):
            MODULE.validate_archive(str(path), 100, 10)


if __name__ == "__main__":
    unittest.main()
