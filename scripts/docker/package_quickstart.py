#!/usr/bin/env python3
"""Package the customer Compose install with a lock of immutable release images."""
import argparse
import json
from pathlib import Path
import re
import shutil
import tarfile

ROOT = Path(__file__).resolve().parents[2]
IMMUTABLE = re.compile(r'(?:[a-z0-9./:_-]+@)?sha256:[a-f0-9]{64}')


def package(destination, server, executor, worker, version):
    for image in (server, executor, worker):
        if not IMMUTABLE.fullmatch(image):
            raise ValueError('All quickstart images must be immutable sha256 references')
    destination = Path(destination)
    if destination.exists() and any(destination.iterdir()):
        raise ValueError('Package output must be an empty directory; never overwrite an installation')
    destination.mkdir(parents=True, exist_ok=True)
    source = (ROOT / 'docs/get-started/quickstart.md').read_text()
    compose = re.search(r'<!-- docs-validation:install.compose mode=run -->\s*```yaml\n(.*?)\n```', source, re.S).group(1)
    (destination / 'compose.yaml').write_text(compose + '\n')
    shutil.copyfile(ROOT / 'docs/get-started/compose.executor.yaml', destination / 'compose.executor.yaml')
    shutil.copyfile(Path(__file__).with_name('compose_quickstart.py'), destination / 'quickstart.py')
    max_schema = max(int(path.name.split('_', 1)[0]) for path in
                     (ROOT / 'src/Honua.Server/Migrations').glob('[0-9]*_*.sql'))
    (destination / 'images.json').write_text(json.dumps(dict(server=server, executor=executor, worker=worker,
                                                           version=version, maxSchema=max_schema), indent=2) + '\n')
    (destination / 'README.md').write_text(
        '# Honua local Compose quickstart\n\n'
        'Run `python3 quickstart.py` with Docker Compose and OpenSSL installed. '
        'Secrets are created locally in `.env` and `secrets/keyring.pfx`; preserve both.\n\n'
        'Serving API: http://localhost:18080. Management API and MCP: http://localhost:18082. '
        'Use HONUA_MANAGER_ADMIN_PASSWORD from `.env` to create separate proposer and approver '
        'API keys on the management API. Self approval is refused. Only approved proposals execute.\n\n'
        'The executor runs as uid 1002, targets only this project and serving service, and is '
        'the only process with Docker socket access. Docker socket access grants host control; '
        'use a dedicated local Docker host. No Docker socket is mounted in either HTTP server. '
        'Retain backups, manager database, and Redis volumes for recovery and audit.\n\n'
        'Images in images.json are immutable digests from one candidate build. '
        'For an upgrade, set HONUA_RELEASE_IMAGE, HONUA_RELEASE_WORKER_IMAGE and HONUA_RELEASE_VERSION '
        'in .env from the new release lock. Pull the new serving image before approving. '
        'For older images without a schema label, configure HONUA_PRIOR_MAX_SCHEMA from that exact image. '
        'Never guess its schema.\n')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--server-image', required=True)
    parser.add_argument('--executor-image', required=True)
    parser.add_argument('--worker-image', required=True)
    parser.add_argument('--version', required=True)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    package(args.output, args.server_image, args.executor_image, args.worker_image, args.version)
    with tarfile.open(args.output.with_suffix('.tar.gz'), 'w:gz') as archive:
        for file in sorted(args.output.iterdir()):
            archive.add(file, arcname=file.name)


if __name__ == '__main__':
    main()
