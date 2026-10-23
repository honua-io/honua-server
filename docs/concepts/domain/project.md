---
type: concept
title: "Project and workspace language"
description: "A disambiguation concept for a user's grouping of work and the specific persisted resources it contains."
tags: [gis, domain-language]
aliases: ["project","workspace","analysis project"]
sources:
  - id: contract
    resource: "/studio/mcp-tools.md"
relations:
  - predicate: relates_to
    target: "/concepts/domain/content-version.md"
  - predicate: relates_to
    target: "/concepts/domain/job.md"
  - predicate: relates_to
    target: "/concepts/domain/map.md"
  - predicate: relates_to
    target: "/guides/workflows/save-resume-map.md"
---

# Project and workspace language
People use “project” for a collection of related datasets, maps, analyses, and outputs. Treat it as intent until the product surface identifies the concrete resource. The Studio lifecycle documented here persists drafts and [content versions](content-version.md); a map draft is not evidence of a universal Project API.[^contract]

“Workspace” is also overloaded. A Studio grouping identifier, a geoprocessing output workspace, a local directory, and a cloud environment have different owners and lifetimes. A [job](job.md) output workspace does not automatically contain a Studio [map](map.md).

For “reopen my project,” resolve the named saved item and version, intended tenant, and required data references. Ask for clarification when multiple readable items match. Preserve the concrete identifiers in the handoff for [save and resume](../../guides/workflows/save-resume-map.md); do not invent a project identifier or promise persistence from a folder name.

[^contract]: [Source contract](../../studio/mcp-tools.md).
