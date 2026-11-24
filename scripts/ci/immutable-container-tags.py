#!/usr/bin/env python3
"""Preserve published per-SHA tags; registry state is authoritative, not run status."""
import argparse
import json
import re
import subprocess
import sys
from xml.etree import ElementTree

VERSION_LABEL = 'org.opencontainers.image.version'
REVISION_LABEL = 'org.opencontainers.image.revision'


class ReleaseVersionMismatch(ValueError):
    pass


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


def image_labels(reference, label):
    result = subprocess.run(['docker', 'buildx', 'imagetools', 'inspect', reference,
                             '--format', '{{json .Image}}'], capture_output=True, text=True, check=True)
    found = set()

    def visit(value):
        if isinstance(value, dict):
            for key, child in value.items():
                if key.lower() == 'config' and isinstance(child, dict):
                    labels = child.get('Labels') or child.get('labels') or {}
                    found.add(labels.get(label))
                else:
                    visit(child)
        elif isinstance(value, list):
            for child in value:
                visit(child)

    visit(json.loads(result.stdout))
    return found


def existing(reference, release_version, *, revision=''):
    digest = inspect(reference)
    if digest:
        print(f'::notice::Preserving immutable tag {reference} at {digest}', file=sys.stderr)
        pinned = reference.rsplit(':', 1)[0] + '@' + digest
        if revision and image_labels(pinned, REVISION_LABEL) != {revision}:
            raise ValueError(f'{reference} already has a different or unknown source revision; use a new SHA')
        if release_version and image_labels(pinned, VERSION_LABEL) != {release_version}:
            raise ReleaseVersionMismatch(f'{reference} already has a different or unknown release version; use a new SHA')
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


def uses_jit_default(sha):
    result = subprocess.run(['git', 'show', f'{sha}:.github/workflows/nightly-container-build.yml'], text=True, capture_output=True)
    if result.returncode:
        raise RuntimeError(f'Cannot read candidate publication workflow at {sha}')
    return 'type=raw,value=nightly-aot-${{ needs.select-candidate.outputs.sha7 }}' in result.stdout


def common_publication_tags(repository, sha):
    suffixes = ['nightly', 'nightly-aot']
    if not repository.startswith('docker.io/'):
        suffixes.append('nightly-compose-executor')
    return [f'{repository}:{suffix}-{sha[:7]}' for suffix in suffixes]


def architecture_tags(repository, sha, suffixes):
    return [f'{repository}:{suffix}-{sha[:7]}-{arch}'
            for suffix in suffixes for arch in ('amd64', 'arm64')]


def ecs_publication_tags(repository, sha):
    return common_publication_tags(repository, sha) + architecture_tags(repository, sha, ['nightly-aot'])


def publication_tags(repository, sha):
    tags = common_publication_tags(repository, sha)
    other_variants = ['nightly-lambda-aot', 'nightly-jit']
    tags += [f'{repository}:{suffix}-{sha[:7]}' for suffix in other_variants]
    architecture_default = 'nightly-aot' if uses_jit_default(sha) else 'nightly'
    tags += architecture_tags(repository, sha, [architecture_default, *other_variants])
    if supports_full_variant(sha):
        tags += [f'{repository}:nightly-jit-{sha[:7]}-full{suffix}'
                 for suffix in ('', '-amd64', '-arm64')]
        if architecture_default == 'nightly-aot':
            tags.append(f'{repository}:nightly-{sha[:7]}-full')
    return tags


def publication_state(tags, release_version, *, revision=''):
    # Do not short circuit: an explicit version mismatch must fail even in a partial publication.
    states = [existing(tag, release_version, revision=revision) for tag in tags]
    return all(states), any(states)


def check_publication(tags_by_repository, release_version, expected_version, *, revision=''):
    states = [publication_state(tags, release_version, revision=revision) for tags in tags_by_repository]
    complete = all(state[0] for state in states)
    partial = any(state[1] for state in states) and not complete
    if partial and not release_version and expected_version:
        # A schedule must not complete half an RC with newly built nightly binaries.
        for tags in tags_by_repository:
            publication_state(tags, expected_version)
    return complete, partial


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--sha', required=True)
    parser.add_argument('--release-version', default='')
    parser.add_argument('--expected-version', default='')
    parser.add_argument('--already-imaged', metavar='REPOSITORY', action='append')
    parser.add_argument('--ecs-only', action='store_true', help='Check only ECS AOT and Compose evidence tags')
    parser.add_argument('--scheduled', action='store_true', help='Skip a partial publication with another version')
    parser.add_argument('--tag', help='Return an existing immutable tag digest, or empty if absent')
    args = parser.parse_args()
    if not re.fullmatch(r'[0-9a-f]{40}', args.sha):
        parser.error('Expected a full lowercase commit SHA')
    if args.ecs_only and not (args.already_imaged or args.tag):
        parser.error('--ecs-only requires --already-imaged or --tag')
    try:
        if args.already_imaged:
            select_tags = ecs_publication_tags if args.ecs_only else publication_tags
            tags_by_repository = [select_tags(repository, args.sha) for repository in args.already_imaged]
            release_version = args.release_version
            if args.ecs_only and not release_version:
                release_version = args.expected_version
            try:
                complete, partial = check_publication(tags_by_repository, release_version, args.expected_version,
                                                      revision=args.sha if args.ecs_only else '')
            except ReleaseVersionMismatch as error:
                if not args.scheduled or args.ecs_only or args.release_version:
                    raise
                print(f'::notice::Skipping scheduled publication for {args.sha}: {error}', file=sys.stderr)
                print('skip_publish=1')
                return 0
            print(f'imaged={int(complete)}')
            print(f'partial={int(partial)}')
        elif args.tag:
            if not is_immutable(args.tag, args.sha):
                raise ValueError('Expected a per-SHA tag')
            print(existing(args.tag, args.release_version,
                           revision=args.sha if args.ecs_only else '') or '')
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
