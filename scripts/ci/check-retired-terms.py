#!/usr/bin/env python3
"""Check added lines against the shared Honua vocabulary policy."""

import argparse
import json
from pathlib import Path, PurePosixPath
import re
import subprocess
import sys


POLICY_PATH = Path(__file__).with_name("retired-terms.v1.json")
ALLOWLIST_PATH = "scripts/ci/retired-terms-allowlist.v1.json"


def git(root, *arguments):
    return subprocess.run(
        ["git", "--literal-pathspecs", "-C", str(root), *arguments], check=True, capture_output=True
    ).stdout


def protected_path(path):
    parts = PurePosixPath(path).parts
    return "certification" in parts and (
        "agent-e2e" in parts
        or ("ai-arc" in parts and PurePosixPath(path).name.startswith("heldout"))
    )


def read_policy(path):
    policy = json.loads(path.read_text(encoding="utf-8"))
    rules = policy["rules"]
    if policy["version"] != 1 or not rules:
        raise ValueError("Expected a nonempty version 1 policy")
    identifiers = set()
    for rule in rules:
        if not all(isinstance(rule.get(key), str) and rule[key].strip()
                   for key in ("id", "literal", "replacement")):
            raise ValueError("Each rule needs an id, literal and replacement")
        if rule["id"] in identifiers:
            raise ValueError("Duplicate rule id")
        identifiers.add(rule["id"])
    return rules


def read_allowlist(path, rules):
    if not path.exists():
        return []
    document = json.loads(path.read_text(encoding="utf-8"))
    if document["version"] != 1:
        raise ValueError("Expected a version 1 allowlist")
    identifiers = {rule["id"] for rule in rules}
    entries = document["entries"]
    for entry in entries:
        if not all(isinstance(entry.get(key), str) and entry[key].strip()
                   for key in ("path", "rule", "line", "reason")):
            raise ValueError("Each exception needs an exact path, rule, line and reason")
        if entry["rule"] not in identifiers or any(c in entry["path"] for c in "*?["):
            raise ValueError("Exceptions must name a known rule and a literal path")
        if "\n" in entry["line"] or "\r" in entry["line"]:
            raise ValueError("An exception must match one complete line")
    return entries


def added_lines(patch):
    number = 0
    in_hunk = False
    for line in patch.decode("utf-8").splitlines():
        if line.startswith("@@ "):
            number = int(re.search(r"\+(\d+)", line).group(1))
            in_hunk = True
        elif in_hunk and line.startswith("+"):
            yield number, line[1:]
            number += 1
        elif in_hunk and line.startswith(" "):
            number += 1


def scan(root, base, policy_path, allowlist_path, scan_all=False):
    rules = read_policy(policy_path)
    exceptions = read_allowlist(allowlist_path, rules)
    patterns = [(rule, re.compile(re.escape(rule["literal"]), re.IGNORECASE)) for rule in rules]
    arguments = ("ls-files", "-z") if scan_all else ("diff", "--name-only", "-z", base, "--")
    names = git(root, *arguments).decode("utf-8").split("\0")
    policy_files = {policy_path.resolve(), allowlist_path.resolve()}
    findings = []
    for name in filter(None, names):
        path = root / name
        if protected_path(name) or path.resolve() in policy_files or path.is_symlink() or not path.is_file():
            continue
        if scan_all:
            body = path.read_bytes()
            if b"\0" in body:
                continue
            lines = enumerate(body.decode("utf-8").splitlines(), 1)
        else:
            patch = git(root, "diff", "--no-ext-diff", "--no-textconv", "--unified=0", base, "--", name)
            lines = added_lines(patch)
        for number, line in lines:
            for rule, pattern in patterns:
                if not pattern.search(line):
                    continue
                if any(entry["path"] == name and entry["rule"] == rule["id"]
                       and entry["line"] == line for entry in exceptions):
                    continue
                findings.append(f"{name}:{number}: {rule['id']}: use {rule['replacement']}")
    return findings


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", type=Path, default=Path.cwd())
    parser.add_argument("--base", help="Git revision to compare against (includes tracked working edits)")
    parser.add_argument("--all", action="store_true", help="Audit all tracked text, including existing debt")
    parser.add_argument("--policy", type=Path, default=POLICY_PATH)
    parser.add_argument("--allowlist", type=Path, help="Default: ROOT/scripts/ci/retired-terms-allowlist.v1.json")
    arguments = parser.parse_args()
    if not arguments.all and not arguments.base:
        parser.error("provide --base or --all")
    try:
        root = arguments.root.resolve()
        findings = scan(root, arguments.base, arguments.policy,
                        arguments.allowlist or root / ALLOWLIST_PATH, arguments.all)
    except (OSError, ValueError, KeyError, TypeError, subprocess.CalledProcessError) as error:
        print(f"Retired-term check could not complete: {type(error).__name__}", file=sys.stderr)
        return 2
    for finding in findings:
        print(finding)
    print(f"Retired-term check: {len(findings)} finding(s)")
    return 1 if findings else 0


if __name__ == "__main__":
    sys.exit(main())
