#!/usr/bin/env python3
"""Exact-input, trusted native-tools reuse; misses retain the bounded local build."""
from __future__ import annotations

import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import shlex
import signal
import subprocess
import time
import uuid

ROOT = Path(__file__).resolve().parents[2]
DOCKERFILE = 'docker/worker-gdal/Dockerfile'
IMAGE = 'ghcr.io/honua-io/honua-server-native-tools'
CACHE = 'ghcr.io/honua-io/honua-server-native-tools-cache:linux-amd64'
LEGACY_CACHE = 'ghcr.io/honua-io/honua-server/buildcache:honua-worker-gdal-amd64'
WORKFLOW = '.github/workflows/native-tools-publish.yml'
CONTRACT = 'honua.native-tools.inputs.v1'
LABEL = 'io.honua.native-tools.identity'
HEX = re.compile(r'^sha256:[0-9a-f]{64}$')


class UnsupportedInput(ValueError):
    """An input cannot be proven complete; never reuse an image for it."""


def digest(data):
    return hashlib.sha256(data).hexdigest()


def instructions(text):
    pending = ''
    for line in text.splitlines():
        if not line.strip() or line.lstrip().startswith('#'):
            continue
        line = line.strip()
        continued = line.endswith('\\')
        pending += line[:-1] + ' ' if continued else line
        if not continued:
            match = re.fullmatch(r'([A-Za-z]+)\s+(.*)', pending)
            if not match:
                raise UnsupportedInput('Unknown Dockerfile instruction syntax')
            yield match[1].upper(), match[2]
            pending = ''
    if pending:
        raise UnsupportedInput('Unterminated Dockerfile instruction')


def identity(root, platform='linux/amd64', overrides=()):
    dockerfile = root / DOCKERFILE
    args, stages, current = {}, {}, None
    for instruction, value in instructions(dockerfile.read_text()):
        if instruction == 'ARG' and current is None:
            name, separator, default = value.partition('=')
            if not re.fullmatch(r'[A-Za-z_][A-Za-z0-9_]*', name):
                raise UnsupportedInput('Unsupported global ARG')
            args[name] = default if separator else ''
        elif instruction == 'FROM':
            tokens = shlex.split(value)
            if len(tokens) != 3 or tokens[1].upper() != 'AS':
                raise UnsupportedInput('FROM must name an explicit stage')
            current = tokens[2].lower()
            if current in stages:
                raise UnsupportedInput('Duplicate stage')
            stages[current] = [('FROM', tokens[0])]
        elif current is not None:
            stages[current].append((instruction, value))
    explicit_args = {}
    for override in overrides:
        name, separator, value = override.partition('=')
        if not separator or name not in args or '\n' in value or '\r' in value:
            raise UnsupportedInput('Unknown or malformed build argument')
        args[name] = value
        explicit_args[name] = value
    inputs = {}

    def add_file(source):
        if source.is_symlink() or not source.resolve().is_relative_to(root.resolve()):
            raise UnsupportedInput('Symlink or external COPY source')
        if source.is_dir():
            if source.resolve() == root.resolve():
                raise UnsupportedInput('Whole-context COPY requires an explicit input policy')
            for item in sorted(source.rglob('*')):
                if item.is_symlink():
                    raise UnsupportedInput('Symlink COPY source')
                if item.is_file():
                    add_file(item)
        elif source.is_file():
            inputs[source.relative_to(root).as_posix()] = digest(source.read_bytes())
        else:
            raise UnsupportedInput('Missing COPY input')

    for name in (DOCKERFILE, '.dockerignore', DOCKERFILE + '.dockerignore',
                 'scripts/ci/native-tools-image.py', WORKFLOW):
        if (root / name).is_file():
            add_file(root / name)
    bases, visited = set(), set()

    def substitute(value):
        result = re.sub(r'\$\{([A-Za-z_][A-Za-z0-9_]*)\}', lambda m: args.get(m[1], m[0]), value)
        if '$' in result:
            raise UnsupportedInput('Unresolved image argument')
        return result

    def visit(stage):
        if stage in visited:
            return
        visited.add(stage)
        if stage not in stages:
            base = substitute(stage)
            if not re.fullmatch(r'[^\s]+@sha256:[0-9a-f]{64}', base):
                raise UnsupportedInput('Unpinned external base or COPY image')
            bases.add(base)
            return
        for instruction, value in stages[stage]:
            if instruction == 'FROM':
                visit(value)
            elif instruction in ('COPY', 'ADD'):
                from_stage = re.search(r'(?:^|\s)--from=([^\s]+)', value)
                if from_stage:
                    visit(from_stage[1].lower())
                    continue
                if instruction == 'ADD':
                    raise UnsupportedInput('ADD requires an explicit external-input policy')
                while value.startswith('--'):
                    flag, value = value.split(None, 1)
                    if not flag.startswith(('--chown=', '--chmod=')):
                        raise UnsupportedInput('Unknown COPY flag')
                # Docker's matching/escaping grammar differs from Python glob.
                # Reject escapes before shlex/JSON can erase their meaning;
                # wildcard sources below remain unsupported rather than partial.
                if '\\' in value:
                    raise UnsupportedInput('Escaped COPY paths require an explicit input policy')
                tokens = json.loads(value) if value.startswith('[') else shlex.split(value)
                if not isinstance(tokens, list) or len(tokens) < 2 or not all(isinstance(t, str) for t in tokens):
                    raise UnsupportedInput('Unknown COPY syntax')
                for source in tokens[:-1]:
                    if any(character in source for character in '*?[]'):
                        raise UnsupportedInput('Wildcard COPY sources require an explicit input policy')
                    if '$' in source or source.startswith('/') or '..' in Path(source).parts:
                        raise UnsupportedInput('Unresolved or external COPY input')
                    add_file(root / source)
            elif instruction == 'RUN' and '--mount' in value:
                raise UnsupportedInput('RUN mount requires an explicit input policy')
    visit('native-tools')
    data = {'contract': CONTRACT, 'target': 'native-tools', 'platform': platform,
            'files': inputs, 'build_args': args, 'explicit_build_args': explicit_args, 'pinned_bases': sorted(bases)}
    return {'identity': digest(json.dumps(data, sort_keys=True, separators=(',', ':')).encode()), 'inputs': data}


