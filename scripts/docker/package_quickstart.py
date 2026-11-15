#!/usr/bin/env python3
"""Package the customer Compose install with a lock of immutable release images."""
import argparse
import hashlib
import io
import json
from pathlib import Path
import re
import shutil
import stat
import tarfile
import zipfile

ROOT = Path(__file__).resolve().parents[2]
IMMUTABLE = re.compile(r'(?:[a-z0-9./:_-]+@)?sha256:[a-f0-9]{64}')
# Serves the static Console bundle and terminates its local HTTPS origin (compose.console.yaml).
CONSOLE_WEB_IMAGE = ('docker.io/nginxinc/nginx-unprivileged:1.29-alpine@sha256:'
                     '0c79d56aee561a1d81c63f00eee5fb5fe29279560cdc55e91425133104c7fbe6')
CONSOLE_ENTRYPOINTS = ('index.html', 'config.json')
CONSOLE_MAX_FILES = 20000
CONSOLE_MAX_BYTES = 1 << 30


def package(destination, images, version):
    if any(name not in images for name in ('server', 'executor', 'worker')):
        raise ValueError('The package requires server, executor and worker image pins')
    for image in images.values():
        if not IMMUTABLE.fullmatch(image):
            raise ValueError('All quickstart images must be immutable sha256 references')
    destination = Path(destination)
    if destination.exists() and any(destination.iterdir()):
        raise ValueError('Package output must be an empty directory; never overwrite an installation')
    destination.mkdir(parents=True, exist_ok=True)
    source = (ROOT / 'docs/get-started/quickstart.md').read_text()
    compose = re.search(r'<!-- docs-validation:install.compose mode=run -->\s*```yaml\n(.*?)\n```', source, re.S).group(1)
    (destination / 'compose.yaml').write_text(compose + '\n')
    for name in ('compose.executor.yaml', 'compose.console.yaml', 'console.nginx.conf'):
        shutil.copyfile(ROOT / 'docs/get-started' / name, destination / name)
    shutil.copyfile(Path(__file__).with_name('compose_quickstart.py'), destination / 'quickstart.py')
    # The agent guide is published once, in the docs; the package carries that exact text.
    guide = (ROOT / 'docs/guides/connect/working-with-ai-agents.md').read_text()
    guide = re.search(r'<!-- agent-guide:begin -->\n(.*?)<!-- agent-guide:end -->', guide, re.S).group(1)
    (destination / 'AGENTS.md').write_text(guide.strip() + '\n', encoding='utf-8')
    package_agent_skills(destination)
    max_schema = max(int(path.name.split('_', 1)[0]) for path in
                     (ROOT / 'src/Honua.Server/Migrations').glob('[0-9]*_*.sql'))
    (destination / 'images.json').write_text(json.dumps(dict(images, version=version, maxSchema=max_schema), indent=2) + '\n')
    (destination / 'README.md').write_text(
        '# Honua local Compose quickstart\n\n'
        'Run `python3 quickstart.py` with Docker Compose and OpenSSL installed. '
        'Each secret is created locally in its own file under `secrets/`, beside `secrets/keyring.pfx`; '
        'preserve that directory. `.env` holds only non-secret settings, so `docker compose config` '
        'never prints a secret. An install whose secrets are still in `.env` (an earlier package) '
        'migrates on the next `python3 quickstart.py` run, after this package\'s files replace its own.\n\n'
        'Serving API: http://localhost:18080. Management API and MCP: http://localhost:18082. '
        'Use the management admin password in `secrets/honua_manager_admin_password` to create '
        'proposer API keys on the management API. A person reviews and approves each proposal '
        'in Console with a live interactive OIDC session; the human requester may approve their own proposal. '
        'API keys and agent tokens cannot approve. Configure the identity provider before operating approval gates. '
        'Only approved proposals execute.\n\n'
        'The executor runs as uid 1002, targets only this project and serving service, and is '
        'the only process with Docker socket access. Docker socket access grants host control; '
        'use a dedicated local Docker host. No Docker socket is mounted in either HTTP server. '
        'Retain backups, manager database, and Redis volumes for recovery and audit.\n\n'
        'Images in images.json are immutable digests from one candidate build. '
        'For an upgrade, set HONUA_RELEASE_IMAGE, HONUA_RELEASE_WORKER_IMAGE and HONUA_RELEASE_VERSION '
        'in .env from the new release lock. Pull the new serving image before approving. '
        'For older images without a schema label, configure HONUA_PRIOR_MAX_SCHEMA from that exact image. '
        'Never guess its schema.\n\n'
        'AI coding agents working in this directory: read `AGENTS.md` first. It covers secrets, '
        'approvals, upgrades and verifying work before reporting it done. '
        'Task-specific skills are in skills/. To install for Claude Code, run '
        '`mkdir -p .claude/skills && cp -R skills/. .claude/skills/`; for Codex, run '
        '`mkdir -p .agents/skills && cp -R skills/. .agents/skills/`. '
        'Review existing honua-* directories before replacing matching files; repeat after upgrades. '
        'Both hosts receive the same source bytes. Verify agent files with '
        '`sha256sum -c agent-files.sha256` after verifying the release archive checksum.\n')


