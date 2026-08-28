#!/usr/bin/env python3
"""Offline tests for check-doc-links.py.

Three layers, none of which touches the network:

* **Port fidelity.** The slug algorithm, its strip set, the fenced-code
  exclusion, and the duplicate-heading suffixes come from `tools/check_links.py`
  in the sibling `geospatial-mcp` repository. That repository is not available
  here, so the compiled `SLUG_STRIP_RE` pattern is pinned literally: a
  transcription slip in either direction would make the two repositories grade
  the same heading differently, which is exactly the drift the port exists to
  prevent.
* **Synthetic docs trees** in temp directories, which exercise every arm the
  gate can take -- a good link, a dead file, a dead anchor, an allowlisted
  break, a *stale* allowlist entry, a manifest anchor that moved, a URL served
  only through a `.gitbook.yaml` redirect, a `pendingPr` entry before and after
  its branch lands, and an unregistered URL found by the completeness scan.
* **The live repository**, which asserts the gate is currently green and that
  the shipped `runbook_url` annotations really do resolve, so a later docs edit
  that moves one of those headings fails here as well as in CI.
"""

from __future__ import annotations

import contextlib
import importlib.util
import io
import json
import tempfile
from contextlib import redirect_stderr, redirect_stdout
from pathlib import Path

SCRIPT = Path(__file__).with_name("check-doc-links.py")
SPEC = importlib.util.spec_from_file_location("check_doc_links", SCRIPT)
assert SPEC and SPEC.loader
MODULE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(MODULE)

REPO_ROOT = Path(__file__).resolve().parents[2]

# Pinned byte for byte from geospatial-mcp `tools/check_links.py`. The two
# Unicode ranges are the general (U+2000-U+206F) and supplemental
# (U+2E00-U+2E7F) punctuation blocks github-slugger strips.
ORIGIN_SLUG_STRIP_PATTERN = (
    "[ -⁯⸀-⹿\\\\'!\"#$%&()*+,./:;<=>?@\\[\\]^`{|}~]"
)


def assert_that(condition: bool, message: str) -> None:
    if not condition:
        raise AssertionError(message)


def write(root: Path, relative: str, payload: str) -> None:
    path = root / relative
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(payload, encoding="utf-8")


def run(root: Path, *extra: str) -> tuple[int, str, str]:
    out, err = io.StringIO(), io.StringIO()
    with redirect_stdout(out), redirect_stderr(err):
        code = MODULE.main(["--repo-root", str(root), *extra])
    return code, out.getvalue(), err.getvalue()


def new_tree(stack, manifest: dict | None = None, allowlist: dict | None = None) -> Path:
    """A minimal repository: docs/ with a .gitbook.yaml, a manifest, an allowlist."""
    root = Path(stack.enter_context(tempfile.TemporaryDirectory()))
    write(root, "docs/.gitbook.yaml", "root: ./\n\nredirects:\n  operator/monitoring: guides/deploy/monitoring.md\n")
    write(root, "docs/guides/deploy/monitoring.md", "# Monitor\n\n## What to watch\n\nText.\n")
    write(
        root,
        "scripts/ci/code-referenced-anchors.v1.json",
        json.dumps(manifest if manifest is not None else {"docsBaseUrl": "https://docs.honua.io/", "references": []}),
    )
    write(
        root,
        "scripts/ci/doc-link-rot-allowlist.v1.json",
        json.dumps(allowlist if allowlist is not None else {"entries": []}),
    )
    return root


# --- port fidelity ---------------------------------------------------------


def test_slug_strip_set_matches_the_origin() -> None:
    assert_that(
        MODULE.SLUG_STRIP_RE.pattern == ORIGIN_SLUG_STRIP_PATTERN,
        "SLUG_STRIP_RE drifted from the geospatial-mcp original; the two repositories "
        "would grade the same heading differently",
    )


