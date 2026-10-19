#!/usr/bin/env python3
# Copyright (c) Honua. All rights reserved.
# Licensed under the Elastic License 2.0. See LICENSE in the project root.
"""Stream immutable files inside a host-leased Linux scratch filesystem.

The trusted host must own the allocation, hold exclusive consumer use for the
entire process, and check current execution authority. This helper neither
authorizes a job nor certifies that an arbitrary directory has a quota. Run it
without network/capabilities, with a read-only root and only the leased volume
writable at /scratch. No host file staging, JSON file bodies, or base64 payloads.

put: stdin bytes -> bounded JSON receipt after exclusive publication and fsync.
inspect: verify the SAME expected file after an uncertain put; never write it.
get: verify first, then stdout bytes; callers must require both expected length
and successful process exit before accepting an artifact. Errors use stderr.
"""
import argparse
import hashlib
import json
import os
import stat
import sys
import unicodedata

CHUNK_BYTES = 64 * 1024
MAX_CONTENT_BYTES = 1 << 50


def validate_content(expected_bytes, expected_hash):
    if (type(expected_bytes) is not int or not 0 <= expected_bytes <= MAX_CONTENT_BYTES
            or not isinstance(expected_hash, str) or len(expected_hash) != 64
            or any(c not in '0123456789abcdef' for c in expected_hash)):
        raise ValueError('a bounded immutable content identity is required')


def open_parent(root, relative):
    if (not isinstance(relative, str) or not relative or len(relative.encode('utf-8')) > 1280
            or '\\' in relative or any(unicodedata.category(c) == 'Cc' for c in relative)):
        raise ValueError('invalid scratch-relative path')
    parts = relative.split('/')
    if len(parts) > 8 or any(part in ('', '.', '..') or len(part.encode('utf-8')) > 255 for part in parts):
        raise ValueError('invalid scratch-relative path')
    descriptor = os.open(root, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW | os.O_CLOEXEC)
    device = os.fstat(descriptor).st_dev
    try:
        for part in parts[:-1]:
            child = os.open(part, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW | os.O_CLOEXEC,
                            dir_fd=descriptor)
            if os.fstat(child).st_dev != device:
                os.close(child)
                raise ValueError('scratch traversal crossed its original filesystem')
            os.close(descriptor)
            descriptor = child
        return descriptor, parts[-1], device
    except BaseException:
        os.close(descriptor)
        raise


def identity(info):
    return (info.st_dev, info.st_ino, info.st_mode, info.st_nlink,
            info.st_size, info.st_mtime_ns, info.st_ctime_ns)


def write_all(output, data):
    view = memoryview(data)
    while view:
        count = output.write(view)
        if type(count) is not int or not 0 < count <= len(view):
            raise OSError('output stream stopped accepting bytes')
        view = view[count:]


def receipt(relative, expected_bytes, expected_hash):
    return {'path': relative, 'bytes': expected_bytes, 'sha256': expected_hash}


def sync_publication(root, relative, source):
    """Confirm the same inode and sync its complete bounded directory ancestry."""
    directories = [os.open(root, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW | os.O_CLOEXEC)]
    try:
        initial = os.fstat(source.fileno())
        if initial.st_nlink != 1:
            raise ValueError('publication must have exactly one directory entry')
        for part in relative.split('/')[:-1]:
            directories.append(os.open(part, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW | os.O_CLOEXEC,
                                       dir_fd=directories[-1]))
        if any(os.fstat(directory).st_dev != initial.st_dev for directory in directories):
            raise ValueError('publication crossed its original filesystem')
        name = relative.split('/')[-1]
        if identity(os.stat(name, dir_fd=directories[-1], follow_symlinks=False)) != identity(initial):
            raise ValueError('publication no longer names the verified inode')
        os.fsync(source.fileno())
        for directory in reversed(directories):
            os.fsync(directory)
        if identity(os.fstat(source.fileno())) != identity(initial):
            raise ValueError('publication changed during synchronization')
    finally:
        for directory in reversed(directories):
            os.close(directory)


