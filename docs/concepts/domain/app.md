---
type: concept
title: "Map application"
description: "An interactive composition combining maps, controls, widgets, and behavior."
tags: [gis, domain-language]
aliases: ["map app","application","interactive map","dashboard"]
sources:
  - id: contract
    resource: "https://github.com/honua-io/honua-studio/blob/8a658c7f75887df8842fabb50cd2585be64ce48d/README.md"
---

# Map application

A map application combines a [map](map.md) with controls, widgets, and interactions. Studio's typed composition model supports iterative changes to these elements, with a live canvas and an activity history.[^contract]

An app's source assets and interaction rules are creative content; changing them is not the same as changing GIS source features. A selected feature can drive a panel without granting authority to edit that feature.

“Build an app” needs the audience and desired interaction: browse, select, compare, inspect, or run supported analysis. Check current widget, data-source, and renderer support. A saved package, a locally previewed application, and a deployed public site are distinct outputs.

Use the [Studio workspace](studio-workspace.md) semantics for prompt, preview, refine, and undo. Publishing or deploying the result crosses a separate actual policy boundary; ordinary design iteration does not require a proposal ceremony.

[^contract]: [Source contract](https://github.com/honua-io/honua-studio/blob/8a658c7f75887df8842fabb50cd2585be64ce48d/README.md).
