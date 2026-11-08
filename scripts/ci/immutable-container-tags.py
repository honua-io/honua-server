#!/usr/bin/env python3
"""Preserve published per-SHA tags; registry state is authoritative, not run status."""
import argparse
import json
import re
import subprocess
import sys
from xml.etree import ElementTree

VERSION_LABEL = 'org.opencontainers.image.version'


def inspect(reference):
    result = subprocess.run(['docker', 'buildx', 'imagetools', 'inspect', reference,
                             '--format', '{{.Manifest.Digest}}'], capture_output=True, text=True)
    if result.returncode:
        if re.search(r'manifest unknown|manifest_unknown|: not found|404 not found', result.stderr, re.I):
            return None
        raise RuntimeError(f'Cannot inspect {reference}: {result.stderr.strip()}')
    digest = result.stdout.strip()
    if not re.fullmatch(r'sha256:[0-9a-f]{64}', digest):
        raise RuntimeError(f'Invalid registry digest for {reference}')
    return digest


def versions(reference):
    result = subprocess.run(['docker', 'buildx', 'imagetools', 'inspect', reference,
                             '--format', '{{json .Image}}'], capture_output=True, text=True, check=True)
    found = set()

    def visit(value):
        if isinstance(value, dict):
            for key, child in value.items():
                if key.lower() == 'config' and isinstance(child, dict):
                    labels = child.get('Labels') or child.get('labels') or {}
                    found.add(labels.get(VERSION_LABEL))
                else:
                    visit(child)
        elif isinstance(value, list):
            for child in value:
                visit(child)

    visit(json.loads(result.stdout))
    return found


def existing(reference, release_version):
    digest = inspect(reference)
    if digest:
        print(f'::notice::Preserving immutable tag {reference} at {digest}', file=sys.stderr)
        if release_version and versions(reference.rsplit(':', 1)[0] + '@' + digest) != {release_version}:
            raise ValueError(f'{reference} already has a different or unknown release version; use a new SHA')
    return digest


def is_immutable(reference, sha):
    tag = reference.rsplit(':', 1)[-1]
    return bool(re.search(rf'(?:^|-)(?:{sha}|{sha[:7]})(?:$|-)', tag))


def filter_tags(tags, sha, release_version):
    # Finish every check before emitting any destination to a downstream publisher.
    return [tag for tag in dict.fromkeys(tag.strip() for tag in tags if tag.strip())
            if not is_immutable(tag, sha) or not existing(tag, release_version)]


def supports_full_variant(sha):
    result = subprocess.run(['git', 'show', f'{sha}:eng/Honua.BuildProfiles.props'],
                            text=True, capture_output=True)
    if result.returncode:
        commit = subprocess.run(['git', 'cat-file', '-e', f'{sha}^{{commit}}'], capture_output=True)
        if commit.returncode:
            raise RuntimeError(f'Cannot read candidate build profiles at {sha}')
        return False
    project = ElementTree.fromstring(result.stdout)
    return any(element.tag.rsplit('}', 1)[-1] == 'HonuaStoreProfile' for element in project.iter())


def publication_state(repository, sha, release_version):
    suffixes = ['nightly', 'nightly-aot', 'nightly-lambda-aot', 'nightly-jit', 'nightly-compose-executor', 'nightly-functions-aot']
    if repository.startswith('docker.io/'):
        suffixes.remove('nightly-compose-executor')
        suffixes.remove('nightly-functions-aot')
    tags = [f'{repository}:{suffix}-{sha[:7]}' for suffix in suffixes]
    tags += [f'{repository}:{suffix}-{sha[:7]}-{arch}'
             for suffix in ('nightly', 'nightly-lambda-aot', 'nightly-jit') for arch in ('amd64', 'arm64')]
    if supports_full_variant(sha):
        tags += [f'{repository}:nightly-jit-{sha[:7]}-full{suffix}'
                 for suffix in ('', '-amd64', '-arm64')]
    # Do not short circuit: an explicit version mismatch must fail even in a partial publication.
    states = [existing(tag, release_version) for tag in tags]
    return all(states), any(states)


def already_imaged(repository, sha, release_version):
    return publication_state(repository, sha, release_version)[0]


def check_publication(repositories, sha, release_version, expected_version):
    states = [publication_state(repository, sha, release_version) for repository in repositories]
    complete = all(state[0] for state in states)
    partial = any(state[1] for state in states) and not complete
    if partial and not release_version and expected_version:
        # A schedule must not complete half an RC with newly built nightly binaries.
        for repository in repositories:
            publication_state(repository, sha, expected_version)
    return complete, partial


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--sha', required=True)
    parser.add_argument('--release-version', default='')
    parser.add_argument('--expected-version', default='')
    parser.add_argument('--already-imaged', metavar='REPOSITORY', action='append')
    parser.add_argument('--tag', help='Return an existing immutable tag digest, or empty if absent')
    args = parser.parse_args()
    if not re.fullmatch(r'[0-9a-f]{40}', args.sha):
        parser.error('Expected a full lowercase commit SHA')
    try:
        if args.already_imaged:
            complete, partial = check_publication(args.already_imaged, args.sha, args.release_version, args.expected_version)
            print(f'imaged={int(complete)}')
            print(f'partial={int(partial)}')
        elif args.tag:
            if not is_immutable(args.tag, args.sha):
                raise ValueError('Expected a per-SHA tag')
            print(existing(args.tag, args.release_version) or '')
        else:
            tags = filter_tags(sys.stdin, args.sha, args.release_version)
            if tags:
                print('\n'.join(tags))
    except (OSError, RuntimeError, ValueError, subprocess.CalledProcessError) as error:
        print(f'::error::{error}', file=sys.stderr)
        return 1
    return 0


if __name__ == '__main__':
    sys.exit(main())
