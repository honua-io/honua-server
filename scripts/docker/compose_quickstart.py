#!/usr/bin/env python3
"""Initialize and start the packaged, approval-governed local Compose installation."""
import argparse
import base64
import binascii
import json
import os
from pathlib import Path
import re
import secrets
import subprocess
import sys

SECRET_KEYS = ('POSTGRES_PASSWORD', 'HONUA_ADMIN_PASSWORD', 'HONUA_MANAGER_ADMIN_PASSWORD',
               'HONUA_MANAGER_POSTGRES_PASSWORD', 'HONUA_MASTER_KEY', 'HONUA_MANAGER_MASTER_KEY',
               'HONUA_KEYRING_PASSWORD')
AUDIT_KEYS = ('HONUA_AUDIT_CHAIN_KEY', 'HONUA_MANAGER_AUDIT_CHAIN_KEY')
IMMUTABLE = re.compile(r'(?:[a-z0-9./:_-]+@)?sha256:[a-f0-9]{64}')


def initialize(directory):
    directory = Path(directory).resolve()
    lock = json.loads((directory / 'images.json').read_text())
    if not all(IMMUTABLE.fullmatch(lock[name]) for name in ('server', 'executor', 'worker')):
        raise ValueError('The image lock must contain immutable sha256 references')
    env_file = directory / '.env'
    existing = env_file.read_text() if env_file.exists() else ''
    values = dict(line.split('=', 1) for line in existing.splitlines() if line and not line.startswith('#'))
    defaults = {
        'COMPOSE_FILE': 'compose.yaml:compose.executor.yaml:compose.executor.image.yaml',
        'COMPOSE_PROJECT_NAME': 'honua-' + secrets.token_hex(6),
        'HONUA_IMAGE': lock['server'], 'HONUA_EXECUTOR_IMAGE': lock['executor'],
        'HONUA_RELEASE_IMAGE': lock['server'], 'HONUA_RELEASE_WORKER_IMAGE': lock['worker'],
        'HONUA_RELEASE_VERSION': lock['version'], 'HONUA_PRIOR_IMAGE': lock['server'],
        'HONUA_PRIOR_MAX_SCHEMA': str(lock['maxSchema']),
        'HONUA_HTTP_PORT': '18080', 'HONUA_GRPC_PORT': '18081', 'HONUA_MANAGER_PORT': '18082',
        'DOCKER_GID': str(Path('/var/run/docker.sock').stat().st_gid),
        'HONUA_PROJECT_GID': str(directory.stat().st_gid),
        'HONUA_COMPOSE_PROJECT_DIRECTORY': str(directory),
        'HONUA_COMPOSE_CUSTOMER_PROBE': 'http://honua:8080/healthz/ready',
    }
    defaults.update({key: ('Aa1!' if key.endswith('ADMIN_PASSWORD') else '') + secrets.token_hex(32)
                     for key in SECRET_KEYS})
    defaults.update({key: base64.b64encode(secrets.token_bytes(32)).decode('ascii') for key in AUDIT_KEYS})
    for key in AUDIT_KEYS:
        if key not in values:
            continue
        try:
            decoded = base64.b64decode(values[key], validate=True)
        except (binascii.Error, ValueError) as error:
            raise ValueError(f'{key} must be base64 with at least 32 decoded bytes') from error
        if len(decoded) < 32:
            raise ValueError(f'{key} must be base64 with at least 32 decoded bytes')
    for key in SECRET_KEYS:
        if key in values and not re.fullmatch(r'[A-Za-z0-9_!.-]{32,}', values[key]):
            raise ValueError(f'{key} must be a locally generated secret of at least 32 characters')
    for key in ('HONUA_IMAGE', 'HONUA_EXECUTOR_IMAGE', 'HONUA_RELEASE_IMAGE',
                'HONUA_RELEASE_WORKER_IMAGE', 'HONUA_PRIOR_IMAGE'):
        if key in values and not IMMUTABLE.fullmatch(values[key]):
            raise ValueError(f'{key} must be an immutable sha256 reference')
    if any(char in str(directory) for char in '\n\r$#\'"'):
        raise ValueError('Install directory contains unsupported dotenv characters')
    if values.get('HONUA_COMPOSE_PROJECT_DIRECTORY', str(directory)) != str(directory):
        raise ValueError('Install directory moved; update the saved project path explicitly')
    additions = {key: value for key, value in defaults.items() if key not in values}
    if additions:
        fd = os.open(env_file, os.O_WRONLY | os.O_CREAT | (os.O_APPEND if env_file.exists() else os.O_EXCL), 0o600)
        with os.fdopen(fd, 'a') as stream:
            if existing and not existing.endswith('\n'):
                stream.write('\n')
            stream.writelines(f'{key}={value}\n' for key, value in additions.items())
    values.update(additions)
    # Only this install directory and its private group are writable by the executor.
    directory.chmod(0o770)
    env_file.chmod(0o640)
    for name in ('backups', 'secrets'):
        (directory / name).mkdir(exist_ok=True)
        (directory / name).chmod(0o770)
    for name in ('compose.yaml', 'compose.executor.yaml'):
        (directory / name).chmod(0o640)
    image_override = directory / 'compose.executor.image.yaml'
    if not image_override.exists():
        image_override.write_text('services:\n  honua:\n    image: ' + values['HONUA_IMAGE'] + '\n')
        image_override.chmod(0o640)
    pfx = directory / 'secrets/keyring.pfx'
    if not pfx.exists():
        child_env = dict(os.environ, HONUA_KEYRING_PASSWORD=values['HONUA_KEYRING_PASSWORD'])
        def openssl(*args):
            result = subprocess.run(['openssl', *args], env=child_env, capture_output=True)
            if result.returncode:
                raise RuntimeError('OpenSSL could not create the local key-ring certificate')
        pem, crt = directory / 'secrets/keyring.pem', directory / 'secrets/keyring.crt'
        pending_pfx = directory / 'secrets/keyring.pfx.pending'
        try:
            openssl('req', '-x509', '-newkey', 'rsa:2048', '-nodes', '-subj', '/CN=honua-quickstart',
                    '-keyout', str(pem), '-out', str(crt), '-days', '3650')
            openssl('pkcs12', '-export', '-inkey', str(pem), '-in', str(crt),
                    '-out', str(pending_pfx), '-passout', 'env:HONUA_KEYRING_PASSWORD')
            pending_pfx.chmod(0o600)
            os.replace(pending_pfx, pfx)
        finally:
            pem.unlink(missing_ok=True)
            crt.unlink(missing_ok=True)
            pending_pfx.unlink(missing_ok=True)
    return values


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--directory', type=Path, default=Path(__file__).resolve().parent)
    parser.add_argument('--init-only', action='store_true')
    args = parser.parse_args()
    try:
        values = initialize(args.directory)
        if args.init_only:
            return 0
        return subprocess.call(['docker', 'compose', 'up', '-d', '--wait', '--wait-timeout', '180'],
                               cwd=args.directory, env=dict(os.environ, **values))
    except (OSError, ValueError, RuntimeError, KeyError):
        print('Quickstart initialization failed; check the image lock, Docker socket, OpenSSL and private install directory.',
              file=sys.stderr)
        return 1


if __name__ == '__main__':
    sys.exit(main())
