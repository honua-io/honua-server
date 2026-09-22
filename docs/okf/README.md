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

Every page in the table of contents is a published concept. Dated evidence,
status snapshots and build mechanics are kept out of the bundle deliberately, so
a page you can reach from here is documentation and not a report.

## Where to go next

| Page | Why |
| --- | --- |
| [Capability concepts](capabilities/README.md) | One concept per capability, keyed by `honua://capability/<key>`, with edition and status. |
| [Editions and licensing](../concepts/editions-and-licensing.md) | What each edition includes, which is what a capability's `Edition` row refers to. |
| [Reference](../reference/README.md) | Where these pages sit in the table of contents. |
| [Architecture](../concepts/architecture.md) | What `type: concept` looks like in practice. |
| [Guides](../guides/README.md) | What `type: guide` looks like in practice. |

[okf]: https://github.com/GoogleCloudPlatform/open-knowledge-format