def put(root, relative, expected_bytes, expected_hash, source):
    validate_content(expected_bytes, expected_hash)
    parent, name, _ = open_parent(root, relative)
    try:
        try:
            os.stat(name, dir_fd=parent, follow_symlinks=False)
        except FileNotFoundError:
            pass
        else:
            raise FileExistsError('the original destination already exists; inspect it instead')
        # An anonymous inode cannot be pre-planted or substituted at a temporary
        # pathname. A crash before publication closes and reclaims it. Require
        # O_TMPFILE support; never fall back to an unaccounted staging directory.
        descriptor = os.open('.', os.O_TMPFILE | os.O_RDWR | os.O_CLOEXEC, 0o600, dir_fd=parent)
        with os.fdopen(descriptor, 'w+b', buffering=0) as target:
            digest = hashlib.sha256()
            count = 0
            while True:
                limit = min(CHUNK_BYTES, expected_bytes - count + 1)
                chunk = source.read(limit)
                if not chunk:
                    break
                if len(chunk) > limit or len(chunk) > expected_bytes - count:
                    raise ValueError('source exceeds its immutable length')
                write_all(target, chunk)
                digest.update(chunk)
                count += len(chunk)
            if count != expected_bytes or digest.hexdigest() != expected_hash:
                raise ValueError('source length or hash differs from its immutable identity')
            os.fsync(target.fileno())
            # The proc descriptor route links this exact open inode without
            # CAP_DAC_READ_SEARCH. See https://man7.org/linux/man-pages/man2/open.2.html
            # O_TMPFILE. The destination is exclusive: existing files, links,
            # and directories are never overwritten, even on a deliberate retry.
            os.link('/proc/self/fd/' + str(target.fileno()), name,
                    dst_dir_fd=parent, follow_symlinks=True)
            sync_publication(root, relative, target)
        return receipt(relative, count, expected_hash)
    finally:
        os.close(parent)


def open_verified(root, relative, expected_bytes, expected_hash):
    validate_content(expected_bytes, expected_hash)
    parent, name, device = open_parent(root, relative)
    try:
        # Nonblocking open lets us reject a FIFO without waiting for a writer.
        descriptor = os.open(name, os.O_RDONLY | os.O_NOFOLLOW | os.O_NONBLOCK | os.O_CLOEXEC,
                             dir_fd=parent)
    finally:
        os.close(parent)
    source = os.fdopen(descriptor, 'rb', buffering=0)
    try:
        initial = os.fstat(source.fileno())
        if (not stat.S_ISREG(initial.st_mode) or initial.st_nlink != 1
                or initial.st_dev != device or initial.st_size != expected_bytes):
            raise ValueError('file descriptor differs from its immutable identity')
        digest = hashlib.sha256()
        count = 0
        while True:
            chunk = source.read(min(CHUNK_BYTES, expected_bytes - count + 1))
            if not chunk:
                break
            count += len(chunk)
            if count > expected_bytes:
                raise ValueError('file grew beyond its immutable length')
            digest.update(chunk)
        if (count != expected_bytes or digest.hexdigest() != expected_hash
                or identity(os.fstat(source.fileno())) != identity(initial)):
            raise ValueError('file content or descriptor changed')
        source.seek(0)
        return source, initial
    except BaseException:
        source.close()
        raise


def inspect(root, relative, expected_bytes, expected_hash):
    source, _ = open_verified(root, relative, expected_bytes, expected_hash)
    with source:
        # A previous put may have lost or failed its directory sync. Reading a
        # visible file alone is insufficient evidence of durable publication.
        sync_publication(root, relative, source)
        return receipt(relative, expected_bytes, expected_hash)


def get(root, relative, expected_bytes, expected_hash, output):
    source, initial = open_verified(root, relative, expected_bytes, expected_hash)
    with source:
        digest = hashlib.sha256()
        count = 0
        pending = b''
        while True:
            chunk = source.read(min(CHUNK_BYTES, expected_bytes - count + 1))
            if not chunk:
                break
            count += len(chunk)
            if count > expected_bytes or identity(os.fstat(source.fileno())) != identity(initial):
                raise ValueError('file changed during export')
            if pending:
                write_all(output, pending)
            pending = chunk
            digest.update(chunk)
        # Never emit a full expected-size body after detecting a changed file.
        # A receiver must still check the process outcome, including empty files.
        if (count != expected_bytes or digest.hexdigest() != expected_hash
                or identity(os.fstat(source.fileno())) != identity(initial)):
            raise ValueError('file changed during export')
        write_all(output, pending)
        output.flush()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('operation', choices=('put', 'inspect', 'get'))
    parser.add_argument('path')
    parser.add_argument('bytes', type=int)
    parser.add_argument('sha256')
    args = parser.parse_args()
    if args.operation == 'get':
        get('/scratch', args.path, args.bytes, args.sha256, sys.stdout.buffer)
    else:
        result = (put('/scratch', args.path, args.bytes, args.sha256, sys.stdin.buffer)
                  if args.operation == 'put'
                  else inspect('/scratch', args.path, args.bytes, args.sha256))
        print(json.dumps(result, separators=(',', ':')))


if __name__ == '__main__':
    try:
        main()
    except (OSError, ValueError) as error:
        # Never append diagnostics to get's binary stdout or expose input data.
        code = f' (errno={error.errno})' if isinstance(error, OSError) and error.errno is not None else ''
        print('Native scratch IO failed: ' + type(error).__name__ + code, file=sys.stderr)
        sys.exit(1)
