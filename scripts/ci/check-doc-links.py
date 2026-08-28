#!/usr/bin/env python3
"""Documentation link and heading-anchor gate for `docs/`.

Ported from `tools/check_links.py` in the sibling `geospatial-mcp` repository
(https://github.com/honua-io/geospatial-mcp). The **anchor** rules are that
checker's, verbatim, so the two repositories grade heading drift identically:
the slug algorithm, the duplicate-heading `-1`/`-2` suffixing, the exclusion of
headings inside fenced code, and the literal Unicode general/supplemental
punctuation ranges in `SLUG_STRIP_RE`, whose compiled pattern
`check-doc-links.test.py` pins against the origin's byte for byte.

Two deliberate departures from the origin, both proved by named cases in the
companion test:

* **Links inside fenced code are not scanned.** The origin scans a document's
  raw text, so a Markdown example inside a ```` ```md ```` block is graded as a
  real link. `docs/` here contains such examples, so link extraction skips
  fences the same way heading extraction already did.
* **Fragment matching is case-sensitive.** The origin lowercases the fragment
  before comparing it against the (already lowercase) slugs, which quietly
  passes `#What-To-Watch`. GitHub emits only the lowercase anchor and does not
  scroll for the mixed-case spelling, so a link that reads as working but is not
  should fail the gate.

Three checks run, in order:

1.  **Relative links** across `docs/**/*.md`. For every Markdown link
    `[text](target)` whose target is relative (external `http(s)`/`mailto`, the
    `honua://` resource scheme, `tel:` and data URIs are intentionally skipped)
    the target file must resolve on disk, and when the link carries a
    `#fragment` that fragment must resolve to a real heading anchor in the target
    file (or in the same file for a pure `#anchor` link). Known pre-existing
    breakage is carried in the rot allowlist (see `--allowlist`) as a warning
    with a ratchet: an allowlist entry that no longer matches a real break is an
    error, so the debt can only shrink.

2.  **The code-referenced-anchor manifest** (see `--manifest`). Product code and
    shipped config embed absolute `https://docs.honua.io/...` URLs — the
    `remediationRef` on a typed capability refusal, a SCIM `documentationUri`, a
    Prometheus `runbook_url`. Those are contracts with an operator or an agent,
    not prose, and nothing else notices when a heading is renamed underneath
    them. Every manifest entry is translated back to a `docs/` file and, when it
    carries a fragment, to a heading in that file. A URL that resolves only
    through a `.gitbook.yaml` redirect is a warning naming the redirect: it
    still serves, but the reference is one restructure away from dead.

3.  **Manifest completeness.** The scan roots declared in the manifest are swept
    for `https://docs.honua.io/` URLs; any URL found in code or config that the
    manifest does not list is an error. This is what keeps check 2 honest as new
    references are added.

An entry may carry `"pendingPr": <number>` for a heading that exists only on an
open pull request. It degrades to a warning until that branch lands, so trunk
stays green today. Once the heading appears the marker is an ERROR until it is
deleted — a warn-forever escape hatch is how a reference stops being enforced
for good, so removing the marker is part of landing the branch it names.

Usage:
    python3 scripts/ci/check-doc-links.py [--repo-root DIR] [--docs-root DIR]
                                          [--manifest FILE] [--allowlist FILE]
                                          [--skip-manifest] [--skip-links]
Exit code 0 = every checked link, anchor and manifest reference resolves.
"""
import argparse
import json
import os
import re
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.abspath(os.path.join(HERE, "..", ".."))

DEFAULT_DOCS_ROOT = "docs"
DEFAULT_MANIFEST = "scripts/ci/code-referenced-anchors.v1.json"
DEFAULT_ALLOWLIST = "scripts/ci/doc-link-rot-allowlist.v1.json"
GITBOOK_CONFIG = ".gitbook.yaml"