def unsafe_entry(entry):
    name = entry.filename
    parts = name.rstrip('/').split('/')
    return (name.startswith('/') or '\\' in name or ':' in parts[0]
            or any(part in ('', '.', '..') for part in parts) or stat.S_ISLNK(entry.external_attr >> 16))


def read_console_bundle(path, expected_sha256):
    """Checks the released Console archive before anything is written."""
    if not re.fullmatch(r'[a-f0-9]{64}', expected_sha256 or ''):
        raise ValueError('The Console bundle needs its release sha256 (64 lower-case hex digits)')
    data = Path(path).read_bytes()
    digest = hashlib.sha256(data).hexdigest()
    if digest != expected_sha256:
        raise ValueError('The Console bundle does not match its release sha256')
    archive = zipfile.ZipFile(io.BytesIO(data))
    entries = archive.infolist()
    names, folded = set(), set()
    total = 0
    for entry in entries:
        name = entry.filename
        if unsafe_entry(entry):
            raise ValueError('The Console bundle contains an unsafe path')
        if not entry.is_dir():
            if name.lower() in folded:
                raise ValueError('The Console bundle contains duplicate paths')
            folded.add(name.lower())
            names.add(name)
            total += entry.file_size
    if len(entries) > CONSOLE_MAX_FILES or total > CONSOLE_MAX_BYTES:
        raise ValueError('The Console bundle is larger than a Console release')
    folders = {'/'.join(name.split('/')[:depth]).lower() for name in names for depth in range(1, name.count('/') + 1)}
    for entry in entries:
        if entry.is_dir():
            parts = entry.filename.rstrip('/').lower().split('/')
            folders |= {'/'.join(parts[:depth]) for depth in range(1, len(parts) + 1)}
    if folders & folded:
        raise ValueError('The Console bundle uses one path as both a file and a folder')
    if any(name not in names for name in CONSOLE_ENTRYPOINTS) or not any(n.startswith('_framework/') for n in names):
        raise ValueError('The Console bundle is missing index.html, config.json or _framework/')
    if '<base href="/console/"' not in archive.read('index.html').decode('utf-8', 'replace'):
        raise ValueError('The Console bundle must be built for /console/')
    return archive, dict(sha256=digest, files=len(names))


def add_console_bundle(destination, console):
    """Adds a bundle checked by read_console_bundle to a package that package() wrote."""
    archive, receipt = console
    extract_console_bundle(archive, destination / 'console')
    lock_file = destination / 'images.json'
    lock = json.loads(lock_file.read_text())
    lock.update(consoleWeb=CONSOLE_WEB_IMAGE, consoleBundle=receipt)
    lock_file.write_text(json.dumps(lock, indent=2) + '\n')
    with (destination / 'README.md').open('a', encoding='utf-8') as readme:
        readme.write('\nConsole: run `python3 quickstart.py --profile console`, then open https://localhost:18083/console/ '
                     'and trust the locally generated certificate in `secrets/console_tls_certificate`. Sign in with '
                     'the serving admin password in `secrets/honua_admin_password`, or set HONUA_CONSOLE_AUTH_MODE=oidc '
                     'to use your identity provider (see the quickstart guide).\n')


def extract_console_bundle(archive, target):
    target.mkdir()
    for entry in archive.infolist():
        path = target.joinpath(*entry.filename.rstrip('/').split('/'))
        if entry.is_dir():
            path.mkdir(parents=True, exist_ok=True)
            continue
        path.parent.mkdir(parents=True, exist_ok=True)
        with archive.open(entry) as source, open(path, 'xb') as output:
            shutil.copyfileobj(source, output)
    # Readable by the web server's user; the bundle holds no credentials.
    for path in [target, *target.rglob('*')]:
        path.chmod(0o755 if path.is_dir() else 0o644)


def package_agent_skills(destination):
    shutil.copytree(ROOT / 'skills', destination / 'skills')
    agent_files = [destination / 'AGENTS.md']
    agent_files.extend(path for path in (destination / 'skills').rglob('*') if path.is_file())
    checksums = []
    for path in sorted(agent_files):
        digest = hashlib.sha256(path.read_bytes()).hexdigest()
        checksums.append(f'{digest}  {path.relative_to(destination).as_posix()}\n')
    (destination / 'agent-files.sha256').write_text(''.join(checksums), encoding='utf-8')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--server-image', required=True)
    parser.add_argument('--executor-image', required=True)
    parser.add_argument('--worker-image', required=True)
    parser.add_argument('--console-bundle', type=Path, help='released honua-console-<version>.zip')
    parser.add_argument('--console-bundle-sha256', help='sha256 from the bundle\'s release receipt')
    parser.add_argument('--version', required=True)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    images = dict(server=args.server_image, executor=args.executor_image, worker=args.worker_image)
    if (args.console_bundle is None) != (args.console_bundle_sha256 is None):
        parser.error('--console-bundle and --console-bundle-sha256 go together')
    # Check the bundle before writing anything.
    console = read_console_bundle(args.console_bundle, args.console_bundle_sha256) if args.console_bundle else None
    package(args.output, images, args.version)
    if console is not None:
        add_console_bundle(args.output, console)
    with tarfile.open(args.output.with_suffix('.tar.gz'), 'w:gz') as archive:
        for file in sorted(args.output.iterdir()):
            archive.add(file, arcname=file.name)


if __name__ == '__main__':
    main()
