#!/usr/bin/env python3
"""Check the curated stage vocabulary and its source-linked surface bindings."""
from __future__ import annotations

import argparse
import json
import re
from pathlib import Path

from okf_frontmatter import split_document

REPO = Path(__file__).resolve().parents[2]
MAP = "docs/guides/workflows/stage-map.v1.json"
POLICY = "scripts/ci/retired-terms.v1.json"
SURFACES = ("rest", "sdk", "cli", "mcp", "skill")
KINDS = {"http": "rest", "sdk": "sdk", "cli": "cli", "mcp": "mcp"}
# Workflow words the glossary rejects that are not unambiguous policy rules.
WORKFLOW_TERMS = ("composition", "published layer", "portal item", "web map")


def read_metadata(root, page):
    return split_document((root / "docs" / page.lstrip("/")).read_text())[0]


def stages(root):
    result = {}
    for path in sorted((root / "docs/guides/workflows").glob("*.md")):
        metadata, _ = split_document(path.read_text())
        for action in (metadata or {}).get("workflow", {}).get("actions", []):
            page = "/" + path.relative_to(root / "docs").as_posix()
            result[f"{page}#{action['id']}"] = action
    return result


def concepts(action):
    return sorted({item["concept"] for direction in ("inputs", "outputs")
                   for item in action.get(direction, [])})


def canonical_terms(root):
    glossary = (root / "docs/concepts/domain/README.md").read_text()
    terms = {}
    for label, page in re.findall(r"^\| \[([^\]]+)\]\(([^)]+\.md)\) \|", glossary, re.M):
        if "internal only" not in label:
            terms.setdefault("/concepts/domain/" + page, []).append(label)
    return terms


def spaced(text):
    text = re.sub(r"([a-z0-9])([A-Z])", r"\1 \2", text)
    text = re.sub(r"([A-Z]+)([A-Z][a-z])", r"\1 \2", text)
    return re.sub(r"[_\-./]+", " ", text).lower()


def policy_literals(root):
    path = root / POLICY
    if not path.is_file():
        path = REPO / POLICY
    document = json.loads(path.read_text())
    rules = [item["literal"] for item in document["rules"]]
    review = [item["literal"] for item in document["reviewRules"]]
    return rules, review


def contains_literal(text, literals):
    haystack = spaced(text)
    for literal in literals:
        parts = [re.escape(part) + r"s?" for part in spaced(literal).split()]
        if re.search(r"\b" + r"\s+".join(parts) + r"\b", haystack):
            return True
    return False


def source_file(root, source):
    # Only repository-relative files: a map cannot make CI fetch arbitrary URLs.
    if Path(source).is_absolute():
        raise ValueError(f"source must be repository-relative: {source}")
    path = (root / source).resolve()
    if not path.is_relative_to(root.resolve()) or not path.is_file():
        raise ValueError(f"missing or outside-repository source: {source}")
    return path


def check_binding(root, surface, binding, rules, review):
    path = source_file(root, binding["source"])
    operation = binding["operation"]
    if surface == "rest":
        document = json.loads(path.read_text())
        method, route = operation.split(" ", 1)
        if method.lower() not in document.get("paths", {}).get(route, {}):
            raise ValueError(f"REST operation absent from contract: {operation}")
    elif surface == "skill":
        metadata, body = split_document(path.read_text())
        if (metadata or {}).get("name") != operation:
            raise ValueError(f"skill name differs from source: {operation}")
        if binding["evidence"] not in body:
            raise ValueError(f"skill evidence absent from source: {operation}")
    elif not re.search(r"(?<![\w])" + re.escape(operation) + r"(?![\w])", path.read_text()):
        raise ValueError(f"{surface} operation absent from source: {operation}")
    # Existing wire spellings can differ from the canonical semantic verb.
    # Require an explicit source-backed exception rather than silently hiding it.
    # Review-rule words are contextual, so they bind callable spellings only.
    # OKF verbs keep the action's own name; a protocol spelling records why.
    retired = rules + list(WORKFLOW_TERMS) + review
    if contains_literal(operation, retired) and not binding.get("literalReason"):
        raise ValueError(f"retired operation spelling needs literalReason: {operation}")


def check_surfaces(root, row, rules, review):
    if set(row["surfaces"]) != set(SURFACES):
        raise ValueError("every REST/SDK/CLI/MCP/skill surface must be recorded")
    for surface, entry in row["surfaces"].items():
        bindings = entry["bindings"]
        if bool(bindings) == bool(entry.get("gap")):
            raise ValueError(f"{surface} needs bindings or an explicit gap, exclusively")
        for binding in bindings:
            if binding.get("verb") != row["verb"]:
                raise ValueError(f"{surface} verb differs from stage verb")
            check_binding(root, surface, binding, rules, review)

def check_row(root, row, action, rules, review):
    verb_terms = rules + list(WORKFLOW_TERMS)
    if row["verb"] != action["verb"] or contains_literal(row["verb"], verb_terms):
        raise ValueError("stage verb differs from OKF action or uses a retired term")
    if sorted(item["concept"] for item in row["objects"]) != concepts(action):
        raise ValueError("objects differ from OKF action inputs/outputs")
    for item in row["objects"]:
        terms = canonical_terms(root).get(item["concept"])
        if item["terms"] != terms or any(contains_literal(term, verb_terms) for term in item["terms"]):
            raise ValueError(f"object term differs from canonical concept: {item['concept']}")
    check_surfaces(root, row, rules, review)
    for interface in action.get("interfaces", []):
        if interface["kind"] not in KINDS:
            continue
        surface = KINDS[interface["kind"]]
        operation = (interface["method"] + " " + interface["path"]
                     if surface == "rest" else interface["operation"])
        bindings = row["surfaces"][surface]["bindings"]
        source = "docs" + interface["contract"]
        if not any(b["operation"] == operation and b["source"] == source for b in bindings):
            raise ValueError(f"{surface} binding differs from OKF action: {operation}")


def check(root, document):
    expected = stages(root)
    rows = document["stages"]
    ids = [row["okf"] for row in rows]
    if document["schemaVersion"] != 1 or len(ids) != len(set(ids)) or set(ids) != set(expected):
        return ["map must contain exactly one row per OKF workflow action"]
    rules, review = policy_literals(root)
    errors = []
    for row in rows:
        try:
            check_row(root, row, expected[row["okf"]], rules, review)
        except (KeyError, TypeError, ValueError, OSError) as exc:
            errors.append(f"{row['okf']}: {exc}")
    return errors


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--gaps", action="store_true", help="list unrecorded bindings")
    args = parser.parse_args()
    document = json.loads((REPO / MAP).read_text())
    errors = check(REPO, document)
    for error in errors:
        print(f"::error::{error}")
    if errors:
        return 1
    print(f"Workflow stage map is current: {len(document['stages'])} stages.")
    if args.gaps:
        for row in document["stages"]:
            missing = [name for name, entry in row["surfaces"].items() if entry.get("gap")]
            if missing:
                print(f"{row['okf']}: {', '.join(missing)}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
