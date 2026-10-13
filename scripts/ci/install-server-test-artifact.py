#!/usr/bin/env python3
"""Transactionally install previously validated output roots on one filesystem."""
import json
import os
from pathlib import Path
import sys

stage, destination, manifest = map(Path, sys.argv[1:4])
project, configuration = sys.argv[4:6]
roots = [f"{project}/bin/{configuration}", f"{project}/obj"]
metadata = json.loads(manifest.read_text())
roots += metadata["static_web_asset_content_roots"]["payload"]
roots += metadata.get("required_dependency_output_roots", [])
# Nested static roots are already contained in a larger replacement.
roots = sorted(set(roots), key=lambda value: (len(Path(value).parts), value))
selected = []
for root in roots:
    if not any(Path(parent) in Path(root).parents for parent in selected):
        selected.append(root)
backup = stage / '.backup'
backup.mkdir()
installed, saved = [], []
try:
    for index, root in enumerate(selected):
        source, target = stage / root, destination / root
        if not source.is_dir():
            raise ValueError(f'Missing staged output root: {root}')
        # Reject checkout symlinks that could redirect writes outside destination.
        if any(parent.is_symlink() for parent in [target, *target.parents] if parent != destination.parent):
            raise ValueError(f'Symlink in destination path: {target}')
        target.parent.mkdir(parents=True, exist_ok=True)
        if target.exists():
            old = backup / str(index)
            os.replace(target, old)
            saved.append((old, target))
        os.replace(source, target)
        installed.append((target, source))
except BaseException:
    for target, source in reversed(installed):
        os.replace(target, source)
    for old, target in reversed(saved):
        os.replace(old, target)
    raise
