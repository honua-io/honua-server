#!/usr/bin/env python3
"""Check added lines against the shared Honua vocabulary policy."""

import argparse
import difflib
import json
import os
from pathlib import Path, PurePosixPath
import re
import subprocess
import sys


POLICY_PATH = Path(__file__).with_name("retired-terms.v1.json")
ALLOWLIST_PATH = "scripts/ci/retired-terms-allowlist.v1.json"
GIT_ENVIRONMENT_OVERRIDES = {
    "GIT_DIR", "GIT_WORK_TREE", "GIT_INDEX_FILE", "GIT_COMMON_DIR",
    "GIT_OBJECT_DIRECTORY", "GIT_ALTERNATE_OBJECT_DIRECTORIES",
    "GIT_LITERAL_PATHSPECS", "GIT_GLOB_PATHSPECS", "GIT_NOGLOB_PATHSPECS", "GIT_ICASE_PATHSPECS",
}
DIFF_OPTIONS = ("--no-ext-diff", "--no-textconv", "--no-color", "--find-renames")


def git(root, *arguments):
    environment = {key: value for key, value in os.environ.items() if key not in GIT_ENVIRONMENT_OVERRIDES}
    return subprocess.run(
        ["git", "-c", "diff.relative=false", "-C", str(root), *arguments],
        check=True, capture_output=True, env=environment,
    ).stdout


def is_protected_path(path):
    parts = PurePosixPath(path).parts
    return "certification" in parts and (
        "agent-e2e" in parts
        or ("ai-arc" in parts and PurePosixPath(path).name.startswith("heldout"))
    )


def has_symlink_ancestor(root, path):
    current = root
    for part in path.relative_to(root).parts:
        current = current / part
        if current.is_symlink():
            return True
    return False


def read_entries(path, collection):
    document = json.loads(path.read_text(encoding="utf-8"))
    if not isinstance(document, dict) or type(document.get("version")) is not int or document["version"] != 1:
        raise ValueError("Expected a version 1 policy document")
    entries = document.get(collection)
    if not isinstance(entries, list) or not all(isinstance(entry, dict) for entry in entries):
        raise ValueError(f"Expected an array of objects for {collection}")
    return entries


def require_text(record, keys):
    if not all(isinstance(record.get(key), str) and record[key].strip() for key in keys):
        raise ValueError(f"Expected nonempty text for {', '.join(keys)}")


def read_policy(path):
    rules = read_entries(path, "rules")
    identifiers = set()
    if not rules:
        raise ValueError("Expected a nonempty policy")
    for rule in rules:
        require_text(rule, ("id", "literal", "replacement"))
        if rule["id"] in identifiers:
            raise ValueError("Duplicate rule id")
        identifiers.add(rule["id"])
    for rule in read_entries(path, "reviewRules"):
        require_text(rule, ("literal", "replacement", "exceptions"))
    return rules


def read_allowlist(path, rules):
    if not path.exists():
        return set()
    entries = read_entries(path, "entries")
    identifiers = {rule["id"] for rule in rules}
    for entry in entries:
        require_text(entry, ("path", "rule", "line", "reason"))
        relative = PurePosixPath(entry["path"])
        if entry["rule"] not in identifiers or any(c in entry["path"] for c in "*?["):
            raise ValueError("Exceptions need a known rule and literal path")
        if relative.is_absolute() or ".." in relative.parts or relative.as_posix() != entry["path"]:
            raise ValueError("Exceptions need a canonical relative path")
        if "\n" in entry["line"] or "\r" in entry["line"]:
            raise ValueError("An exception must match one complete line")
    return {(entry["path"], entry["rule"], entry["line"]) for entry in entries}


def added_lines(previous, current):
    previous_lines = previous.replace(b"\r\n", b"\n").split(b"\n")
    current_lines = current.replace(b"\r\n", b"\n").split(b"\n")
    changes = difflib.SequenceMatcher(None, previous_lines, current_lines, autojunk=False)
    for kind, _, _, start, end in changes.get_opcodes():
        if kind in ("insert", "replace"):
            for index in range(start, end):
                yield index + 1, current_lines[index].removesuffix(b"\r").decode("utf-8")


def changed_paths(status):
    records = iter(status.decode("utf-8").split("\0"))
    for change in filter(None, records):
        original = next(records)
        current = next(records) if change.startswith(("R", "C")) else original
        if not change.startswith("D"):
            yield original, current