# Markdown inline link target: the (...) part of [text](target).
LINK_RE = re.compile(r"\]\(([^)]+)\)")
ATX_HEADING_RE = re.compile(r"^(#{1,6})\s+(.*?)\s*#*\s*$")
FENCE_RE = re.compile(r"^\s*(```+|~~~+)")
HTML_TAG_RE = re.compile(r"<[^>]+>")
# Punctuation github-slugger strips outright: the two Unicode general/supplemental
# punctuation blocks plus an explicit ASCII set. Space, hyphen, underscore and
# alphanumerics are preserved; whitespace is converted to hyphens separately.
SLUG_STRIP_RE = re.compile(
    "[ -⁯⸀-⹿\\\\'!\"#$%&()*+,./:;<=>?@\\[\\]^`{|}~]")

SKIP_PREFIXES = ("http://", "https://", "mailto:", "honua://", "tel:", "data:")

DOCS_URL_RE = re.compile(r"https://docs\.honua\.io/[^\s\"'`<>)\\]*")
# Trailing punctuation that belongs to the surrounding prose or code, not the URL.
URL_TRAILING_TRIM = ".,;:!?"


def slugify(text):
    """Replicate GitHub's heading-anchor slug algorithm."""
    text = HTML_TAG_RE.sub("", text)
    text = text.strip().lower()
    text = SLUG_STRIP_RE.sub("", text)
    text = re.sub(r"\s", "-", text)
    return text


def heading_anchors(path):
    """Return the set of anchor slugs for all ATX headings in a Markdown file."""
    anchors = set()
    seen = {}
    in_fence = False
    fence_marker = None
    try:
        with open(path, "r", encoding="utf-8") as fh:
            lines = fh.readlines()
    except OSError:
        return anchors
    for line in lines:
        fence = FENCE_RE.match(line)
        if fence:
            marker = fence.group(1)[0]
            if not in_fence:
                in_fence, fence_marker = True, marker
            elif marker == fence_marker:
                in_fence, fence_marker = False, None
            continue
        if in_fence:
            continue
        m = ATX_HEADING_RE.match(line)
        if not m:
            continue
        base = slugify(m.group(2))
        if base == "":
            continue
        n = seen.get(base, 0)
        anchors.add(base if n == 0 else f"{base}-{n}")
        seen[base] = n + 1
    return anchors


def markdown_outside_fences(text):
    """Return Markdown with fenced code blocks removed from link scanning."""
    lines = []
    in_fence = False
    fence_marker = None
    for line in text.splitlines(keepends=True):
        fence = FENCE_RE.match(line)
        if fence:
            marker = fence.group(1)[0]
            if not in_fence:
                in_fence, fence_marker = True, marker
            elif marker == fence_marker:
                in_fence, fence_marker = False, None
            continue
        if not in_fence:
            lines.append(line)
    return "".join(lines)


def iter_md_files(roots):
    for root in roots:
        if os.path.isfile(root):
            yield root
            continue
        for dirpath, dirs, files in os.walk(root):
            dirs[:] = [d for d in dirs if d != ".git"]
            for name in sorted(files):
                if name.endswith(".md"):
                    yield os.path.join(dirpath, name)


def rel(path, repo_root):
    return os.path.relpath(path, repo_root).replace(os.sep, "/")


def load_json(path, what):
    try:
        with open(path, "r", encoding="utf-8") as fh:
            return json.load(fh)
    except OSError as exc:
        raise SystemExit(f"error: cannot read the {what} at {path}: {exc}")
    except ValueError as exc:
        raise SystemExit(f"error: the {what} at {path} is not valid JSON: {exc}")


