#!/usr/bin/env python3
"""Validate and relocate staged ASP.NET runtime content roots before installation."""

import json
from pathlib import Path, PurePosixPath
import sys


def absolute_root(value):
    """Reject ambiguous paths rather than normalizing traversal away."""
    if not isinstance(value, str) or not value.startswith('/'):
        raise ValueError(f'Content root must be absolute: {value!r}')
    root = PurePosixPath(value)
    if '..' in root.parts or any(ord(char) < 32 for char in value):
        raise ValueError(f'Unsafe content root: {value!r}')
    return root


class Relocator:
    def __init__(self, stage, destination, metadata):
        self.stage = Path(stage)
        self.destination = Path(destination).resolve()
        self.producer = absolute_root(metadata['repo_root'])

    def relocate(self, value, mvc=False):
        if mvc and value == '~':
            return value
        root = absolute_root(value)
        if not root.is_relative_to(self.producer):
            if mvc or not Path(root).is_dir():
                raise ValueError(f'Content root unavailable in consumer: {value!r}')
            return value
        relative = root.relative_to(self.producer)
        installed = self.destination / relative
        if not (self.stage / relative).is_dir() and not installed.is_dir():
            raise ValueError(f'Content root unavailable in consumer: {installed}')
        return str(installed)

    def rewrite(self, path):
        data = json.loads(path.read_text())
        if path.name == 'MvcTestingAppManifest.json':
            if not isinstance(data, dict):
                raise ValueError(f'MVC manifest must be an object: {path}')
            data = {key: self.relocate(value, mvc=True) for key, value in data.items()}
        else:
            if not isinstance(data, dict):
                raise ValueError(f'Static web assets manifest must be an object: {path}')
            roots = data.get('ContentRoots')
            if not isinstance(roots, list):
                raise ValueError(f'Static web assets ContentRoots must be an array: {path}')
            data['ContentRoots'] = [self.relocate(value) for value in roots]
        path.write_text(json.dumps(data, separators=(',', ':')) + '\n')


def main():
    stage, destination, manifest = sys.argv[1:]
    relocator = Relocator(stage, destination, json.loads(Path(manifest).read_text()))
    paths = sorted(Path(stage).rglob('*.staticwebassets.runtime.json'))
    paths += sorted(Path(stage).rglob('MvcTestingAppManifest.json'))
    for path in paths:
        relocator.rewrite(path)


if __name__ == '__main__':
    try:
        main()
    except (ValueError, OSError, KeyError, TypeError) as error:
        sys.exit(f'::error::Cannot relocate restored content roots: {error}')
