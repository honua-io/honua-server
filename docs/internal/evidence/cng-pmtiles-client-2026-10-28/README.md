# PMTiles canonical-client payload and HTTP evidence, 2026-10-28

Refs #6704. These are source-bound engineering receipts, not a frozen campaign result or published-candidate qualification. Producer and serving sources are deliberately recorded separately.

## Identity and result

Producer: `af0c17d2e61ebc620a7eb69e094f299bf736af19`, existing managed artifact generator. It exited 0 and emitted 21 valid MVT tiles in 25216 bytes, SHA256 `fdf309bda8f0c4ce3e4ac08c5c88ddbddea2b9dbc3736f0604d755cc4e702a54`. Canonical input hashes were unchanged; the generator Program.cs blob is unchanged by subsequent source-binding/timing/evidence edits.

Serving source: `57d2c31132e52147d79ca702db34b70e7954abac`; diagnostic image `sha256:458cacbfb57e3bddde46472de71fa0d62f8163bf43608b6c75a580b7b9d2fecb`. See the [managed source/image bindings](../cng-multidim-diagnostic-2026-10-28/source-bindings.json). This is a CI-managed server with an S3 emulator, not a source-matched published production image or real AWS.

| Actual client | Assertion | Range requests | Bytes | Whole-object downloads | Objects |
|---|---|---:|---:|---:|---:|
| PMTiles JS 4.5.0 + official Mapbox vector-tile 3.0.0 / pbf 5.1.0 | Three separated tile lookups, decoded layer/schema/id/name/point/full attribute |5|20099|0|1|
| PMTiles Python 3.7.0 | Exact 21 archive tile coordinates, nonempty payloads |26|25470|0|1|

Both passed the existing 32-request / 8 MiB / 0-whole-object / minimum 1-range profile without a budget or denominator change. The JS client's normal 16 KiB header prefetch was retained; the new 25216-byte archive exceeds it. HTTP response ranges and bytes were compared with the exact generated archive by the existing observers.

The corrected Python receipt interval is `2026-10-28T06:14:49.276945Z` to `2026-10-28T06:14:50.791067Z` (1.514122 seconds). A prior receipt incorrectly stamped completion before I/O; it is retained privately as superseded timing evidence. The validator now stamps completion in finally after actual client I/O on both success and failure. No timestamp was rewritten after the run.

## Red controls and fixture authorization

- `pre-fix-opaque.pmtiles` is the preserved 1107-byte archive, SHA256 `75afff4d7e181018a7113c4e0f702df62b551b5e8c13f60235984b819255a866`. Official PMTiles read its v3/MVT/21-tile header and 16-byte z0 payload; official Mapbox/pbf decoding failed with `Unimplemented type: 6`. This was file-reader proof of invalid payload, not HTTP evidence.
- The initial real HEAD/GET returned 404 because the fixture's object metadata was unbound. That was correct server policy, not a production-byte defect. The trusted fixture seed now stamps the actual catalog layer/service/publication/resource/storage IDs after readback. Anonymous/unbound policy was not enabled.
- The source binding is an authorization association for this synthetic fixture. It does not prove that the tile-operations publication API materialized these bytes from the catalog layer; `publication_api_proven` stays false.

The existing metadata regression rejects a decoded wrong-tile object; it is helper coverage, not encoded wire proof. Lightweight final regressions passed 52 existing validator tests, 9 Node metadata/range tests, and 5 PMTiles HTTP-helper tests. Managed generator compilation/execution and official new-wire decoding were actual additional runs, not inferred from those mocks.

## Replay isolation and contents

Only the previously stopped NC diagnostic project's infrastructure and server restarted. Its worker remained stopped. LocalStack had `NoSuchBucket` after restart, so the fixture bucket was created and only `pmtiles/cng/honua.pmtiles` uploaded. Existing NC/HDF objects were not restored or jobs rerun; old store URLs are not claimed live. The catalog binding readback was unchanged. Only the four restarted containers were stopped afterward.

- `producer-af0c.pmtiles`, `new-wire-green.json`: actual new bytes and official file-reader semantic proof.
- `javascript-http-observations.json`, `python-http-proof.json`: real HTTP observations with unchanged budget evaluation. The existing JS runner also records an expected non-target FlatGeobuf ENOENT because this replay supplied no `cng.fgb`; it is not a FlatGeobuf result or an overall runner pass.
- `serving-source.json`, `s3-object-metadata.json`, `source-binding-readback.json`: actual object/route/catalog fences.
- `unbound-fixture-red.json`: selected actual refusal events, not full server logs.
- `source-and-generator-binding.json`, `replay-cleanup.json`: exact producer/serving identities, source hashes and cleanup.
- `SHA256SUMS`: integrity for every other file in this directory.

Receipts contain no credentials or request authorization headers. No release pins, production deployment, frozen matrix rows, or full compliance claim changed.
