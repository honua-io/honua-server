---
type: index
title: "Open Knowledge Format in honua-server"
description: "How this documentation is shaped for agents: one page per concept, the file path as identity, frontmatter that says what a page is before it is opened, and honua://capability/<key> as the join to the capability registry."
tags: [okf, documentation, agents]
---

# Open Knowledge Format in honua-server

This documentation is an [Open Knowledge Format][okf] v0.2 bundle, not just a
folder of markdown. The contract is short:

> One markdown file is one concept; the file path is the concept's identity;
> relative markdown links are the graph edges. `type` is the only required
> frontmatter field.

That is what lets an agent treat the documentation as a graph rather than a
search corpus: land on a page, read its frontmatter to decide whether it is the
right one, follow its links to the adjacent concepts, and join from a capability
page straight to the registry that proves it.

## What the frontmatter tells you

Every page opens with a frontmatter block. Read it before the body.

- **`type`** — what kind of concept the page is. `concept` explains an idea,
  `guide` walks through a task, `reference` states a contract, `index` lists a
  section, and `capability` is one entry of the server's capability registry.
- **`title` and `description`** — what the page is, written so a reader or an
  agent can decide whether to open it without reading it.
- **`resource`** — the stable identity of the thing the page documents. For a
  capability it is `honua://capability/<key>`; a prose page that documents a
  capability declares the same key, and the capability page links back to it
  under *Documented in*. A page that documents several keys lists them under
  `resources`.
- **`tags`**, **`generated`**, **`verified`** and **`stale_after`** — optional.
  `generated` is the build stamp on a page produced from data; `stale_after`
  says when a dated claim should no longer be trusted.

## Capability keys are the join

`honua://capability/<key>` is the identity shared by the
[capability concepts](capabilities/README.md), the capability matrix, the
licensing registry and the route mapping. A key that appears on a concept page
appears in every one of those, so an answer found here joins to the edition that
includes the capability and the evidence that proves it without a name lookup.
A `resource` naming a key that the registry does not carry is an error, so the
join cannot silently dangle.

The capability concepts are generated from the registry rather than written by
hand, so they say exactly what the registry says: which edition carries the
capability, its lifecycle status, and how mature its surfaces are. A capability
appearing there is not a statement that it is generally available; `preview` and
`experimental` capabilities carry usage restrictions stated in full on each page.

## What is in the bundle

The [GIS domain language](../concepts/domain/README.md) and [workflow contracts](../guides/workflows/README.md) extend this same bundle with common phrases, explicit ambiguities, typed composition, effect boundaries, recovery, and relevant interface links. They are shared human and agent documentation, not a separate test vocabulary. Agent indexes and compact context are generated views of these pages; full explanations remain in Markdown.

Every page in the table of contents is a published concept. Dated evidence,
status snapshots and build mechanics are kept out of the bundle deliberately, so
a page you can reach from here is documentation and not a report.

## Where to go next

### Agent graph and context

Run these commands from the repository root with Python 3.10 or later. First install the YAML reader into your active Python environment with `python -m pip install -r scripts/ci/okf-requirements.txt`. This dependency is shared by graph generation, OKF validation, and `llms.txt` generation; CI installs the same requirements.

The derived [agent graph](domain-graph.v1.json) preserves the published OKF metadata and links each node to its human document. It includes typed workflow dependencies, semantic relations, interface references, and a content digest. Edit the Markdown sources; regenerate this view with `python scripts/ci/generate-okf-graph.py`. The documentation gate checks that the generated file is current.

For a compact local context, run `python scripts/ci/generate-okf-graph.py --query "publish a layer"`. Retrieval returns lexical candidates and their direct semantic neighbors. Alias collisions remain visible: a match is not a decision about which kind of layer or publication the user intends. Read the full linked document when the compact metadata is insufficient.

Interface bindings report their validation level. `documentation_reference_only` establishes a published reference, while `openapi_method_and_path` additionally checks the operation against a local OpenAPI document and records its digest. Neither validates a composed request or proves runtime support. Discover installed schemas, bind actual returned identifiers, and observe outcomes before claiming execution.

Consumers should pin the source repository revision and `bundleDigest` together. Human links point to the current source branch; use the pinned revision when reproducing an earlier interpretation. The graph excludes internal evidence and private test answers, and never grants execution authority.

For reproducible evaluation, also record the generated graph digest and a manifest
of the document bytes actually made available to the worker. Verify that manifest
inside the execution boundary before the first task. A source commit alone does
not establish that the worker received those documents. Resolve document links
against the pinned revision; do not silently substitute the moving branch or a
different documentation deployment. Retrieval may select public concepts from
this bundle, but private scenario mappings and expected outcomes stay outside the
worker's context.

Keep document-consistency checks, deterministic runtime semantic checks, and
model goal-completion results separate. Static graph validation still reports
`executionVerified: false`. A runtime report should bind the installed interface
schema, candidate revision, source observations and result observations alongside
the knowledge identity. Missing observations are not successful execution.

| Page | Why |
| --- | --- |
| [Capability concepts](capabilities/README.md) | One concept per capability, keyed by `honua://capability/<key>`, with edition and status. |
| [Editions and licensing](../concepts/editions-and-licensing.md) | What each edition includes, which is what a capability's `Edition` row refers to. |
| [Reference](../reference/README.md) | Where these pages sit in the table of contents. |
| [Architecture](../concepts/architecture.md) | What `type: concept` looks like in practice. |
| [Guides](../guides/README.md) | What `type: guide` looks like in practice. |

[okf]: https://github.com/GoogleCloudPlatform/open-knowledge-format