class UnsafeProcessGroup(RuntimeError):
    """The owned group did not stop; abort rather than overlap a fallback."""


def live_group(group):
    for process in Path('/proc').iterdir():
        if not process.name.isdigit():
            continue
        try:
            text = (process / 'stat').read_text()
        except (FileNotFoundError, ProcessLookupError):
            continue
        fields = text[text.rfind(')') + 2:].split()
        if int(fields[2]) == group and fields[0] not in ('Z', 'X'):
            return True
    return False


def command(argv, root, timeout):
    """Kill/reap the owned process group before lookup can fall back to build."""
    process = subprocess.Popen(argv, cwd=root, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                               text=True, start_new_session=True)
    try:
        stdout, stderr = process.communicate(timeout=max(0.01, timeout))
    except subprocess.TimeoutExpired:
        def stop(sig):
            try:
                os.killpg(process.pid, sig)
            except ProcessLookupError:
                # The owned group already exited; nothing remains to signal.
                pass
        stop(signal.SIGTERM)
        try:
            grace = time.monotonic() + 3
            while live_group(process.pid) and time.monotonic() < grace:
                time.sleep(0.04)
            stop(signal.SIGKILL)
            reap_deadline = time.monotonic() + 2
            while live_group(process.pid):
                if time.monotonic() >= reap_deadline:
                    raise UnsafeProcessGroup('Owned native-tools command group did not stop') from None
                time.sleep(0.02)
        except OSError:
            stop(signal.SIGKILL)
            raise UnsafeProcessGroup('Owned native-tools command group could not be inspected') from None
        try:
            process.communicate(timeout=2)
        except subprocess.TimeoutExpired:
            raise UnsafeProcessGroup('Owned native-tools command pipes did not close') from None
        raise TimeoutError('Native-tools command exceeded its budget') from None
    if process.returncode:
        # Tokens and registry responses must not be copied into receipts/logs.
        raise RuntimeError(f'Native-tools command failed: {argv[0]}')
    return stdout


