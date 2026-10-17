---
type: reference
title: "Local Docker preview artifact"
description: "Published image identity and bounded installation evidence for the local Docker evaluation guide."
---
# Local Docker preview artifact

The [quickstart](../get-started/quickstart.md), [Linux](../get-started/linux-packages.md),
and [Windows](../get-started/windows-packages.md) instructions select this published
preview image while the 2026.1 release channel is unavailable. This is a local
evaluation artifact, not release promotion or end-to-end platform qualification.

| Field | Observed value |
| --- | --- |
| Observation date | 2026-10-17 UTC |
| Public image tag | `ghcr.io/honua-io/honua-server:nightly-0e03bdd` |
| Resolved immutable image | `ghcr.io/honua-io/honua-server@sha256:ea016718c8cad41cb995bd10429f92c40d6736e3bc9e413fa07e332ad24bc5d3` |
| Image source revision | `0e03bdd06b3ca3fb1b82388110bb5dfa19b9f8fa` |
| Tested platform | Linux amd64, Docker Compose v5.5.1 |
| Runtime profile | Production, native AOT, licensing disabled, local PostGIS and Redis |
| Python clients | `honua-admin` 0.1.12 and `honua-sdk` 0.1.16 |

The public tag was pulled using an empty Docker client configuration. No registry
credentials were supplied. Docker's resolved digest and the image's
`org.opencontainers.image.revision` label were recorded separately.

To reproduce these exact image bytes, set `HONUA_IMAGE` to the immutable image
above. On Windows also set `$Image` to that reference. A tag can move; a later
pull at another digest is a different artifact and needs its own observations.
The observed digest differs from the older release-manifest record for this source
revision, so that record's certification is not borrowed for this preview.

## Observed installation behavior

The quickstart Compose YAML and Python publish/query example were extracted
from the guide into a new isolated project. Loopback ports were changed to avoid
existing projects. Installation secrets were generated directly into private
files; the documented key-ring init container set the server-readable mount.
The host lacked Python's `ensurepip`, so `uv venv` supplied an isolated Python
3.11 environment with the exact published client versions above. This is a
validation-host substitution, not evidence that `python -m venv` worked there.

- Anonymous image retrieval succeeded.
- The fresh PostGIS, Redis, and Honua containers became healthy.
- `/healthz/ready` returned `200 Ready`; anonymous admin access returned 401,
  while the authenticated admin version request returned 200.
- The documented `public.places` table was created with the two example points.
- The unmodified Python example registered the connection and published layer 1
  in service `quickstart` using authenticated SDK calls.
- The query returned `west` at (-157.875, 21.3125) and `east` at
  (-155.0625, 19.6875), with their expected IDs and attributes.
- After restarting all three services, the SDK query returned the same exact
  identities, attributes, and coordinates without recreating the table or layer.

These observations cover the local packaged server and sample data path. They
are not clean Windows qualification, a browser map rendering result, a genuine
model evaluation, a cloud deployment, or update/rollback certification.

## Release channel

At the observation time, `ghcr.io/honua-io/honua-server:2026.1-rc` returned
`manifest unknown`/`not found`, and the advertised public customer install
manifest had returned HTTP 404. The preview instructions therefore require
neither. Only the normal release-promotion path should assign the release
channel; a working preview does not establish its qualification.
