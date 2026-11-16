#!/usr/bin/env python3
"""Reserve repository-state projections for the proven trunk regeneration producer."""
import argparse
import importlib.util
from pathlib import Path
import subprocess

HERE = Path(__file__).resolve().parent
spec = importlib.util.spec_from_file_location('generated_output_diff', HERE / 'generated-output-diff.py')
proof = importlib.util.module_from_spec(spec)
spec.loader.exec_module(proof)


def check(base, head):
    ancestor = proof.git('merge-base', base, head).decode().strip()
    paths = proof.policy_paths(base)
    changed = proof.git('diff', '--no-renames', '--name-only', '-z', ancestor, head).decode().split('\0')[:-1]
    protected = [p for p in changed if any(p == item or p.startswith(item + '/') for item in paths)]
    if not protected:
        return []
    parents = [line[7:] for line in proof.git('cat-file', '-p', head).decode().splitlines()
               if line.startswith('parent ')]
    # Reuse the canonical producer's exact commit/tree artifact proof. Branch
    # names, PR authors and labels cannot grant an exemption. A source edit
    # mixed into a generated publication always retains the feature-PR ban.
    if len(changed) == len(protected) and parents == [ancestor]:
        tree = proof.git('rev-parse', f'{head}^{{tree}}').decode().strip()
        if proof.producer_proof(ancestor, head, tree):
            return []
    return protected


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--base', required=True)
    parser.add_argument('--head', default='HEAD')
    args = parser.parse_args()
    try:
        rejected = check(args.base, args.head)
    except (ValueError, OSError, subprocess.SubprocessError) as error:
        parser.exit(1, f'Cannot verify generated-file ownership: {error}\n')
    if rejected:
        parser.exit(1, 'Feature PRs must not commit trunk-generated outputs:\n  '
                    + '\n  '.join(rejected)
                    + '\nEdit authored sources; preview generators locally, then restore these outputs. '
                    'The existing generated-files-on-trunk automation publishes them.\n')


if __name__ == '__main__':
    main()