def text_lines(path):
    body = path.read_bytes()
    if b"\0" in body:
        return []
    return [line.removesuffix("\r") for line in body.decode("utf-8").split("\n")]


class VocabularyCheck:
    def __init__(self, root, policy_path, allowlist_path=None):
        self.root = Path(git(root, "rev-parse", "--show-toplevel").decode("utf-8").strip())
        allowlist_path = allowlist_path or self.root / ALLOWLIST_PATH
        rules = read_policy(policy_path)
        self.patterns = [(rule, re.compile(re.escape(rule["literal"]), re.IGNORECASE)) for rule in rules]
        self.exceptions = read_allowlist(allowlist_path, rules)
        self.policy_files = {policy_path.resolve(), allowlist_path.resolve()}

    def is_safe_path(self, name):
        path = self.root / name
        if is_protected_path(name) or has_symlink_ancestor(self.root, path):
            return False
        resolved = path.resolve()
        return resolved.is_relative_to(self.root) and resolved not in self.policy_files

    def check_changes(self, base):
        commit = git(self.root, "rev-parse", "--verify", "--end-of-options", f"{base}^{{commit}}").decode().strip()
        merge_base = git(self.root, "merge-base", commit, "HEAD").decode().strip()
        current = git(self.root, "ls-files", "-z")
        previous = git(self.root, "ls-tree", "-r", "--name-only", "-z", merge_base)
        names = sorted(set((current + previous).decode("utf-8").split("\0")) - {""})
        safe = {name for name in names if self.is_safe_path(name)}
        if not safe:
            return []
        pathspecs = [f":(top,literal){name}" for name in sorted(safe)]
        pathspecs.extend(f":(top,exclude,literal){name}" for name in names if name not in safe)
        status = git(self.root, "diff", *DIFF_OPTIONS, "--name-status", "-z", merge_base, "--", *pathspecs)
        findings = []
        for original, name in changed_paths(status):
            findings.extend(self.changed_file_findings(merge_base, original, name))
        return findings

    def changed_file_findings(self, base, original, name):
        path = self.root / name
        if not self.is_safe_path(name) or not path.is_file():
            return []
        current = path.read_bytes()
        if b"\0" in current:
            return []
        entry = git(self.root, "ls-tree", base, "--", f":(top,literal){original}").split()
        previous = git(self.root, "show", f"{base}:{original}") if entry and entry[1] == b"blob" else b""
        return self.file_findings(name, added_lines(previous, current))

    def audit(self):
        names = git(self.root, "ls-files", "-z").decode("utf-8").split("\0")
        findings = []
        for name in filter(None, names):
            findings.extend(self.tracked_file_findings(name))
        return findings

    def tracked_file_findings(self, name):
        path = self.root / name
        if not self.is_safe_path(name) or not path.is_file():
            return []
        return self.file_findings(name, enumerate(text_lines(path), 1))

    def file_findings(self, name, lines):
        findings = []
        for number, line in lines:
            findings.extend(self.line_findings(name, number, line))
        return findings

    def line_findings(self, name, number, line):
        return [f"{name}:{number}: {rule['id']}: use {rule['replacement']}"
                for rule, pattern in self.patterns
                if pattern.search(line) and (name, rule["id"], line) not in self.exceptions]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", type=Path, default=Path.cwd())
    mode = parser.add_mutually_exclusive_group(required=True)
    mode.add_argument("--base", help="One Git revision; check changes since its merge base with HEAD")
    mode.add_argument("--all", action="store_true", help="Audit all tracked text, including existing debt")
    parser.add_argument("--policy", type=Path, default=POLICY_PATH)
    parser.add_argument("--allowlist", type=Path, help="Default: ROOT/scripts/ci/retired-terms-allowlist.v1.json")
    arguments = parser.parse_args()
    try:
        check = VocabularyCheck(arguments.root, arguments.policy, arguments.allowlist)
        findings = check.audit() if arguments.all else check.check_changes(arguments.base)
    except (OSError, ValueError, KeyError, TypeError, subprocess.CalledProcessError) as error:
        print(f"Retired-term check could not complete: {type(error).__name__}", file=sys.stderr)
        return 2
    for finding in findings:
        print(finding)
    print(f"Retired-term check: {len(findings)} finding(s)")
    return 1 if findings else 0


if __name__ == "__main__":
    sys.exit(main())
