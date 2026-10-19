# Copyright (c) Honua. All rights reserved.
# Licensed under the Elastic License 2.0. See LICENSE in the project root.
"""Fault injection for the privileged test harness; no real Docker commands."""
import contextlib
import io
import json
import os
from pathlib import Path
import runpy
import subprocess
import sys
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import patch

SCRIPT = Path(__file__).resolve().parents[1] / 'native_scratch/probe_container.py'
IMAGE = 'sha256:' + 'a' * 64


class HostFailure:
    def __init__(self, root, scenario):
        self.root = root
        self.scenario = scenario
        self.calls = []
        self.volume = None
        self.created = False
        self.loop_reads = 0
        self.detached = False

    def run(self, arguments, **_):
        self.calls.append(list(arguments))
        output = ''
        if arguments[0] == 'mkfs.ext4':
            pass
        elif arguments[:3] == ['docker', 'container', 'ls']:
            pass
        elif arguments[:3] == ['docker', 'volume', 'create']:
            self.volume = arguments[-1]
            self.created = True
            raise subprocess.TimeoutExpired(arguments, 90)
        elif arguments[:3] == ['docker', 'volume', 'ls']:
            output = self.volume if self.created else ''
        elif arguments[:3] == ['docker', 'volume', 'inspect']:
            output = json.dumps([{'Name': self.volume, 'Driver': 'local',
                                 'Labels': {'honua.component-probe': 'different-owner' if self.scenario == 'wrong-owner' else self.volume},
                                 'Options': {'device': '/dev/loop987', 'type': 'ext4'}}])
        elif arguments[:3] == ['docker', 'volume', 'rm']:
            if self.scenario == 'remove-failed':
                raise subprocess.CalledProcessError(1, arguments, stderr='volume remains in use')
            self.created = False
            output = self.volume
        elif arguments[:2] == ['docker', 'run']:
            executable = arguments[arguments.index('--entrypoint') + 1]
            if executable == '/usr/sbin/losetup':
                if '--find' in arguments:
                    if self.scenario == 'lost-attach':
                        raise subprocess.TimeoutExpired(arguments, 90)
                    output = '/dev/loop987'
                elif '--detach' in arguments:
                    self.detached = True
                elif '--list' in arguments:
                    if arguments[-1] == '/dev/loop987':
                        self.loop_reads += 1
                        backing = next(self.root.rglob('*.ext4'))
                        inode = backing.stat().st_ino
                        if self.scenario == 'changed-loop' and self.loop_reads > 1:
                            inode += 1
                        output = json.dumps({'loopdevices': [{'name': '/dev/loop987',
                                            'back-file': '/' + backing.name, 'back-ino': inode,
                                            'back-maj:min': '8:48'}]})
                    else:
                        output = '{"loopdevices":[]}'
                else:
                    raise AssertionError(arguments)
            elif executable == 'python3':
                output = str(next(self.root.rglob('*.ext4')).stat().st_ino)
            else:
                raise AssertionError(arguments)
        else:
            raise AssertionError(arguments)
        return SimpleNamespace(stdout=output)


class NativeScratchProbeCleanupTests(unittest.TestCase):
    def exercise(self, scenario):
        temporary = tempfile.TemporaryDirectory(prefix='honua-scratch-cleanup-')
        self.addCleanup(temporary.cleanup)
        root = Path(temporary.name)
        host = HostFailure(root, scenario)
        arguments = [str(SCRIPT), '--worker-image', IMAGE, '--host-helper-image', IMAGE,
                     '--evidence-root', str(root)]
        # Only simulate control-plane outcomes. Sparse backing keeps these fault
        # tests small; the separate container probe verifies real preallocation.
        with patch.object(sys, 'argv', arguments), patch('subprocess.run', host.run), \
                patch('subprocess.Popen', side_effect=AssertionError('no worker may launch')), \
                patch('os.posix_fallocate', side_effect=lambda descriptor, offset, length: os.ftruncate(descriptor, length)), \
                contextlib.redirect_stdout(io.StringIO()):
            with self.assertRaises(Exception):
                runpy.run_path(str(SCRIPT), run_name='__main__')
        receipt = json.loads(next(root.rglob('receipt.json')).read_text())
        self.assertFalse(receipt['componentPassed'])
        return host, receipt, list(root.rglob('*.ext4'))

    def test_lost_volume_creation_reply_is_reconciled_before_detach(self):
        host, receipt, backing = self.exercise('lost-create')
        self.assertTrue(receipt['volumeRemoved'])
        self.assertTrue(receipt['loopDetached'])
        self.assertTrue(receipt['backingDeleted'])
        self.assertFalse(backing)
        remove = next(index for index, call in enumerate(host.calls) if call[:3] == ['docker', 'volume', 'rm'])
        detach = next(index for index, call in enumerate(host.calls) if '--detach' in call)
        self.assertLess(remove, detach)

    def test_unknown_loop_attachment_retains_backing(self):
        host, receipt, backing = self.exercise('lost-attach')
        self.assertIn('outcome unknown', receipt['cleanupFailure'])
        self.assertEqual(len(backing), 1)
        self.assertFalse(host.detached)
        self.assertFalse(any(call[:3] == ['docker', 'volume', 'rm'] for call in host.calls))

    def test_another_owner_volume_is_never_removed_or_detached(self):
        host, receipt, backing = self.exercise('wrong-owner')
        self.assertIn('cleanupFailure', receipt)
        self.assertEqual(len(backing), 1)
        self.assertFalse(host.detached)
        self.assertFalse(any(call[:3] == ['docker', 'volume', 'rm'] for call in host.calls))

    def test_changed_loop_binding_is_never_detached(self):
        host, receipt, backing = self.exercise('changed-loop')
        self.assertIn('ownership changed', receipt['cleanupFailure'])
        self.assertTrue(receipt['volumeRemoved'])
        self.assertEqual(len(backing), 1)
        self.assertFalse(host.detached)

    def test_volume_removal_failure_retains_backing_and_loop(self):
        host, receipt, backing = self.exercise('remove-failed')
        self.assertIn('cleanupFailure', receipt)
        self.assertFalse(receipt.get('volumeRemoved', False))
        self.assertEqual(len(backing), 1)
        self.assertFalse(host.detached)


if __name__ == '__main__':
    unittest.main()