def parse_gitbook_redirects(path):
    """Read the flat `redirects:` map out of `.gitbook.yaml` without a YAML parser.

    The block is a single level of `old/url/path: current/file.md` pairs with
    comments interleaved; a full YAML dependency would be the only third-party
    import in this script, so it is parsed directly. A structural surprise (a
    nested key, a list) is reported rather than silently ignored: a missed
    redirect turns a warning into a false error.
    """
    redirects = {}
    if not os.path.exists(path):
        return redirects
    in_block = False
    with open(path, "r", encoding="utf-8") as fh:
        for lineno, line in enumerate(fh, 1):
            stripped = line.strip()
            if not stripped or stripped.startswith("#"):
                continue
            indent = len(line) - len(line.lstrip(" "))
            if indent == 0:
                in_block = stripped.rstrip() == "redirects:"
                continue
            if not in_block:
                continue
            if stripped.startswith("-") or ":" not in stripped:
                raise SystemExit(
                    f"error: unexpected structure in {path}:{lineno} — the "
                    f"redirects block is expected to be a flat key: value map")
            key, _, value = stripped.partition(":")
            redirects[key.strip()] = value.strip().strip("'\"")
    return redirects


def url_to_doc_path(url_path, docs_root):
    """Translate a docs.honua.io URL path to a file under docs/, or None.

    GitBook serves `guides/deploy/troubleshooting.md` at `/guides/deploy/
    troubleshooting` and a directory's `README.md` at the bare directory path,
    so both spellings are tried.
    """
    url_path = url_path.strip("/")
    if not url_path:
        candidates = ["README.md"]
    elif url_path.endswith(".md"):
        candidates = [url_path]
    else:
        candidates = [url_path + ".md", url_path + "/README.md"]
    for candidate in candidates:
        resolved = os.path.join(docs_root, candidate)
        if os.path.isfile(resolved):
            return resolved
    return None


def split_docs_url(url, base_url):
    """Return (url_path, fragment) for a docs URL, or None if it is off-base."""
    if not url.startswith(base_url):
        return None
    remainder = url[len(base_url):]
    path_part, _, fragment = remainder.partition("#")
    path_part = path_part.split("?", 1)[0]
    return path_part, fragment


def scan_for_docs_urls(repo_root, scan, base_url):
    """Collect every docs.honua.io URL embedded in the declared scan roots."""
    found = {}
    extensions = tuple(scan.get("extensions", []))
    exclude_dirs = set(scan.get("excludeDirs", []))
    for root_rel in scan.get("roots", []):
        root = os.path.join(repo_root, root_rel)
        if os.path.isfile(root):
            walk = [(os.path.dirname(root), [], [os.path.basename(root)])]
        elif os.path.isdir(root):
            walk = os.walk(root)
        else:
            continue
        for dirpath, dirs, files in walk:
            dirs[:] = [d for d in dirs if d not in exclude_dirs]
            for name in files:
                if extensions and not name.endswith(extensions):
                    continue
                path = os.path.join(dirpath, name)
                try:
                    with open(path, "r", encoding="utf-8") as fh:
                        text = fh.read()
                except (OSError, UnicodeDecodeError):
                    continue
                if base_url not in text:
                    continue
                for raw in DOCS_URL_RE.findall(text):
                    url = raw.rstrip(URL_TRAILING_TRIM)
                    found.setdefault(url, set()).add(rel(path, repo_root))
    return found


