#!/usr/bin/env python3
"""Exercise Chisel retries with partial cuts and a fake archive client."""
import os
from pathlib import Path
import subprocess
import tempfile
import unittest

SCRIPT = Path(__file__).resolve().parents[2] / "docker/runtime/cut-rootfs-retry.sh"
CHISEL = r'''#!/bin/sh
set -eu
manifest=$2
shift 3
while [ "$1" != --root ]; do shift; done
root=$2
shift 2
printf '%s\n' "$root" >> "$MOCK_STATE/roots"
printf '%s\n' "$@" > "$MOCK_STATE/slices"
attempt=$(wc -l < "$MOCK_STATE/roots")
test ! -e "$root/partial"
if [ "$MOCK_MODE" = exhausted ] || { [ "$MOCK_MODE" = recover ] && [ "$attempt" -eq 1 ]; }; then
    touch "$root/partial"
    echo 'cannot fetch from archive: context deadline exceeded' >&2
    exit 42
fi
mkdir -p "$root/usr/bin"
printf '#!/bin/sh\n' > "$root/usr/bin/launcher"
chmod 755 "$root/usr/bin/launcher"
ln -s launcher "$root/usr/bin/launcher-link"
if [ "$MOCK_MODE" != missing ]; then
    printf 'Package: libc6\n' > "$manifest"
fi
'''


class RootfsRetryTests(unittest.TestCase):
    def run_cut(self, mode):
        with tempfile.TemporaryDirectory() as directory:
            state = Path(directory)
            for name, body in {
                "chisel-wrapper": CHISEL,
                "sleep": '#!/bin/sh\nprintf "%s\\n" "$1" >> "$MOCK_STATE/delays"\n',
            }.items():
                command = state / name
                command.write_text(body)
                command.chmod(0o755)
            output = state / "rootfs"
            result = subprocess.run(
                ["sh", str(SCRIPT), str(output), "libicu78_libs", "tzdata_zoneinfo"],
                env=dict(os.environ, PATH=f"{state}:{os.environ['PATH']}",
                         MOCK_STATE=str(state), MOCK_MODE=mode),
                text=True, capture_output=True, timeout=5,
            )
            roots = (state / "roots").read_text().splitlines()
            self.assertEqual(len(roots), len(set(roots)))
            self.assertTrue(all(not Path(root).exists() for root in roots))
            slices = (state / "slices").read_text().splitlines()
            self.assertIn("libc6_libs", slices)
            self.assertEqual(["libicu78_libs", "tzdata_zoneinfo"], slices[-2:])
            delays_file = state / "delays"
            delays = delays_file.read_text().splitlines() if delays_file.exists() else []
            if result.returncode == 0:
                self.assertTrue((output / "var/lib/dpkg/status").stat().st_size)
                self.assertEqual(0o755, (output / "usr/bin/launcher").stat().st_mode & 0o777)
                self.assertTrue((output / "usr/bin/launcher-link").is_symlink())
                self.assertFalse((output / "partial").exists())
            else:
                self.assertFalse(output.exists(), "Incomplete roots must not be published")
            return result, len(roots), delays

    def test_first_attempt_success_does_not_sleep(self):
        result, attempts, delays = self.run_cut("success")
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual(1, attempts)
        self.assertEqual([], delays)

    def test_archive_timeout_recovers_with_fresh_root(self):
        result, attempts, delays = self.run_cut("recover")
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual(2, attempts)
        self.assertEqual(["5"], delays)

    def test_exhausted_retries_propagate_last_failure(self):
        result, attempts, delays = self.run_cut("exhausted")
        self.assertEqual(42, result.returncode)
        self.assertEqual(3, attempts)
        self.assertEqual(["5", "10"], delays)

    def test_missing_package_manifest_fails_closed(self):
        result, attempts, delays = self.run_cut("missing")
        self.assertNotEqual(0, result.returncode)
        self.assertEqual(1, attempts)
        self.assertEqual([], delays)


if __name__ == "__main__":
    unittest.main()
