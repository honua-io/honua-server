#!/usr/bin/env python3
"""Emit one OKF `capability` concept per entry in the capability registry.

The platform already knows what it can do: `docs/gis/data/capability-keys.v1.json`
names 117 capabilities and `capability-matrix.v1.json` carries each one's
edition, category, maturity split and proving-test count. None of it is reachable
from the documentation bundle, because `docs/gis` is excluded from it — it is
generated evidence feeding a gate, not prose for a reader.

So an agent asking "what can this server do, and where is that documented?" has
to read 135 pages of prose and hope. This closes that: every registry entry
becomes a concept whose `resource` is its stable capability key, carrying the
registry's own facts and linking to the pages that actually discuss it. The
edges are computed by searching the bundle for the key, never hand-written, so a
capability that no page documents shows that plainly instead of pretending.

This is the "derive the mechanical join, hand-maintain only the judgement" rule
ADR-0058 sets for the capability artifacts, applied to the documentation graph.
The judgement lives in the registry; the concepts are mechanical.

    python3 scripts/ci/generate-capability-concepts.py          # write
    python3 scripts/ci/generate-capability-concepts.py --check  # fail on drift
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
    for path in sorted(root.rglob("*.md")):
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


def render(entry: dict, facts: dict, cites: list[pathlib.Path]) -> str:
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

    lines.append("## Where this is documented")
    lines.append("")
    if cites:
        for path in cites:
            rel = path.relative_to(REPO_ROOT / "docs").as_posix()
            hops = "../" * (len(OUT_DIR.relative_to(REPO_ROOT / "docs").parts))
            title_line = path.read_text(encoding="utf-8", errors="replace")
            match = re.search(r'^title:\s*"?(.+?)"?\s*$', title_line, re.M)
            label = match.group(1) if match else rel
            lines.append(f"- [{label}]({hops}{rel})")
    else:
        lines.append(
            "No page inside the documentation bundle names this capability. That is a "
            "documentation gap, not a missing feature — the registry entry and its proving "
            "tests exist."
        )
    lines.append("")
    lines.append(
        "The facts above come from `docs/gis/data/capability-keys.v1.json` and "
        "`capability-matrix.v1.json`, which are generated from the server's own registry and "
        "test evidence. The links are computed by searching the bundle for the capability key "
        "or its name, so a page that stops discussing a capability stops appearing here."
    )
    return "\n".join(lines).rstrip() + "\n"


def render_index(entries: list[tuple[str, str, int]]) -> str:
    lines = [
        "---",
        "type: index",
        'title: "Capability concepts"',
        'description: "One concept per entry in the server capability registry: what it is, '
        'which edition carries it, how mature its surfaces are, and which documentation pages '
        'discuss it."',
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
        "These are generated from the registry by `scripts/ci/generate-capability-concepts.py`.",
        "Edit the registry, not these pages.",
        "",
        "| Capability | Edition | Documented on |",
        "| --- | --- | ---: |",
    ]
    for key, title, edition, cites in entries:
        lines.append(f"| [{title}]({key}.md) | {edition or '—'} | {cites} page(s) |")
    lines.append("")
    undocumented = [e for e in entries if e[3] == 0]
    if undocumented:
        lines.append(
            f"**{len(undocumented)} of {len(entries)} capabilities are named by no page in the "
            "bundle.** That is the documentation backlog, stated rather than inferred."
        )
        lines.append("")
    return "\n".join(lines).rstrip() + "\n"


def build() -> dict[str, str]:
    keys = load(KEYS_PATH)
    matrix = load(MATRIX_PATH)
    entries = keys["capabilities"] if isinstance(keys, dict) else keys
    facts_by_key = {c["key"]: c for c in matrix.get("capabilities", [])}

    pages = {p: p.read_text(encoding="utf-8", errors="replace") for p in bundle_pages()}

    written: dict[str, str] = {}
    index_rows = []
    for entry in sorted(entries, key=lambda e: e["key"]):
        key = entry["key"]
        facts = facts_by_key.get(key, {})
        cites = citing_pages(key, entry.get("displayName") or key, pages)
        written[f"{key}.md"] = render(entry, facts, cites)
        index_rows.append(
            (key, entry.get("displayName") or key, facts.get("edition") or entry.get("edition"), len(cites))
        )
    written["README.md"] = render_index(index_rows)
    return written


def main(argv: list[str]) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--check", action="store_true", help="fail if the concepts are stale")
    args = parser.parse_args(argv)

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
