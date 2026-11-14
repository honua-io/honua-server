---
type: reference
title: "Zarr coverage support"
description: "Executable Zarr v2 and v3 subset support, supported layouts, and stable rejection codes."
---
# Zarr coverage support

The [versioned support matrix](../../gis/data/zarr-support-matrix.v1.json) is the
machine-readable contract for coverage subset reads and scalar decoding. Consumers
should check its `format` (`honua.zarr-support/v1`) and select a matching `cells`
entry instead of maintaining separate dtype or codec tables. Each entry specifies
Zarr version, dtype, element size, order, chunk-key encoding, separator, codec and
bytes-codec endianness. A null endianness means the v3 bytes configuration omits it.
`none` means raw chunks (a v3 pipeline still begins with `bytes`).

<!-- zarr-support-summary:start -->

| Version | Fixture cells | Scalar types | Chunk codecs |
| --- | ---: | --- | --- |
| 2 | 198 | |b1, |i1, |u1, <i2, <u2, <i4, <u4, <i8, <u8, <f4, <f8 | none, zlib, gzip |
| 3 | 136 | |b1, |i1, |u1, <i2, <u2, <i4, <u4, <i8, <u8, <f4, <f8 | none, gzip |

<!-- zarr-support-summary:end -->

Every advertised cell is exercised by an in-memory store through metadata parsing,
chunk decoding and subset reading. Unsupported dtype, endianness, order, chunk-key,
codec, pipeline and filter families carry stable `zarrReasonCode` exception data;
these codes are included in the projection and have rejection fixtures.

The contract describes regular, nonempty arrays in C order. V2 native (`=`) and
neutral (`|`) dtype aliases retain the reader's little-endian interpretation; they
do not enable big-endian samples. V3 single-byte values accept either byte order
or an omitted endian setting. V3 multi-byte values require `little`. Gzip level
configuration must be an integer from 0 through 9. V2 filters must be null or empty.
Existing group discovery and georeferencing requirements still apply. This matrix
does not add persistence, hierarchy, codecs or general Zarr implementation support.

The Core `ZarrSupportMatrix` contract owns the projection. The Fast-tier
`ZarrSupportMatrixTests` compare it byte for byte with the committed JSON, exercise
all its cells, and check the generated documentation summary. To regenerate the projection,
run that test with `HONUA_EMIT_ZARR_SUPPORT_MATRIX=1` through the build-host shim.
SDK eligibility checks and evidence producers can consume the same versioned JSON.
