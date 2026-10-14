---
type: index
title: "GIS workflow contracts"
description: "Human walkthroughs and structured composition contracts for analysis, publishing, map/app design, and operation."
tags: [gis, domain-language, workflow]
---

# GIS workflow contracts

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
- [Design a map application through prompt and preview](design-studio.md)

A workflow page is not a release qualification receipt. Follow the source-linked implementation limits, including preview status and the distinction between the current Studio draft adapter and its Git-workspace design contract.
