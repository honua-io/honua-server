#!/usr/bin/env python3
"""After regeneration, require submitted projections to match canonical bytes and modes."""
import argparse
import importlib.util
from pathlib import Path
import stat
import subprocess

spec = importlib.util.spec_from_file_location(
    'generated_output_diff', Path(__file__).with_name('generated-output-diff.py'))
proof = importlib.util.module_from_spec(spec)
spec.loader.exec_module(proof)


def supplied_paths(base, head):
    policy = proof.policy_paths(base)
    changed = proof.git('diff', '--no-renames', '--name-only', '-z', base, head).decode().split('\0')[:-1]
    return [path for path in changed if any(path == root or path.startswith(root + '/') for root in policy)]


def prepare(base, head):
    # Regeneration must establish submitted bytes and modes from absent paths.
    # In particular, write_text preserves an existing executable bit, and some
    # generators only prune known extensions rather than arbitrary additions.
    for path in supplied_paths(base, head):
        output = Path(path)
        if any(parent.is_symlink() for parent in output.parents):
            raise ValueError(f'Generated output has a symlink parent: {path}')
        if output.exists() or output.is_symlink():
            output.unlink()


def mismatches(base, head):
    return [path for path in supplied_paths(base, head) if not matches_head(head, path)]


def matches_head(head, path):
    entry = proof.git('ls-tree', '-z', head, '--', path)
    output = Path(path)
    if not entry:
        return not output.exists() and not output.is_symlink()
    metadata, _ = entry.rstrip(b'\0').split(b'\t', 1)
    mode, kind, blob = metadata.decode().split()
    if kind != 'blob' or mode not in ('100644', '100755'):
        return False
    if not output.exists() or not stat.S_ISREG(output.lstat().st_mode):
        return False
    executable = bool(output.stat().st_mode & stat.S_IXUSR)
    return (executable == (mode == '100755')
            and output.read_bytes() == proof.git('cat-file', 'blob', blob))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--base', required=True)
    parser.add_argument('--head', default='HEAD')
    parser.add_argument('--prepare', action='store_true', help='remove submitted outputs before regeneration')
    args = parser.parse_args()
    try:
        if args.prepare:
            prepare(args.base, args.head)
            return
        rejected = mismatches(args.base, args.head)
    except (ValueError, OSError, subprocess.SubprocessError) as error:
        parser.exit(1, f'Cannot verify submitted generated files: {error}\n')
    if rejected:
        parser.exit(1, 'Submitted generated files differ from canonical regeneration:\n  '
                    + '\n  '.join(rejected) + '\nRegenerate and commit these outputs with the source change.\n')


if __name__ == '__main__':
    main()
