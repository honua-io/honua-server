#!/usr/bin/env python3
"""Admit source regeneration only with canonical verification, or proven publication."""
import argparse
import importlib.util
from pathlib import Path
import subprocess

HERE = Path(__file__).resolve().parent
spec = importlib.util.spec_from_file_location('generated_output_diff', HERE / 'generated-output-diff.py')
proof = importlib.util.module_from_spec(spec)
spec.loader.exec_module(proof)

VERIFICATION_INPUTS = (
    '.github/workflows/pr-gate.yml',
    'scripts/ci/docs-only-diff.sh',
    'scripts/ci/compute-fast-test-selection.py',
    'scripts/ci/regenerate-generated-files.sh',
    'scripts/ci/classify-generated-output.sh',
    '.github/actions/lean-gate/action.yml',
    'scripts/ci/check-submitted-generated-files.py',
    'scripts/ci/generated-output-diff.py',
    'scripts/ci/generated-files.sh',
)
ARCHITECTURE_INPUTS = ('src/', 'tests/dotnet/', 'scripts/ci/', '.github/workflows/', '.github/actions/')


def has_source_verification(base, head, authored):
    if not any(path.startswith(ARCHITECTURE_INPUTS) for path in authored):
        return False
    # Older branches must adopt both the validator and its required invocation.
    # Comparing trusted blobs also prevents a candidate from disabling either.
    for path in VERIFICATION_INPUTS:
        try:
            trusted = proof.git('show', f'{base}:{path}')
            candidate = proof.git('show', f'{head}:{path}')
        except subprocess.CalledProcessError:
            return False
        if candidate != trusted:
            return False
    return True


def check(base, head):
    ancestor = proof.git('merge-base', base, head).decode().strip()
    paths = proof.policy_paths(base)
    changed = proof.git('diff', '--no-renames', '--name-only', '-z', ancestor, head).decode().split('\0')[:-1]
    protected = [p for p in changed if any(p == item or p.startswith(item + '/') for item in paths)]
    if not protected:
        return []
    authored = [path for path in changed if path not in protected]
    if authored and has_source_verification(base, head, authored):
        return []
    parents = [line[7:] for line in proof.git('cat-file', '-p', head).decode().splitlines()
               if line.startswith('parent ')]
    # Reuse the canonical producer's exact commit/tree artifact proof. Branch
    # names, PR authors and labels cannot grant an exemption. A source edit
    # mixed into a generated publication cannot use the producer exemption.
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
        parser.exit(1, 'Generated outputs require source verification or canonical producer proof:\n  '
                    + '\n  '.join(rejected)
                    + '\nRegenerate alongside source changes and adopt the current trunk validation '
                    'step. Generated-only publications still require the canonical producer proof.\n')


if __name__ == '__main__':
    main()
