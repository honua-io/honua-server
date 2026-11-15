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
COMPOSE_FILES = ('compose.yaml', 'compose.executor.yaml', 'compose.console.yaml')
# Present until a recreated Console edge serves the current settings and certificate.
CONSOLE_RELOAD_MARKER = '.console-reload-pending'


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


def parse_environment(lines):
    return dict(line.split('=', 1) for line in lines if line and not line.startswith('#'))


def update_environment(path, updates, removed=()):
    existing_group = path.stat().st_gid if path.exists() else None
    existing = path.read_text() if path.exists() else ''
    source_lines = existing.splitlines()
    values = parse_environment(source_lines)
    values.update(updates)
    for key in removed:
        values.pop(key, None)
    lines, written = [], set()
    for line in source_lines:
        if not line or line.startswith('#'):
            lines.append(line)
            continue
        key = line.split('=', 1)[0]
        if key in values and key not in written:
            lines.append(f'{key}={values[key]}')
            written.add(key)
    lines.extend(f'{key}={value}' for key, value in values.items() if key not in written)
    updated = ''.join(line + '\n' for line in lines)
    if updated == existing:
        return
    pending = path.with_name('.env.pending')
    pending.unlink(missing_ok=True)
    fd = os.open(pending, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o640)
    with os.fdopen(fd, 'w') as stream:
        if existing_group is not None:
            os.fchown(stream.fileno(), -1, existing_group)
        os.fchmod(stream.fileno(), 0o640)
        stream.write(updated)
        stream.flush()
        os.fsync(stream.fileno())
    os.replace(pending, path)
    path.chmod(0o640)


def console_web_image(directory, lock):
    image = lock.get('consoleWeb', '')
    bundle = lock.get('consoleBundle')
    if (not isinstance(image, str) or not IMMUTABLE.fullmatch(image) or not isinstance(bundle, dict)
            or not (directory / 'console/index.html').is_file() or not (directory / 'console.nginx.conf').is_file()):
        raise ValueError('The package must include the released Console bundle and an immutable Console web image')
    return image


def console_image_settings(directory, lock, values):
    """The Console pins this package supplies; a restart after an upgrade picks them up."""
    settings = dict(HONUA_CONSOLE_WEB_IMAGE=console_web_image(directory, lock),
                    HONUA_CONSOLE_PORT=values.get('HONUA_CONSOLE_PORT', '18083'))
    return {key: value for key, value in settings.items() if values.get(key) != value}


def console_profile_settings(directory, lock, values):
    separator = values.get('COMPOSE_PATH_SEPARATOR', ':')
    files = [name for name in values['COMPOSE_FILE'].split(separator) if name != 'compose.console.yaml']
    managed_image = 'compose.executor.image.yaml'
    files.insert(files.index(managed_image) if managed_image in files else len(files), 'compose.console.yaml')
    profiles = [name for name in values.get('COMPOSE_PROFILES', '').split(',') if name]
    if 'console' not in profiles:
        profiles.append('console')
    settings = dict(COMPOSE_FILE=separator.join(files), COMPOSE_PROFILES=','.join(profiles))
    settings = {key: value for key, value in settings.items() if values.get(key) != value}
    return dict(settings, **console_image_settings(directory, lock, values))


def console_configuration(values):
    """Public Console settings (console.config.json). Credentials never belong here."""
    mode = values.get('HONUA_CONSOLE_AUTH_MODE', 'builtin')
    auth = dict(mode=mode, authority=None, clientId=None, scopes=[])
    if mode == 'oidc':
        authority = values.get('HONUA_CONSOLE_OIDC_AUTHORITY', '')
        client_id = values.get('HONUA_CONSOLE_OIDC_CLIENT_ID', '')
        if not re.fullmatch(r'https://[^\s/?#@]+(/[^\s?#]*)?', authority) or not client_id.strip():
            raise ValueError('OIDC Console sign-in needs HONUA_CONSOLE_OIDC_AUTHORITY (https) and HONUA_CONSOLE_OIDC_CLIENT_ID')
        auth.update(authority=authority, clientId=client_id, scopes=['openid'])
    elif mode != 'builtin':
        raise ValueError('HONUA_CONSOLE_AUTH_MODE must be builtin or oidc')
    return dict(apiBaseUrl='/', auth=auth, v4Enabled=values.get('HONUA_CONSOLE_V4', 'true') != 'false')


