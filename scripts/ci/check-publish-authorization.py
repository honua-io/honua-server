#!/usr/bin/env python3
"""Every workflow job that publishes must be authorized by ref and name an environment.

A job publishes when it pushes an image or index to a registry, publishes a package,
or creates a release or its assets. Such a job must
  * depend, directly or through `needs`, on the shared authorization job
    (.github/workflows/publish-authorization.yml); and
  * declare an `environment:`.
A trunk test in `if:` is not enough: GitHub compares strings without case, so
`github.ref == 'refs/heads/trunk'` also holds on a branch named TRUNK, while the shared job compares
the ref exactly. Any status function (success(), always(), cancelled(), failure()) replaces the
implicit success() check, so such a job inherits authorization only when a required conjunct of its
`if:` is success() or that need's `result == 'success'`.
The authorization job takes only literal tag globs that start with a letter or digit.
A job that cannot meet a rule yet is listed with its reason in
scripts/ci/publish-authorization-exceptions.json. An exception for a rule the job meets, or for
a job that does not publish, fails the check, so the list only shrinks.
"""
from __future__ import annotations

import argparse
import json
import re
import sys
from pathlib import Path

import yaml

ROOT = Path(__file__).resolve().parents[2]
AUTHORIZER = './.github/workflows/publish-authorization.yml'
STATUS_FUNCTION = re.compile(r'\b(?:success|always|cancelled|failure)\s*\(', re.IGNORECASE)
LITERAL_TAG_GLOB = re.compile(r'^[A-Za-z0-9][^\s$]*$')
RULES = ('authorization', 'environment')
PUBLISHING_ACTIONS = ('softprops/action-gh-release@', 'actions/deploy-pages@', 'pypa/gh-action-pypi-publish@')
PUBLISHING_COMMANDS = re.compile(
    r'\bdocker\s+push\b|\bimagetools\s+create\b|promote-verified-image\.py|\bdotnet\s+nuget\s+push\b'
    r'|\bgh\s+release\s+(?:create|upload|edit)\b|\bcrane\s+(?:copy|push|tag)\b|\boras\s+push\b'
    r'|\bnpm\s+publish\b|\btwine\s+upload\b|worker_publication\.py\s+publish|gp_devkit_publication\.py\s+publish\b')
LOCAL_REGISTRY = re.compile(r'localhost:\d+|127\.0\.0\.1:\d+')


def _pushes_image(step: dict) -> bool:
    settings = step.get('with') or {}
    if not str(step.get('uses', '')).startswith('docker/build-push-action@'):
        return False
    pushed = str(settings.get('push', '')).lower() not in ('', 'false') or 'push=true' in str(settings.get('outputs', ''))
    return pushed and not LOCAL_REGISTRY.search(str(settings.get('tags', '')) + str(settings.get('outputs', '')))


def _publishes(step: dict) -> bool:
    uses, settings = str(step.get('uses', '')), step.get('with') or {}
    if uses.startswith(PUBLISHING_ACTIONS) or _pushes_image(step):
        return True
    if uses.startswith('actions/attest-build-provenance@') and str(settings.get('push-to-registry', '')).lower() == 'true':
        return True
    command = str(step.get('run', ''))
    return bool(PUBLISHING_COMMANDS.search(command)) and not LOCAL_REGISTRY.search(command)


def _top_level(expression: str, token: str) -> list[int]:
    """Offsets of `token` outside quotes and parentheses."""
    found, depth, quoted, index = [], 0, False, 0
    while index < len(expression):
        char = expression[index]
        if char == "'":
            quoted = not quoted
        elif not quoted and char in '()':
            depth += 1 if char == '(' else -1
        elif not quoted and depth == 0 and expression.startswith(token, index):
            found.append(index)
            index += len(token)
            continue
        index += 1
    return found


def _wrapped(expression: str) -> bool:
    """True when the first parenthesis closes at the last character."""
    depth, quoted = 0, False
    for index, char in enumerate(expression):
        if char == "'":
            quoted = not quoted
        elif not quoted and char in '()':
            depth += 1 if char == '(' else -1
            if depth == 0:
                return index == len(expression) - 1
    return False


def _unwrap(expression: str) -> str:
    expression = expression.strip()
    if expression.startswith('${{') and expression.endswith('}}'):
        expression = expression[3:-2].strip()
    while expression.startswith('(') and _wrapped(expression):
        expression = expression[1:-1].strip()
    return expression


