#!/usr/bin/env python3
"""Resolve the platform version once for every image in a nightly/candidate run."""
import argparse
import re

# Platform release train, deliberately independent of the local-build VersionPrefix.
NIGHTLY_BASE = '2026.1.0'
NUMBER = r'(?:0|[1-9][0-9]*)'
IDENTIFIER = rf'(?:{NUMBER}|[0-9A-Za-z-]*[A-Za-z-][0-9A-Za-z-]*)'
VERSION = re.compile(rf'{NUMBER}\.{NUMBER}\.{NUMBER}(?:-{IDENTIFIER}(?:\.{IDENTIFIER})*)?')


def derive(release_version: str, sha: str) -> tuple[str, str]:
    """Return SemVer and informational version; reject unsafe build-argument input."""
    if not re.fullmatch(r'[0-9a-f]{40}', sha):
        raise ValueError('Expected a full lowercase commit SHA')
    if release_version and not VERSION.fullmatch(release_version):
        raise ValueError('release_version must be SemVer without a v prefix or build metadata')
    version = release_version or f'{NIGHTLY_BASE}-nightly.{sha[:7]}'
    return version, f'{version}+{sha[:7]}'


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--release-version', default='')
    parser.add_argument('--sha', required=True)
    args = parser.parse_args()
    try:
        version, informational = derive(args.release_version, args.sha)
    except ValueError as error:
        parser.error(str(error))
    print(f'version={version}')
    print(f'informational_version={informational}')