def check_relative_links(docs_root, repo_root, allowlist_entries):
    """Port of the geospatial-mcp relative-link/anchor check, with the ratchet."""
    anchor_cache = {}
    errors = []
    warnings = []
    matched_allowlist = set()
    checked = 0

    for md in iter_md_files([docs_root]):
        source = rel(md, repo_root)
        base_dir = os.path.dirname(md)
        with open(md, "r", encoding="utf-8") as fh:
            text = fh.read()
        for raw in LINK_RE.findall(markdown_outside_fences(text)):
            target = raw.strip()
            # Drop an optional link title:  [t](path "title")
            if not target.startswith("#") and " " in target:
                target = target.split(" ", 1)[0]
            if not target or target.startswith(SKIP_PREFIXES):
                continue

            path_part, _, fragment = target.partition("#")
            path_part = path_part.split("?", 1)[0]

            key = (source, target)

            if path_part == "":
                resolved = md  # same-file anchor
            else:
                resolved = os.path.normpath(os.path.join(base_dir, path_part))
                if not os.path.exists(resolved):
                    detail = f"{source} -> {target} (file not found)"
                    if key in allowlist_entries:
                        matched_allowlist.add(key)
                        warnings.append(f"allowlisted: {detail}")
                    else:
                        errors.append(detail)
                    continue

            checked += 1
            if fragment and resolved.endswith(".md"):
                if resolved not in anchor_cache:
                    anchor_cache[resolved] = heading_anchors(resolved)
                if fragment not in anchor_cache[resolved]:
                    detail = (f"{source} -> {target} (anchor '#{fragment}' not a "
                              f"heading in {rel(resolved, repo_root)})")
                    if key in allowlist_entries:
                        matched_allowlist.add(key)
                        warnings.append(f"allowlisted: {detail}")
                    else:
                        errors.append(detail)

    stale = sorted(set(allowlist_entries) - matched_allowlist)
    for source, target in stale:
        errors.append(
            f"stale rot-allowlist entry: {source} -> {target} now resolves (or "
            f"the link is gone). Delete it from the allowlist; this list only "
            f"shrinks.")

    return checked, errors, warnings


def check_manifest(manifest, repo_root, docs_root, redirects):
    """Verify every code-referenced docs URL maps to a real file and heading."""
    errors = []
    warnings = []
    notes = []
    anchor_cache = {}
    base_url = manifest.get("docsBaseUrl", "https://docs.honua.io/")

    listed = {}
    for entry in manifest.get("references", []):
        url = entry.get("url")
        if not url:
            errors.append("manifest entry is missing a 'url'")
            continue
        if url in listed:
            errors.append(f"manifest lists {url} more than once")
        listed[url] = entry
        pending_pr = entry.get("pendingPr")
        pending = (f" (expected only on open PR #{pending_pr})"
                   if pending_pr else "")

        split = split_docs_url(url, base_url)
        if split is None:
            errors.append(f"manifest entry {url} is not under {base_url}")
            continue
        url_path, fragment = split

        resolved = url_to_doc_path(url_path, docs_root)
        via_redirect = None
        if resolved is None:
            redirect_target = redirects.get(url_path.strip("/"))
            if redirect_target:
                resolved = url_to_doc_path(redirect_target, docs_root)
                via_redirect = redirect_target
        if resolved is None:
            message = (f"{url} does not resolve to a file under "
                       f"{rel(docs_root, repo_root)}/ (tried "
                       f"'{url_path}.md', '{url_path}/README.md', and the "
                       f".gitbook.yaml redirects)")
            (warnings if pending_pr else errors).append(message + pending)
            continue
        if via_redirect:
            warnings.append(
                f"{url} resolves only through the .gitbook.yaml redirect "
                f"'{url_path.strip('/')}: {via_redirect}'. The reference works "
                f"today but names a path the docs tree no longer has; point it "
                f"at {via_redirect} instead.")

        if fragment:
            if resolved not in anchor_cache:
                anchor_cache[resolved] = heading_anchors(resolved)
            if fragment not in anchor_cache[resolved]:
                message = (f"{url} -> anchor '#{fragment}' is not a heading in "
                           f"{rel(resolved, repo_root)}")
                (warnings if pending_pr else errors).append(message + pending)
                continue

        if pending_pr:
            errors.append(
                f"{url} now resolves; PR #{pending_pr} appears to have landed, "
                f"so drop its 'pendingPr' marker to enforce the reference.")

    return listed, errors, warnings, notes


def check_manifest_completeness(manifest, listed, repo_root):
    """Every docs.honua.io URL embedded in the scan roots must be registered."""
    errors = []
    scan = manifest.get("sourceScan")
    if not scan:
        return errors, 0
    base_url = manifest.get("docsBaseUrl", "https://docs.honua.io/")
    found = scan_for_docs_urls(repo_root, scan, base_url)
    for url in sorted(found):
        if url in listed:
            continue
        sources = ", ".join(sorted(found[url]))
        errors.append(
            f"{url} is referenced from {sources} but is not listed in the "
            f"code-referenced-anchor manifest. Add it (with its sources) so a "
            f"renamed heading cannot break it silently.")
    return errors, len(found)


