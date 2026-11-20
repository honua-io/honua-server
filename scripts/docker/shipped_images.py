#!/usr/bin/env python3
"""List the third-party images the customer install artifacts pull, so release gates can scan them.

Honua-built images (server, executor, worker) arrive through ${...} lock variables and are gated where
they are built. Every other image a customer runs or builds from comes from the files below and must
be pinned by digest: a tag can move after review.
"""
import argparse
import re
import sys

from package_quickstart import CONSOLE_WEB_IMAGE, ROOT

SHIPPED_FILES = (
    'docs/get-started/quickstart.md',
    'docs/get-started/compose.executor.yaml',
    'docs/get-started/compose.console.yaml',
    'docs/get-started/linux-packages.md',
    'docs/get-started/windows-packages.md',
    'docker/proj-grids/Dockerfile',
)
REFERENCE = re.compile(r'^\s*(?:image|BASE_IMAGE):\s*["\']?([^\s"\'#]+)|^ARG BASE_IMAGE=(\S+)|^FROM\s+(?:--\S+\s+)*(\S+)')
LOCAL_BUILD = re.compile(r'^\s*build:')
PINNED = re.compile(r'[a-z0-9][a-z0-9./:_-]*@sha256:[a-f0-9]{64}')


def references(relative_path, text):
    """Yield (location, image) for each third-party image reference in one shipped file."""
    lines = text.splitlines()
    for number, line in enumerate(lines, 1):
        match = REFERENCE.match(line)
        if not match:
            continue
        image = next(group for group in match.groups() if group)
        builds_locally = number < len(lines) and LOCAL_BUILD.match(lines[number])
        if image.startswith('$') or builds_locally:
            continue
        yield f'{relative_path}:{number}', image


def shipped_images(root=None):
    """Return sorted (location, image) pairs across every shipped install file."""
    root = root or ROOT
    found = [('scripts/docker/package_quickstart.py:CONSOLE_WEB_IMAGE', CONSOLE_WEB_IMAGE)]
    for relative_path in SHIPPED_FILES:
        found.extend(references(relative_path, (root / relative_path).read_text(encoding='utf-8')))
    return sorted(found)


def unpinned(found):
    return [(location, image) for location, image in found if not PINNED.fullmatch(image)]


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--list', action='store_true', help='print each distinct image once, for scanning')
    args = parser.parse_args(argv)
    found = shipped_images()
    for location, image in unpinned(found):
        print(f'::error::{location} pulls {image} by tag; pin it by digest', file=sys.stderr)
    if unpinned(found):
        return 1
    if args.list:
        print('\n'.join(sorted({image for _, image in found})))
    return 0


if __name__ == '__main__':
    sys.exit(main())