def resolve(root, *, platform='linux/amd64', reuse=True, overrides=(), run=command, now=time.monotonic):
    record = {'contract': CONTRACT, 'platform': platform, 'mode': 'fallback', 'reason': 'disabled'}
    expected = None
    try:
        expected = identity(root, platform, overrides)
        record.update(expected)
    except (ValueError, OSError) as error:
        record['reason'] = 'unsupported-inputs'
    deadline = now() + 120
    try:
        if reuse and expected and platform == 'linux/amd64':
            def lookup(argv, cap=30):
                remaining = deadline - now()
                if remaining <= 0:
                    raise TimeoutError('Lookup budget exhausted')
                return run(argv, root, min(cap, remaining))
            record['reason'] = 'lookup-or-verification-failed'
            tag = f'{IMAGE}:inputs-{expected["identity"]}'
            descriptor = json.loads(lookup(['docker', 'buildx', 'imagetools', 'inspect', tag,
                                           '--format', '{{json .Manifest}}']))
            immutable = descriptor.get('digest', '')
            if not HEX.fullmatch(immutable):
                raise ValueError('Missing immutable image digest')
            reference = f'{IMAGE}@{immutable}'
            lookup(['docker', 'pull', '--platform', platform, reference], 90)
            details = json.loads(lookup(['docker', 'image', 'inspect', reference]))[0]
            labels = details.get('Config', {}).get('Labels') or {}
            if details.get('Os') != 'linux' or details.get('Architecture') != 'amd64' or labels.get(LABEL) != expected['identity']:
                raise ValueError('Image input/platform mismatch')
            source_sha = labels.get('org.opencontainers.image.revision', '')
            if not re.fullmatch(r'[0-9a-f]{40}', source_sha) or not HEX.fullmatch(details.get('Id', '')):
                raise ValueError('Missing source/image identity')
            lookup(['gh', 'attestation', 'verify', f'oci://{reference}', '--repo', 'honua-io/honua-server',
                    '--signer-workflow', f'honua-io/honua-server/{WORKFLOW}',
                    '--cert-identity', f'https://github.com/honua-io/honua-server/{WORKFLOW}@refs/heads/trunk',
                    '--cert-oidc-issuer', 'https://token.actions.githubusercontent.com',
                    '--source-ref', 'refs/heads/trunk', '--source-digest', source_sha,
                    '--deny-self-hosted-runners', '--bundle-from-oci'])
            record.update(mode='reused', reason='verified-trusted-inputs', image=reference,
                          image_id=details['Id'], publisher_source_sha=source_sha)
            return record
        elif reuse and expected:
            record['reason'] = 'unsupported-reuse-platform'
    except UnsafeProcessGroup:
        raise
    except (RuntimeError, TimeoutError, ValueError, OSError, IndexError, KeyError, TypeError, AttributeError):
        # A lookup/verification miss deliberately falls through to the bounded build.
        pass
    local_tag = 'honua-pdal-proof-' + uuid.uuid4().hex[:12]
    build = ['docker', 'buildx', 'build', '--load', '--platform', platform, '--target', 'native-tools',
             '-f', DOCKERFILE, '--cache-from', f'type=registry,ref={CACHE}',
             '--cache-from', f'type=registry,ref={LEGACY_CACHE}', '-t', local_tag]
    effective = expected['inputs']['explicit_build_args'] if expected else dict(item.split('=', 1) for item in overrides)
    for name, value in sorted(effective.items()):
        build.extend(['--build-arg', f'{name}={value}'])
    build.append('.')
    run(build, root, 1800)
    details = json.loads(run(['docker', 'image', 'inspect', local_tag], root, 30))[0]
    if (not HEX.fullmatch(details.get('Id', '')) or details.get('Os') != 'linux'
            or details.get('Architecture') != platform.split('/')[1]):
        raise RuntimeError('Fallback did not yield an immutable image for the requested platform')
    record.update(image=details['Id'], image_id=details['Id'])
    return record


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('operation', choices=['identity', 'resolve'])
    parser.add_argument('--root', type=Path, default=ROOT)
    parser.add_argument('--platform', default='linux/amd64')
    parser.add_argument('--build-arg', action='append', default=[])
    parser.add_argument('--output', type=Path)
    parser.add_argument('--github-output', type=Path)
    args = parser.parse_args()
    if not re.fullmatch(r'linux/(amd64|arm64)', args.platform):
        parser.error('Only explicit Linux amd64/arm64 platforms are supported')
    root = args.root.resolve()
    if args.operation == 'identity':
        record = identity(root, args.platform, args.build_arg)
        source = command(['git', 'rev-parse', 'HEAD'], root, 10).strip()
        if not re.fullmatch(r'[0-9a-f]{40}', source):
            raise ValueError('Missing publisher source SHA')
        record['source_sha'] = source
        record['image'] = f'{IMAGE}:inputs-{record["identity"]}'
        if args.github_output:
            with args.github_output.open('a') as output:
                output.write(f'identity={record["identity"]}\nimage={record["image"]}\nsource_sha={source}\n')
                output.write('build_args<<NATIVE_ARGS\n' + '\n'.join(f'{k}={v}' for k, v in sorted(record['inputs']['explicit_build_args'].items())) + '\nNATIVE_ARGS\n')
    else:
        record = resolve(root, platform=args.platform, overrides=args.build_arg,
                         reuse=os.environ.get('HONUA_NATIVE_TOOLS_REUSE', 'true') != 'false')
        if os.environ.get('GITHUB_ENV'):
            with open(os.environ['GITHUB_ENV'], 'a') as output:
                output.write(f'HONUA_PDAL_PROOF_IMAGE={record["image_id"]}\nHONUA_GDAL_PROOF_USER={os.getuid()}:{os.getgid()}\n')
    if args.output:
        args.output.parent.mkdir(parents=True, exist_ok=True)
        args.output.write_text(json.dumps(record, indent=2, sort_keys=True) + '\n')
    print(json.dumps(record, sort_keys=True))


if __name__ == '__main__':
    main()
