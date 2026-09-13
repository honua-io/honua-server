---
type: capability
title: "gRPC (geospatial.v1)"
description: "The geospatial.v1 gRPC surface — FeatureService, ProcessService, SpecService, SceneService, TileService and ElevationService — served natively over h2c on port 8081 and as gRPC-Web on 8080. Advertised as transport.grpc, transport.grpc-web and transport.native-grpc on the capability manifest, which are wire framings of this one capability."
resource: "honua://capability/serve.grpc"
tags: [capability, serve, community]
---
<!-- GENERATED FILE - DO NOT EDIT. Regenerate with scripts/ci/generate-capability-concepts.py -->

# gRPC (geospatial.v1)

The geospatial.v1 gRPC surface — FeatureService, ProcessService, SpecService, SceneService, TileService and ElevationService — served natively over h2c on port 8081 and as gRPC-Web on 8080. Advertised as transport.grpc, transport.grpc-web and transport.native-grpc on the capability manifest, which are wire framings of this one capability.

| | |
| --- | --- |
| Capability key | `serve.grpc` |
| Category | Serve |
| Edition | Community |

The facts above come from `docs/gis/data/capability-keys.v1.json` and `capability-matrix.v1.json`, both generated from the server's own registry and test evidence. This page is a pure function of those two files — nothing in it depends on what the prose happens to say, so an unrelated documentation edit cannot stale it.

Which pages discuss this capability is a question about the prose, so it is reported rather than baked in: run `scripts/ci/generate-capability-concepts.py --report`.
