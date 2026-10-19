# Copyright (c) Honua. All rights reserved.
# Licensed under the Elastic License 2.0. See LICENSE in the project root.
"""Installed native scratch transport component probe; no release qualification."""
import argparse
import hashlib
import re
import json
import os
from pathlib import Path
import subprocess
import threading
import tracemalloc
import uuid

# This explicit component lane requires a Docker host with local ext4/loop support.
# It intentionally is not collected by the server-HTTP pytest lane. Use an
# installed worker image containing /app/Scripts/native_scratch_io.py; source is
# compared by digest, never mounted into the worker. The host helper is trusted
# and privileged; the file worker has neither network nor capabilities.
parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--worker-image', required=True)
parser.add_argument('--host-helper-image', required=True)
parser.add_argument('--evidence-root', type=Path, required=True)
parser.add_argument('--capacity-bytes', type=int, default=32 * 1024 * 1024)
parser.add_argument('--content-bytes', type=int, default=16 * 1024 * 1024 + 23)
parser.add_argument('--timeout-seconds', type=int, default=600)
OPTIONS = parser.parse_args()
for reference in (OPTIONS.worker_image, OPTIONS.host_helper_image):
    if not re.fullmatch(r'(?:[a-z0-9./:_-]+@)?sha256:[0-9a-f]{64}', reference):
        parser.error('images must be immutable SHA-256 references')
if not (32 * 1024 * 1024 <= OPTIONS.capacity_bytes <= 64 * 1024**3
        and 1024 * 1024 <= OPTIONS.content_bytes <= OPTIONS.capacity_bytes * 3 // 4
        and 10 <= OPTIONS.timeout_seconds <= 3600):
    parser.error('unsupported capacity, content length, or timeout')
ROOT = OPTIONS.evidence_root.resolve() / ('probe-' + uuid.uuid4().hex)
ROOT.mkdir(mode=0o700, parents=True)
IMAGE = OPTIONS.worker_image
HELPER_IMAGE = OPTIONS.host_helper_image
SOURCE = Path(__file__).resolve().parents[3] / 'src/Honua.Worker.Gdal/Scripts/native_scratch_io.py'
NAME = 'honua-scratch-stream-' + uuid.uuid4().hex
STATE = {'workerImage': IMAGE, 'helperImage': HELPER_IMAGE, 'capacityBytes': OPTIONS.capacity_bytes,
         'volume': NAME, 'releaseQualified': False, 'aggregateBudgetIntegrated': False,
         'sourceScriptSha256': hashlib.sha256(SOURCE.read_bytes()).hexdigest()}
CONTAINERS = []
BLOCK = bytes(range(256)) * 256
SIZE = OPTIONS.content_bytes


def save():
    (ROOT / 'receipt.json').write_text(json.dumps(STATE, indent=2) + '\n')


def run(args):
    return subprocess.run(args, text=True, capture_output=True, check=True, timeout=90).stdout.strip()


def helper(*args):
    container = NAME + '-host-' + str(len(CONTAINERS))
    CONTAINERS.append(container)
    return run(['docker', 'run', '--rm', '--name', container, '--network', 'none', '--read-only',
                '--privileged', '--mount', f'type=bind,src={ROOT},dst=/backing',
                '--entrypoint', args[0], HELPER_IMAGE, *args[1:]])


def loop_binding(device):
    rows = json.loads(helper('/usr/sbin/losetup', '--json', '--list', '--output',
                             'NAME,BACK-FILE,BACK-INO,BACK-MAJ:MIN', device))['loopdevices']
    assert len(rows) == 1 and rows[0]['name'] == device, rows
    return rows[0]


def chunks(length):
    while length:
        count = min(length, len(BLOCK))
        yield BLOCK[:count]
        length -= count


def digest(length):
    result = hashlib.sha256()
    for block in chunks(length):
        result.update(block)
    return result.hexdigest()


def command(suffix, entrypoint=None):
    container = NAME + '-' + suffix
    CONTAINERS.append(container)
    result = ['docker', 'run', '--rm', '--name', container, '--network', 'none',
              '--read-only', '--cap-drop', 'ALL', '--security-opt', 'no-new-privileges:true',
              '--ipc', 'none', '--user', '1001:1001', '--memory', '128m', '--pids-limit', '64',
              '--mount', f'type=volume,src={NAME},dst=/scratch']
    if entrypoint:
        result += ['--entrypoint', entrypoint]
    return result, container


