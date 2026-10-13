#!/usr/bin/env python3
"""Signal delivery at real rename boundaries must preserve transactional outputs."""
import json
import hashlib
import io
import tarfile
import os
from pathlib import Path
import signal
import subprocess
import sys
import tempfile
import unittest

INSTALLER = Path(__file__).resolve().parents[1] / 'install-server-test-artifact.py'


class InstallCancellationTests(unittest.TestCase):
    def test_term_and_int_during_each_rename_roll_back_before_exit(self):
        for termination in (signal.SIGTERM, signal.SIGINT):
            for boundary in (1, 2, 3, 4):
                with self.subTest(signal=termination, boundary=boundary), tempfile.TemporaryDirectory() as directory:
                    root = Path(directory)
                    stage, destination, hook = (root / name for name in ('stage', 'destination', 'hook'))
                    hook.mkdir()
                    for relative in ('a/obj', 'a/bin/Release'):
                        for parent, value in ((stage, 'new'), (destination, 'old')):
                            target = parent / relative
                            target.mkdir(parents=True)
                            (target / 'proof').write_text(value)
                    manifest = root / 'manifest.json'
                    manifest.write_text(json.dumps({'static_web_asset_content_roots': {'payload': []}}))
                    # Real os.replace still runs. Deliver the signal immediately
                    # afterward, including on rollback renames: the installer must
                    # defer the first signal until its journal is complete and
                    # ignore subsequent signals while rolling back.
                    (hook / 'sitecustomize.py').write_text('''import os, signal
original = os.replace
count = 0
def replace(*args, **kwargs):
    global count
    original(*args, **kwargs)
    count += 1
    if count >= int(os.environ['INTERRUPT_BOUNDARY']):
        os.kill(os.getpid(), int(os.environ['INTERRUPT_SIGNAL']))
os.replace = replace
''')
                    result = subprocess.run([sys.executable, str(INSTALLER), str(stage), str(destination),
                                             str(manifest), 'a', 'Release'], capture_output=True, text=True,
                                            timeout=5, env=dict(os.environ, PYTHONPATH=str(hook),
                                                               INTERRUPT_BOUNDARY=str(boundary),
                                                               INTERRUPT_SIGNAL=str(int(termination))))
                    self.assertNotEqual(result.returncode, 0)
                    self.assertIn('InterruptedError', result.stderr)
                    for relative in ('a/obj/proof', 'a/bin/Release/proof'):
                        self.assertEqual('old', (destination / relative).read_text())
                        self.assertEqual('new', (stage / relative).read_text())

    def test_real_restore_shell_waits_for_installer_rollback_before_exit_cleanup(self):
        restore = INSTALLER.with_name('restore-server-test-binaries.sh')
        for termination in (signal.SIGTERM, signal.SIGINT):
            with self.subTest(signal=termination), tempfile.TemporaryDirectory() as directory:
                root = Path(directory)
                destination, hook = root / 'checkout', root / 'hook'
                hook.mkdir()
                relative = 'tests/dotnet/Fixture.Tests'
                for output in ('obj', 'bin/Release'):
                    target = destination / relative / output
                    target.mkdir(parents=True)
                    (target / 'original').write_text('old')
                payload = {relative + '/obj/project.assets.json': b'{}'}
                for suffix in ('dll', 'pdb', 'deps.json', 'runtimeconfig.json'):
                    payload[relative + '/bin/Release/net10.0/Fixture.' + suffix] = b'new'
                archive = root / 'server-test-binaries-fixture.tar.gz'
                with tarfile.open(archive, 'w:gz') as tar:
                    for name, content in payload.items():
                        entry = tarfile.TarInfo(name)
                        entry.size = len(content)
                        tar.addfile(entry, io.BytesIO(content))
                manifest = root / 'server-test-binaries-fixture.manifest.json'
                source = 'a' * 40
                manifest.write_text(json.dumps({
                    'contract': 'honua.server-test-binaries.v1', 'source_sha': source,
                    'project': relative + '/Fixture.Tests.csproj', 'dotnet_sdk': 'fixture-sdk',
                    'archive_file': archive.name, 'archive_sha256': hashlib.sha256(archive.read_bytes()).hexdigest(),
                    'archive_bytes': archive.stat().st_size, 'unpacked_bytes': sum(map(len, payload.values())),
                    'file_count': len(payload), 'created_at_epoch': 1000, 'expires_at_epoch': 2000,
                    'repo_root': '/fixture', 'static_web_asset_content_roots': {'payload': [], 'checkout': [], 'nuget': []}
                }))
                # Send to the actual shell+installer group immediately after its
                # first real rename. Slow the rollback enough that an eager EXIT
                # rm-rf would reliably destroy the backup before it can return.
                (hook / 'sitecustomize.py').write_text('''import os, signal, sys, time
if os.path.basename(sys.argv[0]) == 'install-server-test-artifact.py':
    original = os.replace
    count = 0
    def replace(*args, **kwargs):
        global count
        count += 1
        if count > 1:
            time.sleep(0.3)
        original(*args, **kwargs)
        if count == 1:
            os.killpg(os.getpgrp(), int(os.environ['INTERRUPT_SIGNAL']))
    os.replace = replace
''')
                child = subprocess.Popen(['bash', str(restore), '--manifest', str(manifest),
                                          '--destination', str(destination), '--project', relative + '/Fixture.Tests.csproj',
                                          '--source-sha', source], stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                                         text=True, start_new_session=True,
                                         env=dict(os.environ, PYTHONPATH=str(hook), INTERRUPT_SIGNAL=str(int(termination)),
                                                  HONUA_SERVER_TEST_ARTIFACT_DOTNET_SDK='fixture-sdk',
                                                  HONUA_SERVER_TEST_ARTIFACT_NOW_EPOCH='1001'))
                try:
                    stdout, stderr = child.communicate(timeout=30)
                except subprocess.TimeoutExpired:
                    os.killpg(child.pid, signal.SIGKILL)
                    child.communicate()
                    self.fail('Restore wrapper failed to reap cancelled installer')
                self.assertNotEqual(child.returncode, 0, stdout + stderr)
                self.assertIn('InterruptedError', stderr)
                for output in ('obj', 'bin/Release'):
                    self.assertEqual('old', (destination / relative / output / 'original').read_text())
                self.assertFalse(list(destination.glob('.honua-artifact-stage.*')),
                                 'EXIT should remove staging after rollback has finished')


if __name__ == '__main__':
    unittest.main()
