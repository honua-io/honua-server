#!/usr/bin/env python3
"""Validate a quarantine snapshot and emit its non-gating test matrix."""
import argparse
import hashlib
import json
from collections import defaultdict

from flake_quarantine import load_snapshot


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('snapshot')
    options = parser.parse_args()
    projects = defaultdict(list)
    for entry in load_snapshot(options.snapshot):
        projects[entry['project']].append(entry['test'])
    selections = [dict(project=project, suffix=hashlib.sha256(project.encode()).hexdigest()[:16],
                       filter='|'.join(f'FullyQualifiedName={test}' for test in sorted(tests)))
                  for project, tests in sorted(projects.items())]
    print(json.dumps(dict(include=selections), separators=(',', ':')))


if __name__ == '__main__':
    main()