def transfer(operation, path, length, content_hash, suffix):
    args, _ = command(suffix)
    error_file = ROOT / (suffix + '.stderr')
    with error_file.open('wb') as error:
        process = subprocess.Popen(args + ['-i', IMAGE, operation, path, str(length), content_hash],
                                   stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=error)
        timer = threading.Timer(OPTIONS.timeout_seconds, process.kill)
        timer.start()
        try:
            if operation == 'put':
                try:
                    for block in chunks(length):
                        process.stdin.write(block)
                    process.stdin.close()
                except BrokenPipeError:
                    try:
                        process.stdin.close()
                    except BrokenPipeError:
                        pass
            else:
                process.stdin.close()
            if operation == 'get':
                sha = hashlib.sha256()
                count = 0
                while True:
                    block = process.stdout.read(65536)
                    if not block:
                        break
                    count += len(block)
                    assert count <= length, 'export exceeded immutable expected length'
                    sha.update(block)
                output = {'bytes': count, 'sha256': sha.hexdigest()}
            else:
                output = process.stdout.read(4097)
                assert len(output) <= 4096, 'receipt exceeded bounded protocol'
            code = process.wait(timeout=5)
            return code, output
        finally:
            timer.cancel()
            if process.poll() is None:
                process.kill()
            process.wait(timeout=5)
            process.stdout.close()
            if not process.stdin.closed:
                process.stdin.close()


device = binding = None
loop_attempted = False
try:
    disk = ROOT / (NAME + '.ext4')
    with disk.open('xb') as output:
        os.posix_fallocate(output.fileno(), 0, STATE['capacityBytes'])
        os.fsync(output.fileno())
    run(['mkfs.ext4', '-q', '-F', '-m', '0', str(disk)])
    loop_attempted = True
    device = helper('/usr/sbin/losetup', '--find', '--show', '/backing/' + disk.name)
    assert device.startswith('/dev/loop') and device[9:].isdigit(), device
    binding = loop_binding(device)
    observed_inode = int(helper('python3', '-I', '-c',
                                'import os; print(os.stat("/backing/' + disk.name + '").st_ino)'))
    assert binding['back-ino'] == observed_inode, binding
    assert Path(binding['back-file']).name == disk.name, binding
    STATE.update(device=device, backingFile=str(disk), observedLoopBinding=binding)
    save()
    run(['docker', 'volume', 'create', '--driver', 'local', '--opt', 'type=ext4',
         '--opt', 'device=' + device, '--label', 'honua.component-probe=' + NAME, NAME])
    initializer = NAME + '-init'
    CONTAINERS.append(initializer)
    run(['docker', 'run', '--rm', '--name', initializer, '--network', 'none', '--read-only',
         '--mount', f'type=volume,src={NAME},dst=/scratch', '--entrypoint', 'python3',
         HELPER_IMAGE, '-I', '-c', "import os; os.chown('/scratch',1001,1001); os.chmod('/scratch',0o700)"])
    args, _ = command('identity', 'python3')
    program = """import errno,hashlib,json,os
try: open('/outside-scratch','wb')
except OSError as error: assert error.errno in (errno.EROFS,errno.EACCES),repr(error)
else: raise AssertionError('root writable')
info=os.statvfs('/scratch')
print(json.dumps({'helperSha256':hashlib.sha256(open('/app/Scripts/native_scratch_io.py','rb').read()).hexdigest(),'uid':os.getuid(),'readOnlyRoot':True,'filesystemBytes':info.f_blocks*info.f_frsize}))
"""
    STATE['installedWorker'] = json.loads(run(args + [IMAGE, '-I', '-c', program]))
    expected_helper_hash = STATE['sourceScriptSha256']
    assert STATE['installedWorker']['helperSha256'] == expected_helper_hash
    assert STATE['installedWorker']['uid'] == 1001
    sha = digest(SIZE)
    print(json.dumps({'phase':'streaming', 'contentBytes':SIZE}), flush=True)
    tracemalloc.start()
    code, output = transfer('put', 'original.bin', SIZE, sha, 'put')
    assert code == 0, (code, output)
    assert json.loads(output) == {'path': 'original.bin', 'bytes': SIZE, 'sha256': sha}
    code, output = transfer('inspect', 'original.bin', SIZE, sha, 'recover')
    assert code == 0 and json.loads(output)['sha256'] == sha
    code, output = transfer('get', 'original.bin', SIZE, sha, 'get')
    assert code == 0 and output == {'bytes': SIZE, 'sha256': sha}, (code, output)
    _, peak = tracemalloc.get_traced_memory()
    tracemalloc.stop()
    assert peak < 1024 * 1024, peak
    STATE['roundTrip'] = {'bytes': SIZE, 'sha256': sha, 'hostTracedPeakBytes': peak,
                          'uncertainPutRecovered': True, 'noHostFileStaging': True}
    # A killed writer must not leave a published or named temporary file. Keep
    # stdin open and feed more than pipe capacity so the helper has begun writing.
    argv, consumer = command('killed-put')
    with (ROOT / 'killed-put.stderr').open('wb') as error:
        process = subprocess.Popen(argv + ['-i', IMAGE, 'put', 'aborted.bin', str(SIZE), sha],
                                   stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=error)
        timer = threading.Timer(OPTIONS.timeout_seconds, process.kill)
        timer.start()
        try:
            for _ in range(16):
                process.stdin.write(BLOCK)
            process.stdin.flush()
            run(['docker', 'kill', '--signal', 'KILL', consumer])
            assert process.wait(timeout=30) != 0
            assert not process.stdout.read(4097)
        finally:
            timer.cancel()
            if process.poll() is None:
                process.kill()
            process.wait(timeout=5)
            process.stdout.close()
            try:
                process.stdin.close()
            except BrokenPipeError:
                pass
    args, _ = command('killed-put-inspect', 'python3')
    program = "import os; assert sorted(os.listdir('/scratch')) == ['lost+found','original.bin']"
    run(args + [IMAGE, '-I', '-c', program])
    STATE['killedWriterLeftNoPublishedOrTemporaryFile'] = True
    print(json.dumps({'phase':'disk-full', 'roundTrip':STATE['roundTrip']}), flush=True)
    big = STATE['capacityBytes'] * 2
    code, output = transfer('put', 'too-large.bin', big, digest(big), 'enospc')
    assert code != 0 and not output, (code, output)
    assert (ROOT / 'enospc.stderr').read_text().strip() == 'Native scratch IO failed: OSError (errno=28)'
    args, _ = command('reclaimed', 'python3')
    program = """import json,os
assert not os.path.exists('/scratch/too-large.bin')
assert sorted(os.listdir('/scratch')) == ['lost+found','original.bin']
info=os.statvfs('/scratch')
print(json.dumps({'anonymousPartialReclaimed':True,'freeBytes':info.f_bavail*info.f_frsize}))
"""
    STATE['diskFull'] = json.loads(run(args + [IMAGE, '-I', '-c', program]))
    small = 1024 * 1024
    code, output = transfer('put', 'after-enospc.bin', small, digest(small), 'after-enospc')
    assert code == 0 and json.loads(output)['bytes'] == small, (code, output)
    STATE['diskFull'].update(newPutSucceeded=True, errno=28)
    print(json.dumps({'phase':'verify-original'}), flush=True)
    code, output = transfer('put', 'original.bin', SIZE, sha, 'replay')
    assert code != 0 and not output, (code, output)
    assert (ROOT / 'replay.stderr').read_text().strip() == 'Native scratch IO failed: FileExistsError'
    code, output = transfer('get', 'original.bin', SIZE, sha, 'original-unchanged')
    assert code == 0 and output == {'bytes': SIZE, 'sha256': sha}, (code, output)
    STATE['originalUnchanged'] = True
    STATE['componentPassed'] = True
