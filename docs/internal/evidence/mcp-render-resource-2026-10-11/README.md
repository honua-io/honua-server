# Rendered map resources through MCP

Observed 2026-10-11 for server issue 5867. [The receipt](live-proof.json) proves the
local source fix; it is not an immutable candidate or supported installer release
qualification.

## Runtime identity

- Server source: `42ee789d6a74d5eacf1c1d97a17e2a2a3c459cde`, based on trunk
  `5584a0dee5da4dc639b9701162a21e971ba4002a`. The receipt records hashes of the
  compiled Server, Ai, Core and Hosting assemblies copied to a read-only runtime
  snapshot before subsequent test-only commits.
- Runtime base: `ghcr.io/honua-io/honua-server@sha256:785de2ae7f72e6277ce62aa1ece4f08ee0425970b71c47f9f4843c0baae2c8db`,
  originally source `93cad86ec4fb28fba3ff0b9358059d86aa55dde5`.
- API-only JIT harness: local build static-web-asset discovery disabled; the base
  image's unchanged SkiaSharp NoDependencies 3.119.2 library mounted over the JIT
  distro-linked asset because the AOT image lacks fontconfig. Both source trees
  declare SkiaSharp 3.119.2. The native library hash is recorded.
- Fresh database snapshot of the owned installer fixture, separate server port,
  Development environment and explicit canonical 2026.1 `Licensing__Mode=Disabled`.
  Existing candidate databases, keys, services and installer pins were retained.
- Native MCP identity: `honua.operator.mcp`, `v1`. The generated config launches
  actual registry `@honua/mcp-server@0.1.14` via its stdio proxy, using the same
  installer-minted key as direct MCP. No credentials are included.
- SDK qualifier source: `5085634a00e4964c4c1b8359fdad67301abab092`; the prepared
  input/dist digests and relevant compiled module hashes are recorded. The
  three relevant source files match their prepared-input entries at that commit.

## Positive controls

The full direct and generated-stdio setup catalogs match, including metadata.
Each transport renders `svc-publish-installer1401`, layer 1, passes the exact
returned resource-link URI to MCP `resources/read`, and receives binary-only
`image/png` content. Both return identical 2,363-byte, 512×512 PNGs:

`e6a27a24672ddcbe8b6fbfba0bcf24edf91f60df69a9c616826fa1ba6342efd2`

Both render responses retain style `installer1401-parcels` with
`styleRendering: applied`. Pixel inspection of the same hash finds 63,504 orange
fill pixels and 2,048 dark outline pixels alongside the white background. The
stable `honua://temporary-files/{fileId}` alias returns the same bytes.

The existing compiled SDK bounded setup qualifier completes get-style,
apply-style-preset, render, and MCP resource read. A fetch hook redirects only the
owned installer origin to the patched sibling; it does not substitute an HTTP
artifact download or bypass authentication. Its overall result remains **false**
with the original two immutable-image-pin differences intact. SDK issue 1401
still requires a released client/server pair and release-consumed receipts.

## Refusal and regression coverage

The live anonymous session receives the canonical `unauthenticated` JSON-RPC
error with no result. Focused tests exercise the canonical real temporary store
for unrelated-owner refusal and expiration, the MCP principal boundary for an
unresolved bearer tenant, malformed and foreign URIs, exact emitted URI and
canonical alias dispatch, raw byte limits before base64 allocation, and atomic
binary pagination. The adapter inherits existing principal/tenant policy; it
does not introduce a new tenant ownership model.

Focused test validation is pending the shared build slot. Full solution pre-PR checks
are not represented as a local pass; repository PR gates remain required.