def test_slug_rules() -> None:
    cases = {
        "Redis is optional; PostGIS is not": "redis-is-optional-postgis-is-not",
        "Authentication: 401 and 403": "authentication-401-and-403",
        "Availability SLO / error budget": "availability-slo--error-budget",
        # An arrow is stripped, not replaced, so the surrounding spaces collapse
        # to a run of hyphens. Reproducing this exactly is why the strip set is
        # pinned rather than re-derived.
        "Promote L2 -> L1 criteria": "promote-l2---l1-criteria",
        "What needs Redis when multi-node": "what-needs-redis-when-multi-node",
        "<code>Escaped</code> HTML": "escaped-html",
        "Under_scores and 'quotes'": "under_scores-and-quotes",
    }
    for heading, expected in cases.items():
        actual = MODULE.slugify(heading)
        assert_that(actual == expected, f"slugify({heading!r}) == {actual!r}, expected {expected!r}")


def test_duplicate_headings_get_numeric_suffixes(stack) -> None:
    root = Path(stack.enter_context(tempfile.TemporaryDirectory()))
    write(root, "page.md", "# Tools\n\n## Tools\n\n### Tools\n")
    anchors = MODULE.heading_anchors(str(root / "page.md"))
    assert_that(anchors == {"tools", "tools-1", "tools-2"}, f"unexpected anchors: {sorted(anchors)}")


def test_headings_inside_fences_are_ignored(stack) -> None:
    root = Path(stack.enter_context(tempfile.TemporaryDirectory()))
    write(root, "page.md", "# Real\n\n```bash\n# Not a heading\n```\n\n~~~\n## Also not\n~~~\n\n## Second\n")
    anchors = MODULE.heading_anchors(str(root / "page.md"))
    assert_that(anchors == {"real", "second"}, f"fenced headings leaked: {sorted(anchors)}")


# --- relative links --------------------------------------------------------


def test_resolving_link_and_anchor_pass(stack) -> None:
    root = new_tree(stack)
    write(root, "docs/index.md", "See [watch](guides/deploy/monitoring.md#what-to-watch).\n")
    code, out, err = run(root)
    assert_that(code == 0, f"expected pass, got {code}: {err}")
    assert_that("gate passed" in out, out)


def test_missing_file_fails(stack) -> None:
    root = new_tree(stack)
    write(root, "docs/index.md", "See [gone](guides/deploy/nowhere.md).\n")
    code, _, err = run(root)
    assert_that(code == 1, "a missing target file must fail the gate")
    assert_that("file not found" in err, err)


def test_missing_anchor_fails(stack) -> None:
    root = new_tree(stack)
    write(root, "docs/index.md", "See [moved](guides/deploy/monitoring.md#what-to-watch-for).\n")
    code, _, err = run(root)
    assert_that(code == 1, "a dead anchor must fail the gate")
    assert_that("not a heading" in err, err)


def test_links_inside_fences_are_ignored(stack) -> None:
    root = new_tree(stack)
    write(
        root,
        "docs/index.md",
        "```md\n[example](guides/deploy/missing.md#not-real)\n```\n\n"
        "~~~markdown\n[other](also-missing.md)\n~~~\n",
    )
    code, _, err = run(root)
    assert_that(code == 0, f"fenced Markdown examples must not be scanned as links: {err}")


def test_relative_link_fragments_are_case_sensitive(stack) -> None:
    root = new_tree(stack)
    write(root, "docs/index.md", "See [watch](guides/deploy/monitoring.md#What-To-Watch).\n")
    code, _, err = run(root)
    assert_that(code == 1, "fragment matching must preserve case")
    assert_that("anchor '#What-To-Watch'" in err, err)


def test_external_and_scheme_links_are_skipped(stack) -> None:
    root = new_tree(stack)
    write(
        root,
        "docs/index.md",
        "[a](https://example.invalid/x#y) [b](mailto:x@y.invalid) "
        "[c](honua://dataset/x#y) [d](tel:+1) [e](data:text/plain,x)\n",
    )
    code, _, err = run(root)
    assert_that(code == 0, f"external schemes must be skipped: {err}")