def write_console_files(directory, values, openssl):
    """Writes Console's public settings and local certificate.

    A changed file the running edge has not loaded leaves CONSOLE_RELOAD_MARKER behind; main()
    recreates the edge and removes it, so a failed start keeps the restart pending."""
    # The web server (uid 101) reads these through bind mounts; none holds a credential.
    # Extraction applies the customer's umask, so set the bundle's modes here.
    (directory / 'console.nginx.conf').chmod(0o644)
    bundle = directory / 'console'
    for path in [bundle, *bundle.rglob('*')]:
        if not path.is_symlink():
            path.chmod(0o755 if path.is_dir() else 0o644)
    write_console_config(directory, console_configuration(values))
    write_console_certificate(directory, openssl)


def write_console_config(directory, configuration):
    config = directory / 'console.config.json'
    previous = config.read_text() if config.exists() else None
    text = json.dumps(configuration, indent=2) + '\n'
    if previous == text:
        return
    if previous is not None:
        (directory / CONSOLE_RELOAD_MARKER).touch()
    pending = config.with_name('.console.config.json.pending')
    pending.write_text(text)
    pending.chmod(0o644)
    os.replace(pending, config)


def write_console_certificate(directory, openssl):
    certificate = directory / 'secrets/console_tls_certificate'
    key = directory / 'secrets/console_tls_key'
    if certificate.exists() and key.exists():
        if console_certificate_current(certificate):
            return
        (directory / CONSOLE_RELOAD_MARKER).touch()
    pending_key, pending_certificate = key.with_name('.console_tls_key.pending'), certificate.with_name('.console_tls_certificate.pending')
    try:
        # A local, self-signed certificate for https://localhost only; nothing leaves this host.
        openssl('req', '-x509', '-newkey', 'ec', '-pkeyopt', 'ec_paramgen_curve:prime256v1', '-nodes',
                '-subj', '/CN=localhost', '-addext', 'subjectAltName=DNS:localhost,IP:127.0.0.1',
                '-addext', 'basicConstraints=critical,CA:FALSE', '-addext', 'keyUsage=critical,digitalSignature',
                '-addext', 'extendedKeyUsage=serverAuth', '-days', '397',
                '-keyout', str(pending_key), '-out', str(pending_certificate))
        pending_key.chmod(0o640)
        pending_certificate.chmod(0o644)
        os.replace(pending_key, key)
        os.replace(pending_certificate, certificate)
    finally:
        pending_key.unlink(missing_ok=True)
        pending_certificate.unlink(missing_ok=True)


MINIMUM_OPENSSL_MAJOR = 3


def require_openssl_3():
    result = subprocess.run(['openssl', 'version'], capture_output=True, text=True)
    match = re.match(r'OpenSSL (\d+)\.', result.stdout)
    if result.returncode or not match or int(match.group(1)) < MINIMUM_OPENSSL_MAJOR:
        raise RuntimeError('Console needs OpenSSL 3 or later')


def console_certificate_current(certificate):
    """Valid for another 30 days and an end-entity certificate (earlier ones lacked CA:FALSE)."""
    result = subprocess.run(['openssl', 'x509', '-in', str(certificate), '-noout', '-checkend', str(30 * 86400),
                             '-ext', 'basicConstraints'], capture_output=True, text=True)
    return result.returncode == 0 and 'CA:FALSE' in result.stdout


def console_changes(directory, profiles, values):
    """The .env additions and removals for Console. Reads and validates only; writes nothing."""
    enabled = 'console' in profiles or 'console' in values.get('COMPOSE_PROFILES', '').split(',')
    if not enabled:
        return False, {}, ()
    lock = json.loads((directory / 'images.json').read_text())
    if 'console' in profiles:
        additions = console_profile_settings(directory, lock, values)
    else:
        # A saved profile keeps the customer's file order; only the package's pins change.
        additions = console_image_settings(directory, lock, values)
    console_configuration({**values, **additions})
    require_openssl_3()
    # HONUA_CONSOLE_IMAGE pinned the retired server-hosted Console; the overlay reads the web image.
    retired = ('HONUA_CONSOLE_IMAGE',) if 'HONUA_CONSOLE_IMAGE' in values else ()
    return True, additions, retired


