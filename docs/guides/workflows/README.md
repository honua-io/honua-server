---
type: index
title: "GIS workflow contracts"
description: "Human walkthroughs and structured workflow contracts for analysis, publishing, map/app design, and operation."
tags: [gis, domain-language, workflow]
---

# GIS workflow contracts

Start with [Use the Honua GIS workflow language](use-workflow-language.md): set up
Honua with AI, add services and layers and operate Honua with AI, and make maps
in realtime with AI. It explains how ordinary requests become typed intent,
installed tool calls, and observed results.

Each guide combines a readable walkthrough with typed inputs, outputs, actions, effects, checks, recovery, and links to relevant interfaces. Read the [language contract](../../concepts/domain/workflow-language.md) before interpreting these fields as machine metadata. They describe intent and required outcomes; actual runtime contracts determine execution.

Routine Studio design is the direct prompt/edit/preview/refine/undo loop. Data publication, audience changes, and deployment use their existing policy and rollback surfaces when requested. All AI workflows preserve authoritative GIS source records.

## Workflows

- [Install and configure a durable environment](install-configure.md)
- [Connect and publish existing data](connect-publish-data.md)
- [Import data with explicit geometry and provenance](import-data.md)
- [Discover and disambiguate data](discover-data.md)
- [Compose a map from readable layers](compose-map.md)
- [Analyze features with explicit spatial meaning](analyze-features.md)
- [Save, reopen, and resume a map](save-resume-map.md)
- [Publish and share a saved map](publish-share-map.md)
- [Configure and verify resource access](govern-access.md)
- [Recover or cancel an interrupted job](recover-job.md)
- [Run a native conversion and retain its output](run-native-job.md)
- [Clean up owned outputs and configuration](cleanup-resources.md)
- [Design an App through prompt and preview](design-studio.md)

## Which MCP view to select

Each guide names MCP tools by their exact names. The [workflow views](../connect/ai-agents-mcp.md#workflow-views-bounded-discovery) below carry every MCP tool that guide names; where no single view does, the row names the views to combine. View membership is server-owned and can change: confirm it with `tools/list` for the view rather than keeping this table in a client.

| Guide | View |
|---|---|
| [Install and configure](install-configure.md) | `default` or `setup` for `honua_list_capabilities`, plus `operate` for `honua_ops_health` |
| [Connect and publish existing data](connect-publish-data.md) | `configure` |
| [Import data](import-data.md) | `configure` |
| [Discover data](discover-data.md) | `analyze` for `honua_ground_candidates`, plus `default`, `setup` or `configure` for `honua_describe_layer` |
| [Compose a map](compose-map.md) | `setup` |
| [Analyze features](analyze-features.md) | `default`, `setup` or `analyze` |
| [Save, reopen, and resume a map](save-resume-map.md) | `setup` |
| [Publish and share a saved map](publish-share-map.md) | `setup`, plus `review` for `honua_validate_package` |
| [Configure and verify resource access](govern-access.md) | `configure` |
| [Recover or cancel a job](recover-job.md) | `analyze` |
| [Run a native conversion](run-native-job.md) | `default` or `setup` |
| [Clean up owned outputs](cleanup-resources.md) | `default`, `setup`, `configure` or `analyze` |

A workflow page is not a release qualification receipt. Follow the source-linked implementation limits, including preview status and the distinction between the current Studio draft adapter and its Git worktree design contract.

## Vocabulary review rule

Review workflow prose and semantic bindings against the [canonical glossary](../../concepts/domain/README.md). Use Connection, Dataset, Layer, Service, Folder, Environment, Map, App, Draft, Version, and Released for those objects. Reject Composition, Published layer, workspace as a grouping or tenancy object, and slot or serving target as user-facing objects. Distinguish a Layer from a Map's drawing layer. Keep exact current API identifiers, schema values, and historical file paths unchanged when documenting their existing contracts; they do not define alternative object names.

## Stage map and vocabulary check

The [machine-readable stage map](stage-map.v1.json) records one row for each
workflow action: its semantic verb, canonical object names, OKF action reference,
and source-linked REST, SDK, CLI, MCP and skill bindings. An operation keeps its
exact callable spelling; its `verb` records which workflow action it implements.
These mappings describe stages, not interchangeable signatures or complete
implementations of every stage requirement.

Run `python3 scripts/ci/check-workflow-stage-map.py --gaps` when changing a workflow
or one of its recorded interfaces. The docs gate checks stage coverage, verbs,
object names, referenced operations and skill evidence. A retired wire spelling
needs an explicit `literalReason`; it does not change the canonical object name.
Update the map with the workflow and the linked source. Empty bindings carry an
explicit documentation gap, not a claim that the runtime lacks the capability.
Runtime discovery and live verification remain necessary.
