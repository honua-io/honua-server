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
# Each secret lives in its own file, secrets/<lower-case key>. Compose mounts the files at
# /run/secrets; none is a Compose interpolation variable, so `docker compose config` cannot print one.
ALL_SECRET_KEYS = SECRET_KEYS + AUDIT_KEYS
IMMUTABLE = re.compile(r'(?:[a-z0-9./:_-]+@)?sha256:[a-f0-9]{64}')
COMPOSE_FILES = ('compose.yaml', 'compose.executor.yaml')


def secret_path(directory, key):
    """The private file that holds one generated secret."""
    return Path(directory) / 'secrets' / key.lower()


def read_secret(directory, key):
    """Returns one saved secret, for operator tooling that runs on the install host."""
    return secret_path(directory, key).read_text().strip()


def validate_secret(key, value):
    if key in AUDIT_KEYS:
        try:
            decoded = base64.b64decode(value, validate=True)
        except (binascii.Error, ValueError) as error:
            raise ValueError(f'{key} must be base64 with at least 32 decoded bytes') from error
        if len(decoded) < 32:
            raise ValueError(f'{key} must be base64 with at least 32 decoded bytes')
    elif not re.fullmatch(r'[A-Za-z0-9_!.-]{32,}', value):
        raise ValueError(f'{key} must be a locally generated secret of at least 32 characters')


def generate_secret(key):
    if key in AUDIT_KEYS:
        return base64.b64encode(secrets.token_bytes(32)).decode('ascii')
    return ('Aa1!' if key.endswith('ADMIN_PASSWORD') else '') + secrets.token_hex(32)


def write_secret(path, value):
    # Owner read/write, group read: the one-shot keyring-init step moves the group to the
    # containers' gid 1001 so the servers (uid 1001) and executor (uid 1002) can read the mount.
    pending = path.with_name('.' + path.name + '.pending')
    pending.unlink(missing_ok=True)
    fd = os.open(pending, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o640)
    try:
        with os.fdopen(fd, 'w') as stream:
            os.fchmod(stream.fileno(), 0o640)
            stream.write(value)
            stream.flush()
            os.fsync(stream.fileno())
        # A link never replaces a file another bootstrap created first.
        os.link(pending, path)
    finally:
        pending.unlink(missing_ok=True)


def initialize(directory):
    """Creates or migrates the install. Returns only non-secret settings."""
    directory = Path(directory).resolve()
    lock = json.loads((directory / 'images.json').read_text())
    if not all(IMMUTABLE.fullmatch(lock[name]) for name in ('server', 'executor', 'worker')):
        raise ValueError('The image lock must contain immutable sha256 references')
    env_file = directory / '.env'
    existing = env_file.read_text() if env_file.exists() else ''
    lines = existing.splitlines()
    values = dict(line.split('=', 1) for line in lines if line and not line.startswith('#'))
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
    for key in ('HONUA_IMAGE', 'HONUA_EXECUTOR_IMAGE', 'HONUA_RELEASE_IMAGE',
                'HONUA_RELEASE_WORKER_IMAGE', 'HONUA_PRIOR_IMAGE'):
        if key in values and not IMMUTABLE.fullmatch(values[key]):
            raise ValueError(f'{key} must be an immutable sha256 reference')
    if any(char in str(directory) for char in '\n\r$#\'"'):
        raise ValueError('Install directory contains unsupported dotenv characters')
    if values.get('HONUA_COMPOSE_PROJECT_DIRECTORY', str(directory)) != str(directory):
        raise ValueError('Install directory moved; update the saved project path explicitly')

    # Resolve every secret before changing anything: a saved file wins, an earlier install's
    # .env value migrates unchanged, and only a missing secret is generated.
    saved, legacy = {}, {}
    for key in ALL_SECRET_KEYS:
        path = secret_path(directory, key)
        if key in values:
            validate_secret(key, values[key])
            legacy[key] = values[key]
        if path.exists():
            saved[key] = path.read_text().strip()
            validate_secret(key, saved[key])
            if key in legacy and not secrets.compare_digest(saved[key], legacy[key]):
                raise ValueError(f'{key} differs between .env and its secrets file; keep one explicitly')
    if legacy:
        for name in COMPOSE_FILES:
            compose = (directory / name).read_text()
            if any(re.search(r'\$\{' + key + r'\b', compose) for key in legacy):
                raise ValueError(f'{name} still reads secrets from .env; extract the new package files first')

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
    for name in COMPOSE_FILES:
        (directory / name).chmod(0o640)

    resolved = {}
    for key in ALL_SECRET_KEYS:
        if key in saved:
            resolved[key] = saved[key]
            continue
        resolved[key] = legacy.get(key) or generate_secret(key)
        write_secret(secret_path(directory, key), resolved[key])
    if legacy:
        # The files are durable now; drop the migrated lines and keep every other setting.
        kept = [line for line in env_file.read_text().splitlines()
                if not (line and not line.startswith('#') and line.split('=', 1)[0] in legacy)]
        pending = directory / '.env.pending'
        pending.unlink(missing_ok=True)
        fd = os.open(pending, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o640)
        with os.fdopen(fd, 'w') as stream:
            stream.write(''.join(line + '\n' for line in kept))
            stream.flush()
            os.fsync(stream.fileno())
        os.replace(pending, env_file)
        env_file.chmod(0o640)
        for key in legacy:
            values.pop(key)

    image_override = directory / 'compose.executor.image.yaml'
    if not image_override.exists():
        image_override.write_text('services:\n  honua:\n    image: ' + values['HONUA_IMAGE'] + '\n')
        image_override.chmod(0o640)
    pfx = directory / 'secrets/keyring.pfx'
    if not pfx.exists():
        child_env = dict(os.environ, HONUA_KEYRING_PASSWORD=resolved['HONUA_KEYRING_PASSWORD'])
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
        # Secrets stay in their files; never hand them to Compose as environment.
        env = {key: value for key, value in os.environ.items() if key not in ALL_SECRET_KEYS}
        return subprocess.call(['docker', 'compose', 'up', '-d', '--wait', '--wait-timeout', '180'],
                               cwd=args.directory, env=dict(env, **values))
    except (OSError, ValueError, RuntimeError, KeyError):
        print('Quickstart initialization failed; check the image lock, Docker socket, OpenSSL and private install directory.',
              file=sys.stderr)
        return 1


if __name__ == '__main__':
    sys.exit(main())