def test_allowlisted_break_warns_and_stale_entry_fails(stack) -> None:
    entry = {"source": "docs/index.md", "target": "guides/deploy/monitoring.md#gone"}
    root = new_tree(stack, allowlist={"totals": {"entries": 1}, "entries": [entry]})
    write(root, "docs/index.md", "See [gone](guides/deploy/monitoring.md#gone).\n")
    code, _, err = run(root)
    assert_that(code == 0, f"an allowlisted break must warn, not fail: {err}")
    assert_that("allowlisted:" in err, err)

    # Ratchet: once the link is fixed, the allowlist entry itself is the error.
    write(root, "docs/index.md", "See [ok](guides/deploy/monitoring.md#what-to-watch).\n")
    code, _, err = run(root)
    assert_that(code == 1, "a stale allowlist entry must fail so the list can only shrink")
    assert_that("stale rot-allowlist entry" in err, err)


def test_allowlist_total_must_match(stack) -> None:
    root = new_tree(stack, allowlist={"totals": {"entries": 7}, "entries": []})
    write(root, "docs/index.md", "ok\n")
    code, _, err = run(root)
    assert_that(code == 1, "a miscounted allowlist must fail")
    assert_that("totals.entries" in err, err)


# --- the code-referenced-anchor manifest -----------------------------------


def manifest(*references, scan=None) -> dict:
    payload = {"docsBaseUrl": "https://docs.honua.io/", "references": list(references)}
    if scan is not None:
        payload["sourceScan"] = scan
    return payload


def test_manifest_url_resolves_to_file_and_heading(stack) -> None:
    root = new_tree(stack, manifest=manifest({"url": "https://docs.honua.io/guides/deploy/monitoring#what-to-watch"}))
    code, out, err = run(root, "--skip-links")
    assert_that(code == 0, f"expected pass: {err}")
    assert_that("1 manifest entries checked" in out, out)


def test_manifest_moved_heading_fails(stack) -> None:
    root = new_tree(stack, manifest=manifest({"url": "https://docs.honua.io/guides/deploy/monitoring#what-to-watch-for"}))
    code, _, err = run(root, "--skip-links")
    assert_that(code == 1, "a moved heading under a code-referenced URL must fail")
    assert_that("is not a heading" in err, err)


def test_manifest_fragment_is_case_sensitive(stack) -> None:
    root = new_tree(stack, manifest=manifest({"url": "https://docs.honua.io/guides/deploy/monitoring#What-To-Watch"}))
    code, _, err = run(root, "--skip-links")
    assert_that(code == 1, "manifest fragment matching must preserve case")
    assert_that("anchor '#What-To-Watch'" in err, err)


def test_manifest_missing_page_fails(stack) -> None:
    root = new_tree(stack, manifest=manifest({"url": "https://docs.honua.io/operations/runbook#emergency-procedures"}))
    code, _, err = run(root, "--skip-links")
    assert_that(code == 1, "a URL with no page behind it must fail")
    assert_that("does not resolve to a file" in err, err)


def test_manifest_readme_directory_url_resolves(stack) -> None:
    root = new_tree(stack, manifest=manifest({"url": "https://docs.honua.io/guides/operate"}))
    write(root, "docs/guides/operate/README.md", "# Operating\n")
    code, _, err = run(root, "--skip-links")
    assert_that(code == 0, f"a bare directory URL must resolve to its README.md: {err}")


def test_manifest_redirect_only_url_warns(stack) -> None:
    root = new_tree(stack, manifest=manifest({"url": "https://docs.honua.io/operator/monitoring#what-to-watch"}))
    code, _, err = run(root, "--skip-links")
    assert_that(code == 0, f"a redirect-served URL must warn, not fail: {err}")
    assert_that("resolves only through the .gitbook.yaml redirect" in err, err)
    assert_that("guides/deploy/monitoring.md" in err, "the warning must name the redirect target")


