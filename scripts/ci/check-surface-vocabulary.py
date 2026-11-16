#!/usr/bin/env python3
"""Fail when a public surface names a Honua object with a different term.

Surfaces are the REST routes in the endpoint registry, MCP tool names (the
generated roster and tool-name constants), Console page routes and the SDK, CLI
and skill operations recorded in the workflow stage map. Each name, including
its route parameter names, is split into singular words and compared with the
other terms the policy lists for every glossary object. Exceptions are exact;
an exception whose name is absent fails, so each rename shrinks the list, and
with --base a pending rename may only cover a name that already exists there.
"""
from __future__ import annotations

import argparse
import fnmatch
import json
import re
import subprocess
import sys
from pathlib import Path

POLICY = "scripts/ci/surface-vocabulary.v1.json"
EXCEPTIONS = "scripts/ci/surface-vocabulary-exceptions.v1.json"
STAGE_MAP = "docs/guides/workflows/stage-map.v1.json"
MCP_ROSTER = "docs/gis/data/mcp-tool-roster.v1.json"
ROSTER_LISTS = ("static", "projectedAdmin", "requiresDurableControlPlane")
KINDS = ("protocol", "technical", "pending")
EXTRACTORS = {
    "rest": (("src/Honua.Server/EndpointRegistry*.cs",), re.compile(
        r'new\("(?:GET|HEAD|POST|PUT|PATCH|DELETE)",\s*"(/[^"]*)"')),
    "mcp": (("*.cs",), re.compile(r'(?:\bToolName|McpServerTool\(\s*Name)\s*=\s*"(honua_[a-z0-9_]+)"')),
    "console-route": (("*.razor",), re.compile(r'^@page\s+"(/[^"]*)"', re.M)),
}
PLURALS = ((re.compile(r"(?<=\w{2})ies$"), "y"), (re.compile(r"(?<=ss|sh|ch|.x)es$"), ""),
           (re.compile(r"(?<=\w{2}[^su])s$"), ""))


def singular(token):
    for pattern, replacement in PLURALS:
        if pattern.search(token):
            return pattern.sub(replacement, token)
    return token


def words(name):
    name = re.sub(r"\{\**(\w+)[^}]*\}", r" \1 ", name)
    name = re.sub(r"([a-z0-9])([A-Z])", r"\1 \2", name)
    name = re.sub(r"([A-Z]+)([A-Z][a-z])", r"\1 \2", name)
    return [singular(t) for t in re.sub(r"[^A-Za-z0-9]+", " ", name).lower().split()]


def find_rules(name, rules):
    tokens = words(name)
    hits = []
    for rule in rules:
        for term in rule["terms"]:
            term_words = words(term)
            span = len(term_words)
            if any(tokens[i:i + span] == term_words for i in range(len(tokens) - span + 1)):
                hits.append(rule["id"])
                break
    return hits


class Tree:
    """Tracked files of the working tree, or of a commit when rev is set."""

    def __init__(self, root, rev=None):
        self.root, self.rev = Path(root), rev

    def git(self, *args, stdin=None):
        return subprocess.run(["git", *args], cwd=self.root, input=stdin, check=True, capture_output=True).stdout

    def paths(self, patterns):
        if self.rev:
            listed = self.git("ls-tree", "-r", "-z", "--name-only", self.rev).decode().split("\0")
            listed = [p for p in listed if any(fnmatch.fnmatchcase(p, pattern) for pattern in patterns)]
        else:
            listed = self.git("ls-files", "-z", "--", *patterns).decode().split("\0")
        return [p for p in listed if p and not p.startswith(("tests/", "test/"))
                and "/tests/" not in p and ".test." not in p and ".spec." not in p]

    def texts(self, paths):
        if not self.rev:
            return [(self.root / p).read_text(encoding="utf-8", errors="replace") for p in paths
                    if (self.root / p).is_file()]
        out = self.git("cat-file", "--batch", stdin="".join(f"{self.rev}:{p}\n" for p in paths).encode())
        texts, at = [], 0
        while at < len(out):
            header_end = out.index(b"\n", at)
            header = out[at:header_end].split()
            at = header_end + 1
            if header[-1] == b"missing":
                continue
            size = int(header[2])
            texts.append(out[at:at + size].decode("utf-8", errors="replace"))
            at += size + 1
        return texts

    def json(self, path):
        found = self.texts([path])
        return json.loads(found[0]) if found else None


