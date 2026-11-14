#!/usr/bin/env python3
"""Cancellation during process creation must still reap the test process."""
import os
from pathlib import Path
import signal
import subprocess
import sys
import tempfile
import unittest


PROBE = '''
import importlib.util, os, pathlib, signal, subprocess, sys, time
sys.path.insert(0, sys.argv[1])
spec = importlib.util.spec_from_file_location('runner', pathlib.Path(sys.argv[1]) / 'retry-failed-shard-tests.py')
runner = importlib.util.module_from_spec(spec)
spec.loader.exec_module(runner)
spawn = subprocess.Popen
pid_file = pathlib.Path(sys.argv[2])
signum = int(sys.argv[3])
def interrupted_spawn(command):
    child = spawn(command, start_new_session=True, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    deadline = time.monotonic() + 5
    while not pid_file.exists():
        if time.monotonic() >= deadline:
            child.kill()
            child.wait()
            raise TimeoutError('child did not become ready')
        time.sleep(0.01)
    subprocess.Popen = spawn
    os.kill(os.getpid(), signum)
    return child
subprocess.Popen = interrupted_spawn
result = runner.call_test([sys.executable, '-c', sys.argv[4], str(pid_file)])
assert result == -signum, result
'''
CHILD = '''
import os, pathlib, signal, sys, time
signal.signal(signal.SIGTERM, signal.SIG_IGN)
signal.signal(signal.SIGINT, signal.SIG_IGN)
pathlib.Path(sys.argv[1]).write_text(str(os.getpid()))
time.sleep(30)
'''


class CancellationTests(unittest.TestCase):
    @unittest.skipUnless(os.name == 'posix', 'POSIX signal cleanup')
    def test_signal_during_spawn_reaps_child_and_preserves_cancellation(self):
        for signum in (signal.SIGTERM, signal.SIGINT):
            with self.subTest(signum=signum), tempfile.TemporaryDirectory() as directory:
                pid_file = Path(directory) / 'child.pid'
                try:
                    result = subprocess.run(
                        [sys.executable, '-c', PROBE, str(Path(__file__).parent), str(pid_file), str(signum), CHILD],
                        capture_output=True, text=True, timeout=10)
                    self.assertEqual(0, result.returncode, result.stderr)
                    process = Path('/proc') / pid_file.read_text() / 'stat'
                    if process.exists():
                        self.assertEqual('Z', process.read_text().split(') ', 1)[1].split()[0])
                finally:
                    if pid_file.exists():
                        try:
                            os.kill(int(pid_file.read_text()), signal.SIGKILL)
                        except ProcessLookupError:
                            pass


if __name__ == '__main__':
    unittest.main()