def test_pending_pr_entry_warns_then_fails_when_stale(stack) -> None:
    url = "https://docs.honua.io/guides/deploy/monitoring#redis-is-optional-postgis-is-not"
    root = new_tree(stack, manifest=manifest({"url": url, "pendingPr": 3583}))
    code, _, err = run(root, "--skip-links")
    assert_that(code == 0, f"a pendingPr heading must warn while its branch is open: {err}")
    assert_that("open PR #3583" in err, err)

    # Once the branch lands the heading appears; the stale marker becomes an
    # error so it must be removed before the gate passes.
    write(root, "docs/guides/deploy/monitoring.md", "# Monitor\n\n## What to watch\n\n## Redis is optional; PostGIS is not\n")
    code, out, err = run(root, "--skip-links")
    assert_that(code == 1, "a resolved pendingPr marker must fail until removed")
    assert_that("appears to have landed" in err, err)


def test_unregistered_url_in_source_fails(stack) -> None:
    scan = {"roots": ["src"], "extensions": [".cs"], "excludeDirs": ["bin", "obj"]}
    root = new_tree(stack, manifest=manifest(scan=scan))
    write(root, "src/Thing.cs", 'const string Ref = "https://docs.honua.io/guides/deploy/monitoring#what-to-watch";\n')
    code, _, err = run(root, "--skip-links")
    assert_that(code == 1, "an unregistered code-referenced URL must fail")
    assert_that("not listed in the code-referenced-anchor manifest" in err, err)
    assert_that("src/Thing.cs" in err, "the error must name the source file")

    root = new_tree(
        stack,
        manifest=manifest({"url": "https://docs.honua.io/guides/deploy/monitoring#what-to-watch"}, scan=scan),
    )
    write(root, "src/Thing.cs", 'const string Ref = "https://docs.honua.io/guides/deploy/monitoring#what-to-watch";\n')
    code, _, err = run(root, "--skip-links")
    assert_that(code == 0, f"a registered URL must pass: {err}")


def test_duplicate_manifest_entry_fails(stack) -> None:
    url = "https://docs.honua.io/guides/deploy/monitoring#what-to-watch"
    root = new_tree(stack, manifest=manifest({"url": url}, {"url": url}))
    code, _, err = run(root, "--skip-links")
    assert_that(code == 1, "a duplicated manifest entry must fail")
    assert_that("more than once" in err, err)


def test_gitbook_redirects_parse(stack) -> None:
    root = new_tree(stack)
    redirects = MODULE.parse_gitbook_redirects(str(root / "docs/.gitbook.yaml"))
    assert_that(redirects == {"operator/monitoring": "guides/deploy/monitoring.md"}, f"unexpected: {redirects}")


# --- the live repository ---------------------------------------------------


def test_live_repository_is_green() -> None:
    code, out, err = run(REPO_ROOT)
    assert_that(code == 0, f"the live documentation link/anchor gate must be green:\n{err}")
    assert_that("gate passed" in out, out)


def test_live_runbook_annotations_all_resolve() -> None:
    """Every shipped `runbook_url` must be a manifest entry that resolves.

    The 19 annotations in `docs/guides/deploy/examples/prometheus-alerts.yml`
    pointed at `/operations/runbook#...` for as long as they existed; that page
    has never been in this repository. This pins the retarget so the next docs
    restructure cannot quietly recreate the same hole.
    """
    alerts = (REPO_ROOT / "docs/guides/deploy/examples/prometheus-alerts.yml").read_text(encoding="utf-8")
    urls = [line.split('"')[1] for line in alerts.splitlines() if "runbook_url:" in line]
    assert_that(len(urls) == 19, f"expected 19 runbook_url annotations, found {len(urls)}")
    assert_that(
        not any("/operations/runbook" in url for url in urls),
        "docs.honua.io/operations/ does not exist; no runbook_url may point at it",
    )

    payload = json.loads((REPO_ROOT / MODULE.DEFAULT_MANIFEST).read_text(encoding="utf-8"))
    listed = {entry["url"] for entry in payload["references"]}
    for url in sorted(set(urls)):
        assert_that(url in listed, f"runbook_url {url} is not registered in the manifest")


def main() -> int:
    cases = [value for name, value in sorted(globals().items()) if name.startswith("test_")]
    for case in cases:
        with contextlib.ExitStack() as stack:
            if case.__code__.co_argcount:
                case(stack)
            else:
                case()
        print(f"ok - {case.__name__}")
    print(f"\n{len(cases)} passed")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
