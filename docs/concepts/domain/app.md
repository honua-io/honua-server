---
type: concept
title: "App"
description: "An interactive composition combining maps, controls, widgets, and behavior."
tags: [gis, domain-language]
aliases: ["map app","application","interactive map","dashboard"]
sources:
  - id: contract
    resource: "https://github.com/honua-io/honua-studio/blob/8a658c7f75887df8842fabb50cd2585be64ce48d/README.md"
---

# App

An App combines a [map](map.md) with controls, widgets, and interactions. Studio's typed composition model supports iterative changes to these elements, with a live canvas and an activity history.[^contract]

An app's source assets and interaction rules are creative content; changing them is not the same as changing GIS source features. A selected feature can drive a panel without granting authority to edit that feature.

“Build an app” needs the audience and desired interaction: browse, select, compare, inspect, or run supported analysis. Check current widget, data-source, and renderer support. A saved Version, a locally previewed App, and a Released App are distinct outputs. An App follows the same [Draft → Version → Released lifecycle](content-version.md) as a Map. **Go live** means activate its saved Version at the governed route.

Use the [Studio editor](studio-workspace.md) semantics for prompt, preview, refine, and undo. Publishing or deploying the result crosses a separate actual policy boundary; ordinary design iteration does not require a proposal ceremony.

[^contract]: [Source contract](https://github.com/honua-io/honua-studio/blob/8a658c7f75887df8842fabb50cd2585be64ce48d/README.md).
