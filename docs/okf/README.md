---
type: index
title: "Open Knowledge Format in honua-server"
description: "How this repository's documentation is shaped as an OKF v0.2 bundle: what the contract is, where it is defined, what is generated, and what CI checks."
tags: [okf, documentation, contributing]
---

# Open Knowledge Format in honua-server

This repository's documentation is an [Open Knowledge Format][okf] v0.2 bundle,
not just a folder of markdown. The contract is short:

> One markdown file is one concept; the file path is the concept's identity;
> relative markdown links are the graph edges. `type` is the only required
> frontmatter field.

That sentence is not paraphrased here for convenience — it is copied from
[`scripts/ci/okf-bundle.v1.json`](../../scripts/ci/okf-bundle.v1.json), which is
the **authoritative** definition. This page exists because that file lives in a
CI directory nobody browses, so the rules were effectively invisible to anyone
writing documentation. When the two disagree, the manifest is right.

## What the manifest already tells you

Read it directly rather than a summary of it. It carries, with a reason for each
entry:

- **`conceptTypes`** — the six `type` values and what each one means. A page
  whose `type` is not one of them fails the gate.
- **`excludedDirs` / `excludedFiles`** — what is deliberately *not* in the
  bundle, and why. `docs/internal`, `docs/archive` and `docs/gis` are out for
  three different reasons, and the reasons matter more than the list.
- **`includedFiles`** — the handful of pages re-included *over* an exclusion,
  because `SUMMARY.md` links them and anything in the table of contents is
  published documentation.
- **`rejectedFields`** — fields that must never appear. Today that is
  `timestamp`, which is not an OKF field in any version; the trust family spells
  the build stamp `generated`.
- **`dateFields`** — `generated`, `verified`, `stale_after`.

## The three rules that are easy to get wrong

**A page in `SUMMARY.md` is a published concept.** The gate enforces that every
`SUMMARY.md` link target is inside the bundle, so adding a link to a page that
sits in an excluded tree fails CI. Either the page belongs in the bundle, or it
does not belong in the table of contents.

`SUMMARY.md` itself is **hand-maintained** in this repository, and is excluded
from the bundle because it is navigation *over* the graph rather than a node in
it. There is no generator for it here; adding a page means editing it.

**Capability concepts are generated. Never edit them by hand.** Everything under
`docs/okf/capabilities/` is a pure function of
[`docs/gis/data/capability-keys.v1.json`](../gis/data/capability-keys.v1.json)
and the capability matrix. They are deliberately *not* derived from prose,
because an edit to any page would otherwise restale all of them at once.

**`honua://capability/<key>` must resolve.** That URI is the identity shared by
the capability concepts, the capability matrix, the licensing registry and the
route mapping — it is what lets an agent land on a concept and join straight to
the evidence. A `resource:` naming a key that is absent from
`capability-keys.v1.json` fails the gate.

## Running the gates locally

```bash
python3 scripts/ci/check-okf-bundle.py --summary       # gate, plus a type census
python3 scripts/ci/generate-capability-concepts.py --check   # fail on drift
python3 scripts/ci/generate-capability-concepts.py           # regenerate
python3 scripts/ci/generate-capability-concepts.py --report  # prose coverage
python3 scripts/ci/check-okf-bundle.test.py            # the checker's own tests
python3 scripts/ci/generate-llms-txt.py                # docs/llms.txt, stale after any SUMMARY edit
python3 scripts/ci/check-doc-links.py                  # links, anchors, and llms.txt freshness
```

CI runs these in
[`.github/workflows/docs-link-gate.yml`](../../.github/workflows/docs-link-gate.yml).

## Adding a page

1. Give it frontmatter opening with `type`. Add `title` and `description` unless
   there is a reason not to — they are what an agent reads before deciding to
   open the file.
2. Put it where its path states what it is. Path is identity, so moving a page
   later is a URL break that needs a redirect in `docs/.gitbook.yaml`.
3. Link it from `SUMMARY.md` if it is published. If it is not published, it
   belongs in an excluded tree, and the exclusion needs a `why`.
4. Run the gate before pushing.

If a page genuinely is not documentation — dated evidence, a status snapshot,
contributor mechanics — do not give it frontmatter to quiet the gate. Add it to
the manifest with a reason. The reasons are the part that keeps the boundary
honest a year from now.

## Where to go next

| Page | Why |
| --- | --- |
| [Capability concepts](capabilities/README.md) | The generated half of the bundle, and the worked example of `resource` as identity. |
| [Reference](../reference/README.md) | Where these pages sit in the table of contents. |
| [Architecture](../concepts/architecture.md) | What `type: concept` looks like in practice. |
| [Guides](../guides/README.md) | What `type: guide` looks like in practice. |

## This is a nine-repo contract now

honua-server is one area of an aggregate: every published Honua documentation set
is vendored into a single GitBook space, and **each repository's own `SUMMARY.md`
is the inclusion list**. A page that is not in `SUMMARY.md` is not published —
not here, and not in the aggregate.

[okf]: https://github.com/GoogleCloudPlatform/open-knowledge-format
