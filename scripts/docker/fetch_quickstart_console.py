#!/usr/bin/env python3
"""Bind the nightly quickstart to a successful official Console trunk build."""
import argparse
import hashlib
import json
from pathlib import Path
import re
import subprocess
import tempfile
import zipfile

from package_quickstart import read_console_bundle

REPOSITORY = 'honua-io/honua-console'


def github(*arguments):
    result = subprocess.run(['gh', *arguments], stdin=subprocess.DEVNULL, capture_output=True,
                            text=True, timeout=180)
    if result.returncode:
        raise RuntimeError('Official Console build lookup failed; output withheld')
    return result.stdout


def select_build():
    runs = json.loads(github('api', f'repos/{REPOSITORY}/actions/workflows/ci.yml/runs'
                            '?branch=trunk&event=push&status=success&per_page=30'))['workflow_runs']
    for run in runs:
        if (run.get('head_repository') or {}).get('full_name') != REPOSITORY or (
                run.get('head_branch') != 'trunk' or run.get('event') != 'push' or
                run.get('conclusion') != 'success' or not re.fullmatch(r'[a-f0-9]{40}', run.get('head_sha', ''))):
            continue
        artifacts = json.loads(github('api', f"repos/{REPOSITORY}/actions/runs/{int(run['id'])}/artifacts"))['artifacts']
        if any(item['name'] == 'honua-console-web' and not item['expired'] for item in artifacts):
            return run
    raise ValueError('No successful official Console trunk build has an available web artifact')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    if args.output.exists():
        raise ValueError('Console output must be new')
    args.output.parent.mkdir(parents=True, exist_ok=True)
    run = select_build()
    with tempfile.TemporaryDirectory(prefix='honua-console-build-') as temporary:
        root = Path(temporary)
        github('run', 'download', str(run['id']), '--repo', REPOSITORY,
               '--name', 'honua-console-web', '--dir', str(root))
        console = root / 'wwwroot/console'
        if not console.is_dir():
            raise ValueError('Official Console artifact lacks its static web root')
        provenance = {'repository': REPOSITORY, 'workflow': 'ci.yml', 'runId': run['id'],
                      'sourceSha': run['head_sha'], 'artifact': 'honua-console-web'}
        # Keep the producer identity inside the checksum-bound customer archive.
        with zipfile.ZipFile(args.output, 'w', compression=zipfile.ZIP_DEFLATED) as bundle:
            for path in sorted(console.rglob('*')):
                if path.is_symlink():
                    raise ValueError('Console artifact contains a symbolic link')
                if path.is_file():
                    bundle.write(path, path.relative_to(console).as_posix())
            bundle.writestr('quickstart-build.json', json.dumps(provenance, indent=2) + '\n')
        digest = hashlib.sha256(args.output.read_bytes()).hexdigest()
        checked, _ = read_console_bundle(args.output, digest)
        checked.close()
        args.output.with_suffix('.sha256').write_text(digest + '\n')
        print(f"Bound official Console build {run['id']} at {run['head_sha']}", flush=True)


if __name__ == '__main__':
    main()