def main(argv):
    parser = argparse.ArgumentParser(
        description="Validate docs/ relative links, heading anchors, and the "
                    "code-referenced docs.honua.io anchor manifest.")
    parser.add_argument("--repo-root", default=REPO)
    parser.add_argument("--docs-root", default=None,
                        help=f"default: <repo-root>/{DEFAULT_DOCS_ROOT}")
    parser.add_argument("--manifest", default=None,
                        help=f"default: <repo-root>/{DEFAULT_MANIFEST}")
    parser.add_argument("--allowlist", default=None,
                        help=f"default: <repo-root>/{DEFAULT_ALLOWLIST}")
    parser.add_argument("--skip-links", action="store_true",
                        help="skip the docs/**/*.md relative-link sweep")
    parser.add_argument("--skip-manifest", action="store_true",
                        help="skip the code-referenced-anchor manifest checks")
    args = parser.parse_args(argv)

    repo_root = os.path.abspath(args.repo_root)
    docs_root = os.path.abspath(
        args.docs_root or os.path.join(repo_root, DEFAULT_DOCS_ROOT))
    manifest_path = os.path.abspath(
        args.manifest or os.path.join(repo_root, DEFAULT_MANIFEST))
    allowlist_path = os.path.abspath(
        args.allowlist or os.path.join(repo_root, DEFAULT_ALLOWLIST))

    if not os.path.isdir(docs_root):
        raise SystemExit(f"error: docs root {docs_root} does not exist")

    errors = []
    warnings = []
    notes = []

    if not args.skip_links:
        allowlist = load_json(allowlist_path, "documentation link rot allowlist")
        allowlist_entries = {
            (item["source"], item["target"]) for item in allowlist.get("entries", [])
        }
        declared = allowlist.get("totals", {}).get("entries")
        if declared is not None and declared != len(allowlist.get("entries", [])):
            errors.append(
                f"rot allowlist totals.entries says {declared} but the file "
                f"lists {len(allowlist.get('entries', []))}")
        checked, link_errors, link_warnings = check_relative_links(
            docs_root, repo_root, allowlist_entries)
        errors.extend(link_errors)
        warnings.extend(link_warnings)
        print(f"Relative links: {checked} resolved targets checked across "
              f"{rel(docs_root, repo_root)}/**/*.md "
              f"({len(allowlist_entries)} allowlisted known breaks).")

    if not args.skip_manifest:
        manifest = load_json(manifest_path, "code-referenced-anchor manifest")
        redirects = parse_gitbook_redirects(
            os.path.join(docs_root, GITBOOK_CONFIG))
        listed, m_errors, m_warnings, m_notes = check_manifest(
            manifest, repo_root, docs_root, redirects)
        errors.extend(m_errors)
        warnings.extend(m_warnings)
        notes.extend(m_notes)
        c_errors, scanned = check_manifest_completeness(
            manifest, listed, repo_root)
        errors.extend(c_errors)
        print(f"Code-referenced anchors: {len(listed)} manifest entries checked "
              f"against {rel(docs_root, repo_root)}/ "
              f"({len(redirects)} .gitbook.yaml redirects known, "
              f"{scanned} distinct URLs found in the scanned source roots).")

    for note in notes:
        print("NOTE:", note)
    for warning in sorted(set(warnings)):
        print("WARN:", warning, file=sys.stderr)
    if errors:
        for error in sorted(set(errors)):
            print("ERROR:", error, file=sys.stderr)
        print(f"Documentation link/anchor gate FAILED "
              f"({len(set(errors))} error(s), {len(set(warnings))} warning(s)).",
              file=sys.stderr)
        return 1
    print(f"Documentation link/anchor gate passed "
          f"({len(set(warnings))} warning(s)).")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
