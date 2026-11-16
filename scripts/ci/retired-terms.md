# Shared vocabulary check

The canonical policy is `retired-terms.v1.json` in this directory. Its `rules` contain literal checks for unambiguous retired domain names. Its `reviewRules` record contextual words that need human review because valid protocol or technical uses share the same spelling. This is a regression guard during the staged rename, not evidence that existing vocabulary debt has been removed.

Run from any repository using this script and policy from the same pinned honua-server commit:

```bash
python3 /path/to/honua-server/scripts/ci/check-retired-terms.py --root "$PWD" --base origin/trunk
```

In CI, fetch the base revision and pin the checker and adjacent policy together. Do not copy the policy into each repository or use a moving download URL. Each repository owns only its exact exceptions at `scripts/ci/retired-terms-allowlist.v1.json`. The adjacent checker tests run with Python's standard library:

```bash
python3 -m unittest discover -s scripts/ci/tests -p test_check_retired_terms.py
```

The default checks added lines in tracked committed, staged and working changes since the merge base of HEAD and the specified single revision. Stage new files before running. Deleted lines and unchanged existing debt do not fail the gate, including debt in a pure rename. A rename with edits checks its added lines at the destination; any exception must name that destination. `--root` may be any directory in the repository; paths in findings and exceptions are repository-relative. `--all` audits all tracked text when completing adoption.

An invalid base, malformed policy, invalid added text encoding or Git error fails the check; exit 0 is clear, 1 means findings, and 2 means the check could not complete. Binary files, symlinks and paths beneath symlink directories are skipped. Current UTF-8 text is checked even when its previous blob was binary or Git attributes mark it binary. Protected certification inputs are excluded before content-aware Git operations or reading file content. Exact safe entries are streamed with NUL separators into a temporary Git index, so selection is independent of command-line size. The comparison writes filtered tree objects to Git’s object database; it leaves the real index and working files unchanged and removes its temporary index afterward.

For an intentional protocol term, source-system transition aid, historical migration or test of old persisted state, add an exception with a literal relative `path`, policy `rule` ID, exact complete `line`, and nonempty `reason`. Wildcard paths are rejected. Changing the line invalidates the exception. Remove the old text when it is unnecessary; never assemble a retired name from fragments. The policy and exception files are declarations reviewed as policy data and are excluded from text scanning.

Review every contextual policy entry alongside the automated result. Do not add aliases for an unreleased client or change protocol-required payloads to satisfy the vocabulary gate. Model/API/SQL renames follow their approved migration boundary; do not sweep existing persisted spellings before that cutover.

## Surface names

`check-surface-vocabulary.py` checks whole names on public surfaces rather than added lines: the REST routes in the endpoint registry, MCP tool names (`docs/gis/data/mcp-tool-roster.v1.json` and tool-name constants), Blazor `@page` routes and the operations in `docs/guides/workflows/stage-map.v1.json`. It splits each name, including route parameter names, into singular words and fails when they contain another term for a glossary object (`surface-vocabulary.v1.json`). Exceptions in `surface-vocabulary-exceptions.v1.json` are exact (surface, name, rule) entries with a kind (`protocol`, `technical` or `pending`) and a reason. An entry whose name no longer exists fails, and `--base <commit>` refuses a pending entry for a name that does not exist at that commit, so a new name must use the glossary term. SDK method and CLI command names are covered only where the stage map records them; the SDK repositories keep their own retired-name checks. Other repositories run the pinned checker with `--root "$PWD"` and keep their own exceptions file. Run `python3 scripts/ci/check-surface-vocabulary.test.py` and `python3 scripts/ci/check-surface-vocabulary.py --base origin/trunk` from the repository root.
