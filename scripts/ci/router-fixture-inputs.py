#!/usr/bin/env python3
"""List branch + staged/unstaged + untracked inputs; uncertainty selects everything."""
import subprocess


def changed_inputs():
    try:
        base = subprocess.check_output(
            ['git', 'merge-base', 'origin/trunk', 'HEAD'], stderr=subprocess.DEVNULL
        ).strip().decode('ascii')
        # --no-renames retains both sides, so moving a dependency cannot hide it.
        tracked = subprocess.check_output(
            ['git', 'diff', '--name-only', '--no-renames', '-z', base, 'HEAD', '--']
        )
        staged = subprocess.check_output(
            ['git', 'diff', '--cached', '--name-only', '--no-renames', '-z', '--']
        )
        unstaged = subprocess.check_output(
            ['git', 'diff', '--name-only', '--no-renames', '-z', '--']
        )
        untracked = subprocess.check_output(
            ['git', 'ls-files', '--others', '--exclude-standard', '-z']
        )
        paths = sorted(set((tracked + staged + unstaged + untracked).decode('utf-8').split('\0')) - {''})
        # The shell protocol is newline-delimited: fail closed for exotic names.
        if any('\n' in path or '\r' in path for path in paths):
            return ['*']
        return paths
    except (subprocess.CalledProcessError, UnicodeError):
        return ['*']


if __name__ == '__main__':
    for changed in changed_inputs():
        print(changed)
