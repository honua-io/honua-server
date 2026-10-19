# Copyright (c) Honua. All rights reserved.
# Licensed under the Elastic License 2.0. See LICENSE in the project root.
"""Actual Linux filesystem tests; no GDAL, database, or server fixture needed.

Run: python3 -m unittest discover -s tests/python/unit -p test_native_scratch_io.py
The installed-image/fixed-volume qualification is separate from these tests.
"""
import hashlib
import importlib.util
import io
import os
import stat
from pathlib import Path
import subprocess
import sys
import tempfile
import tracemalloc
import unittest
from unittest.mock import patch

SCRIPT = Path(__file__).resolve().parents[3] / 'src/Honua.Worker.Gdal/Scripts/native_scratch_io.py'
SPEC = importlib.util.spec_from_file_location('native_scratch_io', SCRIPT)
NATIVE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(NATIVE)


def sha(data):
    return hashlib.sha256(data).hexdigest()


class GeneratedInput:
    def __init__(self, length):
        self.remaining = length
        self.calls = 0

    def read(self, limit):
        if not 0 < limit <= NATIVE.CHUNK_BYTES:
            raise AssertionError('unbounded input read')
        self.calls += 1
        count = min(self.remaining, limit)
        self.remaining -= count
        return b'x' * count


class DigestOutput:
    def __init__(self):
        self.count = 0
        self.digest = hashlib.sha256()
        self.flushed = False

    def write(self, value):
        if len(value) > NATIVE.CHUNK_BYTES:
            raise AssertionError('unbounded output write')
        self.count += len(value)
        self.digest.update(value)
        return len(value)

    def flush(self):
        self.flushed = True


class NativeScratchIoTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory(prefix='honua-native-io-')
        self.root = Path(self.directory.name)

    def tearDown(self):
        self.directory.cleanup()

    def put(self, data=b'abc', path='source.bin'):
        return NATIVE.put(self.root, path, len(data), sha(data), io.BytesIO(data))

    def test_put_inspect_get_and_empty_artifact(self):
        for index, data in enumerate([b'abc', b'', bytes(range(256)) * 513]):
            with self.subTest(length=len(data)):
                path = str(index) + '.bin'
                expected = {'path': path, 'bytes': len(data), 'sha256': sha(data)}
                self.assertEqual(self.put(data, path), expected)
                self.assertEqual(NATIVE.inspect(self.root, path, len(data), sha(data)), expected)
                output = io.BytesIO()
                NATIVE.get(self.root, path, len(data), sha(data), output)
                self.assertEqual(output.getvalue(), data)
                self.assertEqual((self.root / path).stat().st_nlink, 1)

    def test_uncertain_put_is_recovered_without_overwrite_or_readmission(self):
        original = self.put()
        before = (self.root / 'source.bin').stat()
        source = GeneratedInput(3)
        with self.assertRaises(FileExistsError):
            NATIVE.put(self.root, 'source.bin', 3, sha(b'xyz'), source)
        self.assertEqual(source.calls, 0)
        self.assertEqual(NATIVE.inspect(self.root, 'source.bin', 3, sha(b'abc')), original)
        self.assertEqual(NATIVE.identity(before), NATIVE.identity((self.root / 'source.bin').stat()))

    def test_wrong_length_or_hash_never_publishes_a_partial_file(self):
        for data, length, digest in [(b'ab', 3, sha(b'abc')), (b'abcd', 3, sha(b'abc')), (b'abc', 3, sha(b'xyz'))]:
            with self.subTest(data=data):
                with self.assertRaises(ValueError):
                    NATIVE.put(self.root, 'source.bin', length, digest, io.BytesIO(data))
                self.assertEqual(list(self.root.iterdir()), [])

    def test_bounded_streams_do_not_buffer_the_complete_artifact(self):
        length = 9 * 1024 * 1024 + 19
        source = GeneratedInput(length)
        expected = hashlib.sha256()
        remaining = length
        while remaining:
            count = min(remaining, NATIVE.CHUNK_BYTES)
            expected.update(b'x' * count)
            remaining -= count
        output = DigestOutput()
        tracemalloc.start()
        try:
            NATIVE.put(self.root, 'large.bin', length, expected.hexdigest(), source)
            NATIVE.get(self.root, 'large.bin', length, expected.hexdigest(), output)
            peak = tracemalloc.get_traced_memory()[1]
        finally:
            tracemalloc.stop()
        self.assertEqual(source.remaining, 0)
        self.assertEqual(output.count, length)
        self.assertEqual(output.digest.hexdigest(), expected.hexdigest())
        self.assertTrue(output.flushed)
        self.assertLess(peak, 1024 * 1024)

    def test_missing_or_corrupt_content_emits_no_artifact_bytes(self):
        self.put()
        for name, length, digest in [('missing', 3, sha(b'abc')), ('source.bin', 4, sha(b'abc')), ('source.bin', 3, sha(b'xyz'))]:
            with self.subTest(name=name, length=length, digest=digest):
                output = io.BytesIO()
                with self.assertRaises((OSError, ValueError)):
                    NATIVE.get(self.root, name, length, digest, output)
                self.assertEqual(output.getvalue(), b'')

    def test_mutation_during_export_withholds_the_complete_expected_body(self):
        data = b'x' * (3 * NATIVE.CHUNK_BYTES)
        self.put(data)
        path = self.root / 'source.bin'

        class MutatingOutput(DigestOutput):
            def write(output, value):
                count = super().write(value)
                if output.count == len(value):
                    with path.open('ab') as target:
                        target.write(b'changed')
                return count

        output = MutatingOutput()
        with self.assertRaises(ValueError):
            NATIVE.get(self.root, 'source.bin', len(data), sha(data), output)
        self.assertLess(output.count, len(data))
        self.assertFalse(output.flushed)

    def test_partial_output_writes_are_completed(self):
        self.put()

        class ShortOutput(io.BytesIO):
            def write(self, value):
                return super().write(value[:1])

        output = ShortOutput()
        NATIVE.get(self.root, 'source.bin', 3, sha(b'abc'), output)
        self.assertEqual(output.getvalue(), b'abc')

    def test_stopped_output_fails_instead_of_claiming_success(self):
        self.put()

        class StoppedOutput(io.BytesIO):
            def write(self, value):
                return 0

        with self.assertRaises(OSError):
            NATIVE.get(self.root, 'source.bin', 3, sha(b'abc'), StoppedOutput())

    def test_file_and_parent_symlinks_are_rejected(self):
        with tempfile.TemporaryDirectory() as outside:
            external = Path(outside)
            (external / 'source.bin').write_bytes(b'abc')
            (self.root / 'directory').symlink_to(external, target_is_directory=True)
            (self.root / 'link.bin').symlink_to(external / 'source.bin')
            for name in ['directory/source.bin', 'link.bin']:
                with self.subTest(name=name), self.assertRaises((OSError, ValueError)):
                    NATIVE.get(self.root, name, 3, sha(b'abc'), io.BytesIO())
                with self.assertRaises((OSError, ValueError)):
                    self.put(b'xyz', name)
            self.assertEqual((external / 'source.bin').read_bytes(), b'abc')

    def test_hardlinked_artifact_is_rejected(self):
        self.put()
        os.link(self.root / 'source.bin', self.root / 'alias.bin')
        with self.assertRaises(ValueError):
            NATIVE.inspect(self.root, 'source.bin', 3, sha(b'abc'))

    def test_fifo_is_rejected_without_waiting_for_a_writer(self):
        os.mkfifo(self.root / 'pipe')
        code = ('import importlib.util,sys; '
                's=importlib.util.spec_from_file_location("n",sys.argv[1]); '
                'm=importlib.util.module_from_spec(s);s.loader.exec_module(m); '
                'm.inspect(sys.argv[2],"pipe",0,sys.argv[3])')
        result = subprocess.run([sys.executable, '-c', code, str(SCRIPT), str(self.root), sha(b'')],
                                capture_output=True, timeout=3, check=False)
        self.assertNotEqual(result.returncode, 0)
        self.assertIn(b'ValueError', result.stderr)

    def test_invalid_relative_paths_fail_before_input_read(self):
        paths = ['', '/outside', '../outside', 'a/../outside', 'a//b', 'a\\b', 'a\x00b',
                 'a\x85b', 'x' * 256, '/'.join(['dir'] * 8 + ['file']), 'é' * 128,
                 '/'.join(['x' * 200] * 7)]
        for path in paths:
            with self.subTest(path=path):
                source = GeneratedInput(3)
                with self.assertRaises(ValueError):
                    NATIVE.put(self.root, path, 3, sha(b'abc'), source)
                self.assertEqual(source.calls, 0)

    def test_nested_existing_directories_work_but_are_not_created_implicitly(self):
        (self.root / 'nested').mkdir()
        self.put(path='nested/source.bin')
        self.assertEqual(NATIVE.inspect(self.root, 'nested/source.bin', 3, sha(b'abc'))['bytes'], 3)
        with self.assertRaises(FileNotFoundError):
            self.put(path='missing/source.bin')

    def test_invalid_content_identity_is_rejected_before_opening(self):
        for length, digest in [(-1, sha(b'')), (True, sha(b'')), (1 << 51, sha(b'')), (0, 'A' * 64), (0, 'x')]:
            with self.subTest(length=length, digest=digest), self.assertRaises(ValueError):
                NATIVE.inspect(self.root, 'missing', length, digest)

    def test_lost_directory_sync_reply_can_be_reconciled_by_exact_identity(self):
        original = os.fsync

        def fsync(descriptor):
            original(descriptor)
            if stat.S_ISDIR(os.fstat(descriptor).st_mode):
                raise OSError('directory sync reply lost')

        with patch.object(NATIVE.os, 'fsync', fsync), self.assertRaises(OSError):
            self.put()
        with patch.object(NATIVE.os, 'fsync', fsync), self.assertRaises(OSError):
            NATIVE.inspect(self.root, 'source.bin', 3, sha(b'abc'))
        self.assertEqual(NATIVE.inspect(self.root, 'source.bin', 3, sha(b'abc'))['sha256'], sha(b'abc'))

    def test_nested_publication_syncs_each_ancestor_before_acknowledgment(self):
        (self.root / 'a' / 'b').mkdir(parents=True)
        synchronized = []
        original = os.fsync

        def fsync(descriptor):
            if stat.S_ISDIR(os.fstat(descriptor).st_mode):
                synchronized.append(os.fstat(descriptor).st_ino)
            original(descriptor)

        with patch.object(NATIVE.os, 'fsync', fsync):
            self.put(path='a/b/source.bin')
        self.assertEqual(synchronized, [(self.root / 'a' / 'b').stat().st_ino,
                                        (self.root / 'a').stat().st_ino, self.root.stat().st_ino])


if __name__ == '__main__':
    unittest.main()