def conjuncts(condition: object) -> list[str]:
    """The terms a job condition requires, lower-cased; a disjunction or an embedded `${{` contributes none."""
    expression = _unwrap(str(condition or ''))
    if not expression or '${{' in expression or _top_level(expression, '||'):
        return []
    cuts = _top_level(expression, '&&')
    if not cuts:
        return [' '.join(expression.lower().split())]
    terms, start = [], 0
    for cut in cuts + [len(expression)]:
        terms.extend(conjuncts(expression[start:cut]))
        start = cut + 2
    return terms


def _inherits(condition: object, parent: str) -> bool:
    """Whether a job with this `if:` runs only after `parent` succeeded."""
    if not STATUS_FUNCTION.search(str(condition or '')):
        return True
    required = set(conjuncts(condition))
    return 'success()' in required or f"needs.{parent.lower()}.result == 'success'" in required


def _authorized(jobs: dict, job_id: str, seen: frozenset = frozenset()) -> bool:
    job = jobs.get(job_id) or {}
    if job.get('uses') == AUTHORIZER:
        return True
    needs = job.get('needs') or []
    needs = [needs] if isinstance(needs, str) else needs
    return any(_authorized(jobs, parent, seen | {job_id}) for parent in needs
               if parent not in seen and _inherits(job.get('if', ''), parent))


def _tag_pattern_problems(name: str, jobs: dict) -> list[str]:
    problems = []
    for job_id, job in jobs.items():
        if (job or {}).get('uses') != AUTHORIZER:
            continue
        patterns = str(((job or {}).get('with') or {}).get('tag-patterns', ''))
        if not all(LITERAL_TAG_GLOB.match(pattern) for pattern in patterns.split()):
            problems.append(f'{name}:{job_id}: tag-patterns {patterns!r} must be literal globs that start with a letter or digit')
    return problems


def _rule_problems(key: str, met: dict[str, bool], excused: dict) -> list[str]:
    problems = []
    for rule in RULES:
        if met[rule] and rule in excused:
            problems.append(f'{key}: the {rule} exception no longer applies; remove it')
        elif not met[rule] and rule not in excused:
            problems.append(f'{key}: publishing job without {rule}')
    return problems


def _exception_problems(exceptions: dict[str, dict], used: set) -> list[str]:
    problems = []
    for key, rules in sorted(exceptions.items()):
        for rule, reason in sorted((rules or {}).items()):
            if rule not in RULES or not isinstance(reason, str) or not reason.strip():
                problems.append(f'{key}: exception {rule!r} needs a rule in {RULES} and a reason')
            elif (key, rule) not in used:
                problems.append(f'{key}: the {rule} exception names no publishing job; remove it')
    return problems


def findings(workflows: dict[str, dict], exceptions: dict[str, dict]) -> list[str]:
    """`workflows` maps a file name to its parsed YAML; `exceptions` maps `file:job` to rule reasons."""
    problems, used = [], set()
    for name, document in sorted(workflows.items()):
        jobs = (document or {}).get('jobs') or {}
        problems.extend(_tag_pattern_problems(name, jobs))
        for job_id, job in jobs.items():
            if not any(_publishes(step) for step in (job or {}).get('steps') or []):
                continue
            key = f'{name}:{job_id}'
            excused = exceptions.get(key) or {}
            used.update((key, rule) for rule in RULES if rule in excused)
            met = {'authorization': _authorized(jobs, job_id), 'environment': bool(job.get('environment'))}
            problems.extend(_rule_problems(key, met, excused))
    return problems + _exception_problems(exceptions, used)


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument('--root', type=Path, default=ROOT)
    args = parser.parse_args(argv)
    workflows = {path.name: yaml.safe_load(path.read_text(encoding='utf-8'))
                 for path in sorted((args.root / '.github/workflows').glob('*.y*ml'))}
    exceptions = json.loads((args.root / 'scripts/ci/publish-authorization-exceptions.json').read_text(encoding='utf-8'))
    problems = findings(workflows, exceptions)
    for problem in problems:
        print(f'::error::{problem}')
    if not problems:
        print(f'Publish authorization: {len(workflows)} workflows checked, {len(exceptions)} jobs with listed exceptions.')
    return 1 if problems else 0


if __name__ == '__main__':
    sys.exit(main())
