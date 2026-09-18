---
type: reference
title: "GeocodeServer provider parity matrix"
description: "Which GeocodeServer operations each geocoding provider supports, how batch and rate limits are enforced, and the known caveats."
resource: "honua://capability/serve.geoservices-geocodeserver"
resources:
  - "honua://capability/geocoding.batch"
  - "honua://capability/geocoding.reverse"
---
# GeocodeServer provider parity matrix

This page records GeocodeServer parity across Honua's geocoding providers and against Esri
GeocodeServer behavior. The per-provider capability matrix below is checked against each
provider's advertised capabilities on every build, so a regression fails the build.

For the operation-by-operation comparison against Esri GeocodeServer, see
[GeoServices REST parity](../compatibility/geoservices-parity.md).

## Status vocabulary

- **Supported** — the operation is implemented and exercised by tests.
- **Mapped** — the operation is supported, but an input is adapted onto the provider's native shape
  (e.g. structured fields composed into a single text query) rather than passed through 1:1.
- **Unsupported** — the provider does not expose the capability; the coordinator skips it and fails
  over to a capable provider.

## Provider capability matrix

| Capability | Nominatim | Azure Maps | Amazon Location | Local PostGIS |
| --- | --- | --- | --- | --- |
| Forward geocode | Supported | Supported | Supported | Supported |
| Reverse geocode | Supported | Supported | Supported | Supported |
| Suggest | Supported¹ | Supported | Supported | Supported |
| Batch (`geocodeAddresses`) | Supported² | Unsupported³ | Unsupported³ | Supported² |
| Structured input | Supported | Supported | Mapped⁴ | Supported |
| Proximity bias | Supported | Supported | Supported | Unsupported |
| Requires API key | No | Yes | Yes | No |
| Native batch cap (`MaxBatchSize`) | 100 (configurable) | 0 | 0 | 1000 |
| Advertised `RateLimitPerMinute` | — | 500 | 100 | — |

¹ Nominatim suggest is derived from its search endpoint; can be disabled via configuration.
² No native batch endpoint — fanned out to sequential single-address calls by the shared base
  provider, preserving Esri `ResultID` alignment.
³ No native batch; the coordinator fails over to a batch-capable provider for `geocodeAddresses`.
⁴ AWS Location v1 has no structured-address request; structured components are composed into the
  `Text` query (graceful mapping), and the same structured fields are advertised in capability
  metadata.

## Licensing and limit enforcement

- Advertised per-provider `RateLimitPerMinute` is **enforced** at request time by the shared
  `IGeocodeLimitEnforcer` in the geocoding coordinator. Over-limit requests are rejected with HTTP
  `429 Too Many Requests` and a `Retry-After` header; the fixed window resets after one minute.
  Enforcement can be disabled with `Geocoding:EnforceRateLimits = false`.
- Advertised `MaxBatchSize` is **enforced** for `geocodeAddresses`. An optional licensing/edition cap
  (`Geocoding:MaxBatchSizeLimit`) further bounds the effective batch cap across all providers; the
  effective cap is `min(provider.MaxBatchSize, MaxBatchSizeLimit)`. Over-cap batches are rejected
  with HTTP `400` carrying the advertised cap so clients can chunk to a compliant size.

## Caveats

- **Hosted providers are verified against recorded responses.** Azure Maps and Amazon Location
  structured-input fidelity is checked against recorded responses; live-key verification is not
  part of the default path.
- **Unsupported locator constructs.** Honua exposes a single anonymous, read-only locator.
  Esri-specific locator constructs (suggest categories beyond provider-supplied address types,
  multi-locator composite services, and locator-side custom output fields) are not modeled.
