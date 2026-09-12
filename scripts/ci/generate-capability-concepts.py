#!/usr/bin/env python3
"""Emit one OKF `capability` concept per entry in the capability registry.

The platform already knows what it can do: `docs/gis/data/capability-keys.v1.json`
names 117 capabilities and `capability-matrix.v1.json` carries each one's
edition, category, maturity split and proving-test count. None of it is reachable
from the documentation bundle, because `docs/gis` is excluded from it — it is
generated evidence feeding a gate, not prose for a reader.

So an agent asking "what can this server do, and where is that documented?" has
to read 135 pages of prose and hope. This closes that: every registry entry
becomes a concept whose `resource` is its stable capability key and which carries
the registry's own facts.

Each page is a pure function of those two JSON files, deliberately. An earlier
draft embedded the pages that discuss each capability, computed by searching the
bundle - which made all 117 concepts depend on the prose of every page, so an
unrelated documentation edit restaled the lot. That view is a report now
(`--report`), not committed output.

This is the "derive the mechanical join, hand-maintain only the judgement" rule
ADR-0058 sets for the capability artifacts, applied to the documentation graph.
The judgement lives in the registry; the concepts are mechanical.

    python3 scripts/ci/generate-capability-concepts.py          # write
    python3 scripts/ci/generate-capability-concepts.py --check  # fail on drift
    python3 scripts/ci/generate-capability-concepts.py --report # prose coverage
"""
from __future__ import annotations

import argparse
import json
import pathlib
import re
import sys

REPO_ROOT = pathlib.Path(__file__).resolve().parents[2]
DATA = REPO_ROOT / "docs" / "gis" / "data"
KEYS_PATH = DATA / "capability-keys.v1.json"
MATRIX_PATH = DATA / "capability-matrix.v1.json"
OUT_DIR = REPO_ROOT / "docs" / "okf" / "capabilities"
BUNDLE_PATH = REPO_ROOT / "scripts" / "ci" / "okf-bundle.v1.json"

GENERATED = "<!-- GENERATED FILE - DO NOT EDIT. Regenerate with scripts/ci/generate-capability-concepts.py -->"


def load(path: pathlib.Path) -> dict:
    return json.loads(path.read_text(encoding="utf-8"))


def bundle_pages() -> list[pathlib.Path]:
    """Every page inside the documentation bundle, so edges only point at it."""
    manifest = load(BUNDLE_PATH)
    root = REPO_ROOT / manifest["root"]
    ex_dirs = {(REPO_ROOT / e["path"]).resolve() for e in manifest.get("excludedDirs", [])}
    ex_files = {(REPO_ROOT / e["path"]).resolve() for e in manifest.get("excludedFiles", [])}
    pages = []
    # Sort on the posix string, not the Path: comparing WindowsPath values is
    # case-insensitive, so the same tree orders differently on Windows and Linux.
    for path in sorted(root.rglob("*.md"), key=lambda p: p.as_posix()):
        resolved = path.resolve()
        if resolved in ex_files or any(p in ex_dirs for p in resolved.parents):
            continue
        if OUT_DIR.resolve() in resolved.parents:
            continue  # do not let generated concepts cite each other
        pages.append(path)
    return pages


def slugify(value: str) -> str:
    return re.sub(r"[^a-z0-9]+", "-", value.lower()).strip("-")


def citing_pages(key: str, display_name: str, pages: dict[pathlib.Path, str]) -> list[pathlib.Path]:
    """Pages that name this capability.

    The key is unambiguous; the display name is matched only as a whole phrase,
    so "Admin Control Plane" does not drag in every page that says "admin".
    """
    phrase = re.compile(r"\b" + re.escape(display_name) + r"\b", re.I)
    hits = []
    for path, text in pages.items():
        if key in text or phrase.search(text):
            hits.append(path)
    return hits


def render(entry: dict, facts: dict) -> str:
    key = entry["key"]
    title = entry.get("displayName") or key
    description = " ".join((entry.get("description") or "").split())
    if len(description) > 300:
        description = description[:297].rsplit(" ", 1)[0] + "…"

    tags = ["capability"]
    for field in ("category", "edition"):
        value = facts.get(field) or entry.get(field)
        if value:
            tags.append(slugify(value))

    lines = [
        "---",
        "type: capability",
        f'title: "{title}"',
    ]
    if description:
        lines.append(f'description: "{description}"')
    lines += [
        f'resource: "honua://capability/{key}"',
        f"tags: [{', '.join(tags)}]",
        "---",
        GENERATED,
        "",
        f"# {title}",
        "",
    ]
    if description:
        lines += [description, ""]

    lines += [
        "| | |",
        "| --- | --- |",
        f"| Capability key | `{key}` |",
    ]
    for label, value in (
        ("Category", facts.get("category") or entry.get("category")),
        ("Edition", facts.get("edition") or entry.get("edition")),
    ):
        if value:
            lines.append(f"| {label} | {value} |")

    maturity = facts.get("maturity") or {}
    if maturity:
        parts = ", ".join(f"{count} {state}" for state, count in sorted(maturity.items()) if count)
        if parts:
            lines.append(f"| Surface maturity | {parts} |")
    if facts.get("entryCount"):
        lines.append(f"| Registry entries | {facts['entryCount']} |")
    if facts.get("provingTestCount"):
        lines.append(f"| Proving tests | {facts['provingTestCount']} |")
    lines.append("")

    lines.append(
        "The facts above come from `docs/gis/data/capability-keys.v1.json` and "
        "`capability-matrix.v1.json`, both generated from the server's own registry and test "
        "evidence. This page is a pure function of those two files — nothing in it depends on "
        "what the prose happens to say, so an unrelated documentation edit cannot stale it."
    )
    lines.append("")
    lines.append(
        "Which pages discuss this capability is a question about the prose, so it is reported "
        "rather than baked in: run `scripts/ci/generate-capability-concepts.py --report`."
    )
    return "\n".join(lines).rstrip() + "\n"