def surface_names(tree):
    names = set()
    for surface, (patterns, regex) in EXTRACTORS.items():
        for text in tree.texts(tree.paths(patterns)):
            names.update((surface, match) for match in regex.findall(text))
    roster = tree.json(MCP_ROSTER) or {}
    names.update(("mcp", tool) for key in ROSTER_LISTS for tool in roster.get(key, []))
    for stage in (tree.json(STAGE_MAP) or {}).get("stages", []):
        for surface, entry in stage["surfaces"].items():
            names.update((surface, binding["operation"]) for binding in entry["bindings"])
    return names


def key(entry):
    return entry["surface"], entry["name"], entry["rule"]


def exception_errors(exceptions, rules, seen):
    errors, keys = [], set()
    for entry in exceptions:
        label = f"exception {entry['surface']} {entry['name']!r} ({entry['rule']})"
        if key(entry) in keys:
            errors.append(f"duplicate {label}")
        keys.add(key(entry))
        if entry["rule"] not in rules:
            errors.append(f"{label}: unknown rule")
        elif entry.get("kind") not in KINDS or not entry.get("reason", "").strip():
            errors.append(f"{label}: needs a kind ({', '.join(KINDS)}) and a reason")
        elif key(entry) not in seen:
            errors.append(f"stale {label}: remove it")
    return errors


def check(tree, policy, exceptions):
    rules = {rule["id"]: rule for rule in policy["rules"]}
    allowed = {key(entry) for entry in exceptions}
    errors, seen = [], set()
    for surface, name in sorted(surface_names(tree)):
        for rule_id in find_rules(name, policy["rules"]):
            seen.add((surface, name, rule_id))
            if (surface, name, rule_id) not in allowed:
                errors.append(f"{surface} {name!r}: {rule_id}; name the {rules[rule_id]['object']}")
    return errors + exception_errors(exceptions, rules, seen)


def new_pending(base, exceptions):
    existing = surface_names(base)
    return [f"pending exception {e['surface']} {e['name']!r} ({e['rule']}) names a surface new since base: "
            "use the glossary term" for e in exceptions
            if e.get("kind") == "pending" and (e["surface"], e["name"]) not in existing]


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--root", type=Path, default=Path.cwd())
    parser.add_argument("--base", help="Refuse pending exceptions for surface names absent at this commit.")
    args = parser.parse_args(argv)
    root = Path(subprocess.run(["git", "rev-parse", "--show-toplevel"], cwd=args.root,
                               check=True, capture_output=True, text=True).stdout.strip())
    tree = Tree(root)
    policy = tree.json(POLICY) or json.loads(Path(__file__).with_name(Path(POLICY).name).read_text())
    exceptions = (tree.json(EXCEPTIONS) or {"exceptions": []})["exceptions"]
    errors = check(tree, policy, exceptions)
    if args.base:
        verified = subprocess.run(["git", "rev-parse", "--verify", "--quiet", f"{args.base}^{{commit}}"],
                                  cwd=root, capture_output=True, text=True)
        if verified.returncode:
            print(f"surface vocabulary: base {args.base!r} is not a commit here", file=sys.stderr)
            return 2
        errors += new_pending(Tree(root, verified.stdout.strip()), exceptions)
    for error in errors:
        print(error, file=sys.stderr)
    print(f"surface vocabulary: {len(errors)} finding(s)")
    return 1 if errors else 0


if __name__ == "__main__":
    sys.exit(main())