except BaseException as error:
    STATE['componentPassed'] = False
    STATE['failure'] = repr(error)
    if isinstance(error, subprocess.CalledProcessError):
        STATE['stderr'] = error.stderr[-4096:]
    raise
finally:
    try:
        # Snapshot: loop inspections create their own --rm helper containers.
        for container in list(CONTAINERS):
            found = run(['docker', 'container', 'ls', '--all', '--quiet', '--filter', 'name=^/' + container + '$'])
            if found:
                run(['docker', 'container', 'rm', '--force', container])
        # Creation may have succeeded before its response was lost. Always
        # reconcile the exact owned volume before detaching any backing device.
        found = run(['docker', 'volume', 'ls', '--format', '{{.Name}}',
                     '--filter', 'name=^' + NAME + '$'])
        if found:
            assert found == NAME, 'volume lookup returned a different identity'
            info = json.loads(run(['docker', 'volume', 'inspect', NAME]))
            assert len(info) == 1 and info[0]['Name'] == NAME
            assert info[0]['Labels'].get('honua.component-probe') == NAME
            assert info[0]['Driver'] == 'local' and info[0]['Options'] == {'device': device, 'type': 'ext4'}
            run(['docker', 'volume', 'rm', NAME])
            STATE['volumeRemoved'] = True
        if loop_attempted and (device is None or binding is None):
            raise RuntimeError('Loop attachment outcome unknown; backing retained for reconciliation')
        if device is not None and binding is not None:
            assert loop_binding(device) == binding, 'Loop device ownership changed'
            helper('/usr/sbin/losetup', '--detach', device)
            rows = json.loads(helper('/usr/sbin/losetup', '--json', '--list', '--output', 'NAME,BACK-FILE'))['loopdevices']
            assert all(row['back-file'] != binding['back-file'] for row in rows), 'backing remains attached'
            STATE['loopDetached'] = True
        if not loop_attempted or STATE.get('loopDetached'):
            disk.unlink(missing_ok=True)
            parent = os.open(ROOT, os.O_RDONLY | os.O_DIRECTORY)
            try:
                os.fsync(parent)
            finally:
                os.close(parent)
            STATE['backingDeleted'] = True
    except BaseException as error:
        STATE['cleanupFailure'] = repr(error)
        raise
    finally:
        save()
        print(json.dumps(STATE, indent=2), flush=True)