def openssl_runner(environment):
    def openssl(*args):
        result = subprocess.run(['openssl', *args], env=environment, capture_output=True)
        if result.returncode:
            raise RuntimeError('OpenSSL could not create a local certificate')
    return openssl


def ensure_keyring(directory, openssl):
    pfx = directory / 'secrets/keyring.pfx'
    if pfx.exists():
        return
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


def initialize(directory, profiles=()):
    """Creates or migrates the install. Returns only non-secret settings."""
    directory = Path(directory).resolve()
    lock = json.loads((directory / 'images.json').read_text())
    if not isinstance(lock, dict):
        raise ValueError('The image lock must be a JSON object')
    if not all(isinstance(lock.get(name), str) and IMMUTABLE.fullmatch(lock[name])
               for name in ('server', 'executor', 'worker')):
        raise ValueError('The image lock must contain immutable sha256 references')
    env_file = directory / '.env'
    existing = env_file.read_text() if env_file.exists() else ''
    lines = existing.splitlines()
    values = parse_environment(lines)
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
                'HONUA_RELEASE_WORKER_IMAGE', 'HONUA_PRIOR_IMAGE', 'HONUA_CONSOLE_WEB_IMAGE'):
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
    console_enabled, console_additions, retired = console_changes(directory, profiles, dict(defaults, **values))
    additions.update(console_additions)
    values.update(additions)
    for key in retired:
        values.pop(key)
    update_environment(env_file, additions, retired)
    if not console_enabled:
        # A restart left pending for a Console that is now off must not start it again.
        (directory / CONSOLE_RELOAD_MARKER).unlink(missing_ok=True)
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
        for key in legacy:
            values.pop(key)
        update_environment(env_file, {}, legacy)

    image_override = directory / 'compose.executor.image.yaml'
    if not image_override.exists():
        image_override.write_text('services:\n  honua:\n    image: ' + values['HONUA_IMAGE'] + '\n')
        image_override.chmod(0o640)
    openssl = openssl_runner(dict(os.environ, HONUA_KEYRING_PASSWORD=resolved['HONUA_KEYRING_PASSWORD']))
    ensure_keyring(directory, openssl)
    if console_enabled:
        write_console_files(directory, values, openssl)
    return values


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--directory', type=Path, default=Path(__file__).resolve().parent)
    parser.add_argument('--init-only', action='store_true')
    parser.add_argument('--profile', action='append', choices=('console',), default=[])
    args = parser.parse_args()
    try:
        values = initialize(args.directory, args.profile)
        if args.init_only:
            return 0
        # Secrets stay in their files; never hand them to Compose as environment.
        env = dict({key: value for key, value in os.environ.items() if key not in ALL_SECRET_KEYS}, **values)
        result = subprocess.call(['docker', 'compose', 'up', '-d', '--wait', '--wait-timeout', '180'],
                                 cwd=args.directory, env=env)
        marker = Path(args.directory) / CONSOLE_RELOAD_MARKER
        if result == 0 and marker.exists():
            # Single-file mounts keep the replaced file's old inode; a new container sees the new one.
            result = subprocess.call(['docker', 'compose', 'up', '-d', '--wait', '--wait-timeout', '180',
                                      '--force-recreate', '--no-deps', 'console'], cwd=args.directory, env=env)
            if result == 0:
                marker.unlink()
        return result
    except (OSError, ValueError, RuntimeError, KeyError) as error:
        print('Quickstart initialization failed; check the image lock, Docker socket, OpenSSL and private install directory.',
              file=sys.stderr)
        # Plain validation errors name settings, never their values. Decoding errors
        # are ValueError subclasses whose messages can expose private file contents.
        if type(error) is ValueError or isinstance(error, RuntimeError):
            print(str(error), file=sys.stderr)
        return 1


if __name__ == '__main__':
    sys.exit(main())
