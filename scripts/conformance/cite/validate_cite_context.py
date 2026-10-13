#!/usr/bin/env python3
"""Admit bounded dispatch data before starting any CITE containers."""
import argparse
import os
import re
import tempfile
from pathlib import Path

from build_protocol_certification_fragment import FIXTURE_BY_SUITE, SUITE_BY_SURFACE, run_identity, timestamp
from requirements_context import load_context


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--output', type=Path)
    args = parser.parse_args()
    source = os.environ.get('SOURCE_SHA', '')
    producer = os.environ.get('GITHUB_SHA', '')
    image = os.environ.get('SERVER_IMAGE', '')
    cut = os.environ.get('CANDIDATE_CUT', '')
    if not re.fullmatch(r'[0-9a-f]{40}', source) or not re.fullmatch(r'ghcr\.io/honua-io/honua-server@sha256:[0-9a-f]{64}', image):
        raise ValueError('CITE candidate must be an exact source and immutable server image')
    timestamp(cut)
    run_identity(producer)
    raw = os.environ.get('REQUIREMENTS_CONTEXT', '').encode()
    with tempfile.TemporaryDirectory() as temporary:
        path = Path(temporary) / 'context.json'
        path.write_bytes(raw)
        load_context(path, source, producer, image.split('@')[1], cut, SUITE_BY_SURFACE, FIXTURE_BY_SUITE)
    if args.output:
        args.output.write_bytes(raw)


if __name__ == '__main__':
    main()
