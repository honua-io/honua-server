#!/usr/bin/env python3
"""Package the customer Compose install with a lock of immutable release images."""
import argparse
import hashlib
import json
from pathlib import Path
import re
import shutil
import stat
import tarfile
import zipfile

ROOT = Path(__file__).resolve().parents[2]
IMMUTABLE = re.compile(r'(?:[a-z0-9./:_-]+@)?sha256:[a-f0-9]{64}')
SHA256 = re.compile(r'[a-f0-9]{64}')


def check_console_bundle(console, images):
    """`console` is the release's static Console zip and its SHA-256, or None; a Console image pin is refused."""
    if 'console' in images:
        raise ValueError('Console ships as a static bundle; use --console-bundle, not a Console image')
    if console is None:
        return
    bundle, digest = console
    if not SHA256.fullmatch(digest or ''):
        raise ValueError('The Console bundle and its lower-case release SHA-256 are supplied together')
    if hashlib.sha256(Path(bundle).read_bytes()).hexdigest() != digest:
        raise ValueError('The Console bundle does not match its release SHA-256')


def package(destination, images, version, console=None):
    check_console_bundle(console, images)
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
    for name in ('compose.executor.yaml', 'compose.console.yaml'):
        shutil.copyfile(ROOT / 'docs/get-started' / name, destination / name)
    shutil.copyfile(Path(__file__).with_name('compose_quickstart.py'), destination / 'quickstart.py')
    # The agent guide is published once, in the docs; the package carries that exact text.
    guide = (ROOT / 'docs/guides/connect/working-with-ai-agents.md').read_text()
    guide = re.search(r'<!-- agent-guide:begin -->\n(.*?)<!-- agent-guide:end -->', guide, re.S).group(1)
    (destination / 'AGENTS.md').write_text(guide.strip() + '\n', encoding='utf-8')
    package_agent_skills(destination)
    max_schema = max(int(path.name.split('_', 1)[0]) for path in
                     (ROOT / 'src/Honua.Server/Migrations').glob('[0-9]*_*.sql'))
    lock = dict(images, version=version, maxSchema=max_schema)
    if console is not None:
        lock['consoleBundle'] = package_console_bundle(destination, console)
    (destination / 'images.json').write_text(json.dumps(lock, indent=2) + '\n')
    (destination / 'README.md').write_text(
        '# Honua local Compose quickstart\n\n'
        'Run `python3 quickstart.py` with Docker Compose and OpenSSL installed. '
        'Each secret is created locally in its own file under `secrets/`, beside `secrets/keyring.pfx`; '
        'preserve that directory. `.env` holds only non-secret settings, so `docker compose config` '
        'never prints a secret. An install whose secrets are still in `.env` (an earlier package) '
        'migrates on the next `python3 quickstart.py` run, after this package\'s files replace its own.\n\n'
        'Serving API: http://localhost:18080. Management API and MCP: http://localhost:18082. '
        'When this package includes `console/`, run `python3 quickstart.py --profile console` after configuring '
        'OIDC on the serving service, then sign in at http://localhost:18080/console/. '
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


def package_console_bundle(destination, console):
    """Extracts the release's static Console, points it at this origin's sign-in broker, returns its digest."""
    bundle, digest = console
    root = destination / 'console'
    root.mkdir()
    with zipfile.ZipFile(bundle) as archive:
        for entry in archive.infolist():
            name = Path(entry.filename)
            kind = stat.S_IFMT(entry.external_attr >> 16)
            if name.is_absolute() or '..' in name.parts or kind not in (0, stat.S_IFREG, stat.S_IFDIR):
                raise ValueError('The Console bundle contains an unsafe entry')
            target = root / name
            if entry.is_dir():
                target.mkdir(parents=True, exist_ok=True)
                continue
            target.parent.mkdir(parents=True, exist_ok=True)
            with archive.open(entry) as source, open(target, 'xb') as output:
                shutil.copyfileobj(source, output)
    if not (root / 'index.html').is_file() or not (root / 'config.json').is_file():
        raise ValueError('The Console bundle must contain index.html and config.json at its root')
    config = json.loads((root / 'config.json').read_text())
    # Same-origin API and server-brokered OIDC: no client secret or token is placed in the bundle.
    config['apiBaseUrl'] = '/'
    config['auth'] = dict(mode='oidc', authority=None, clientId=None, scopes=[])
    (root / 'config.json').write_text(json.dumps(config, indent=2) + '\n')
    for path in root.rglob('*'):
        path.chmod(0o755 if path.is_dir() else 0o644)
    root.chmod(0o755)
    checksums = [f'{hashlib.sha256(path.read_bytes()).hexdigest()}  {path.relative_to(destination).as_posix()}\n'
                 for path in sorted(root.rglob('*')) if path.is_file()]
    (destination / 'console-files.sha256').write_text(''.join(checksums), encoding='utf-8')
    return digest


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
    parser.add_argument('--console-bundle', type=Path, help='Static Console release zip')
    parser.add_argument('--console-bundle-sha256', help='SHA-256 of that zip from the release lock')
    parser.add_argument('--version', required=True)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    images = dict(server=args.server_image, executor=args.executor_image, worker=args.worker_image)
    if (args.console_bundle is None) != (args.console_bundle_sha256 is None):
        parser.error('--console-bundle and --console-bundle-sha256 are supplied together')
    console = None if args.console_bundle is None else (args.console_bundle, args.console_bundle_sha256)
    package(args.output, images, args.version, console)
    with tarfile.open(args.output.with_suffix('.tar.gz'), 'w:gz') as archive:
        for file in sorted(args.output.iterdir()):
            archive.add(file, arcname=file.name)


if __name__ == '__main__':
    main()