def render_index(entries: list[tuple[str, str, str, str]]) -> str:
    lines = [
        "---",
        "type: index",
        'title: "Capability concepts"',
        'description: "One concept per entry in the server capability registry: what it is, '
        'which edition carries it, and how mature its surfaces are."',
        "tags: [capability, registry, generated]",
        "---",
        GENERATED,
        "",
        "# Capability concepts",
        "",
        "Every capability the server declares, as a concept an agent can traverse. Each page's",
        "`resource` is the stable capability key (`honua://capability/<key>`), which is the same",
        "identity the capability matrix, the licensing registry and the route mapping use — so an",
        "answer found here joins to the evidence without a name lookup.",
        "",
        "These are generated from the registry by `scripts/ci/generate-capability-concepts.py`,",
        "and they are a pure function of `docs/gis/data/capability-keys.v1.json` and",
        "`capability-matrix.v1.json`. Edit the registry, not these pages.",
        "",
        "The bundle contract these pages obey — frontmatter, concept types, what is generated —",
        "is described in [Open Knowledge Format](../README.md).",
        "",
        "Which prose pages discuss a capability is a question about the prose, so it is not baked",
        "in here — a page rewritten elsewhere would silently stale all 117 of these. Run",
        "`scripts/ci/generate-capability-concepts.py --report` for that view and for the",
        "capabilities no page in the bundle names yet.",
        "",
        "| Capability | Category | Edition |",
        "| --- | --- | --- |",
    ]
    for key, title, category, edition in entries:
        lines.append(f"| [{title}]({key}.md) | {category or '—'} | {edition or '—'} |")
    lines.append("")
    return "\n".join(lines).rstrip() + "\n"


def build() -> dict[str, str]:
    keys = load(KEYS_PATH)
    matrix = load(MATRIX_PATH)
    entries = keys["capabilities"] if isinstance(keys, dict) else keys
    facts_by_key = {c["key"]: c for c in matrix.get("capabilities", [])}

    written: dict[str, str] = {}
    index_rows = []
    for entry in sorted(entries, key=lambda e: e["key"]):
        key = entry["key"]
        facts = facts_by_key.get(key, {})
        written[f"{key}.md"] = render(entry, facts)
        index_rows.append((
            key,
            entry.get("displayName") or key,
            facts.get("category") or entry.get("category"),
            facts.get("edition") or entry.get("edition"),
        ))
    written["README.md"] = render_index(index_rows)
    return written


def report() -> int:
    """Print which capabilities the prose names, without committing the answer.

    Baking this into the pages would make every capability concept depend on
    every page in the bundle, so an unrelated documentation edit would restale
    all 117. As a report it stays useful and costs nothing.
    """
    keys = load(KEYS_PATH)
    entries = keys["capabilities"] if isinstance(keys, dict) else keys
    pages = {p: p.read_text(encoding="utf-8", errors="replace") for p in bundle_pages()}

    undocumented = []
    for entry in sorted(entries, key=lambda e: e["key"]):
        key = entry["key"]
        cites = citing_pages(key, entry.get("displayName") or key, pages)
        if cites:
            print(f"{key:<44} {len(cites)} page(s)")
        else:
            undocumented.append(key)
    print()
    print(
        f"{len(undocumented)} of {len(entries)} capabilities are named by no page inside the "
        "documentation bundle:"
    )
    for key in undocumented:
        print(f"  {key}")
    return 0


def main(argv: list[str]) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--check", action="store_true", help="fail if the concepts are stale")
    parser.add_argument(
        "--report",
        action="store_true",
        help="print which capabilities the prose names, and which none does",
    )
    args = parser.parse_args(argv)

    if args.report:
        return report()

    rendered = build()

    if args.check:
        existing = {p.name: p.read_text(encoding="utf-8") for p in OUT_DIR.glob("*.md")} if OUT_DIR.is_dir() else {}
        if existing != rendered:
            added = sorted(set(rendered) - set(existing))
            removed = sorted(set(existing) - set(rendered))
            changed = sorted(k for k in set(rendered) & set(existing) if rendered[k] != existing[k])
            print(
                "::error::docs/okf/capabilities is stale. Run "
                "'python3 scripts/ci/generate-capability-concepts.py' and commit the result.",
                file=sys.stderr,
            )
            for label, names in (("added", added), ("removed", removed), ("changed", changed)):
                if names:
                    print(f"  {label}: {', '.join(names[:8])}{' …' if len(names) > 8 else ''}", file=sys.stderr)
            return 1
        print(f"Capability concepts are current: {len(rendered)} page(s).")
        return 0

    OUT_DIR.mkdir(parents=True, exist_ok=True)
    for stale in OUT_DIR.glob("*.md"):
        if stale.name not in rendered:
            stale.unlink()
    for name, text in rendered.items():
        (OUT_DIR / name).write_text(text, encoding="utf-8", newline="\n")
    print(f"Wrote {len(rendered)} capability concept(s) to docs/okf/capabilities/.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
