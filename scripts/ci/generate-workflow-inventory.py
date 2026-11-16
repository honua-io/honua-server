#!/usr/bin/env python3
"""Project workflow YAML into the authored inventory template without shared edits."""
from pathlib import Path
import argparse
import sys
import re
import yaml

ROOT = Path(__file__).resolve().parents[2]
ROW = re.compile(r'^\| `([^`]+\.ya?ml)` \| ([^|]*) \| ([^|]*) \| (.*)$')


def render(root: Path) -> str:
    workflows = {}
    for path in sorted((root / '.github/workflows').glob('*')):
        if path.suffix not in ('.yml', '.yaml'):
            continue
        data = yaml.safe_load(path.read_text())
        events = data.get('on', data.get(True, {}))
        triggers = [events] if isinstance(events, str) else list(events or {})
        name = str(data.get('name', path.stem)).replace('|', '&#124;')
        workflows[path.name] = (name, ', '.join(f'`{event}`' for event in triggers))
    lines, seen = [], set()
    # Historical retirement rows have three columns and are deliberately not
    # live workflow rows. Their authored explanations stay untouched.
    for line in (root / 'scripts/ci/workflow-inventory.template.md').read_text().splitlines():
        match = ROW.match(line)
        if match:
            filename, _, _, notes = match.groups()
            if filename not in workflows:
                continue
            if filename in seen:
                raise ValueError(f'duplicate live workflow row: {filename}')
            seen.add(filename)
            name, triggers = workflows[filename]
            line = f'| `{filename}` | {name} | {triggers} | {notes}'
        lines.append(line)
    missing = sorted(workflows.keys() - seen)
    if missing:
        lines += ['', '## Additional workflows', '',
                  '| Workflow file | Name | Triggers | Notes |', '|---|---|---|---|']
        for filename in missing:
            name, triggers = workflows[filename]
            lines.append(f'| `{filename}` | {name} | {triggers} | |')
    return '\n'.join(lines) + '\n'


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--check', action='store_true')
    # Validators project into a scratch file: a rewritten tracked page dirties
    # the checkout for later steps that require it unchanged (prove-nuget-auth.py).
    parser.add_argument('--output', type=Path, help='write the projection here instead of the tracked page')
    args = parser.parse_args()
    if args.check and args.output:
        parser.error('--check compares the tracked page; it takes no --output')
    path = ROOT / 'docs/internal/ci/workflow-inventory.md'
    output = render(ROOT)
    if args.check:
        # Drift is advisory, same as scripts/ci/report-generated-file-drift.sh.
        # Feature PRs cannot commit this projection; trunk automation publishes it.
        # render() still fails closed on duplicate live rows before this compare.
        if not path.is_file():
            parser.exit(1, 'Workflow inventory page is missing.\n')
        if path.read_text() != output:
            print(
                'Workflow inventory drift is advisory; trunk automation publishes this page. '
                'Feature PRs edit the template and workflow YAML and must not commit the projection.',
                file=sys.stderr)
    else:
        (args.output or path).write_text(output)


if __name__ == '__main__':
    main()
