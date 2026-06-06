// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Xunit;

namespace Honua.Protocols.GeoServices.Tests;

// Parallelism note (#1359):
//
// The classes in this assembly drive the database-backed server through a per-class
// `new WebAppFixture()` (IAsyncLifetime), not a shared collection fixture — the
// `[Collection("Database")]` attribute they historically carried only serialized them.
// xUnit parallelizes ACROSS collections, so splitting the single Database collection
// into shard-aligned collections lets the GeoServices ImageServer shard run its
// ImageServer / GPServer / GeometryService / Catalog / NAServer classes concurrently.
//
// Per-test isolation is unchanged and real: each WebAppFixture gets its own PostgreSQL
// schema and the shared in-memory server is schema-routed per request. The metadata-v2
// graph singleton is partitioned by schema in TestMetadataV2GraphProvider, so concurrent
// metadata mutations across schemas no longer collide.
//
// These collections intentionally declare no ICollectionFixture — the fixture is
// per-class — so they exist only to give xUnit distinct, parallelizable collection names.

/// <summary>
/// Database-backed collection for ImageServer / GPServer / GeometryService tests.
/// </summary>
[CollectionDefinition("Database.GeoServicesRaster")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1711:Identifiers should not have incorrect suffix", Justification = "This is an xUnit collection definition which requires the Collection suffix")]
public class DatabaseGeoServicesRasterCollection
{
}

/// <summary>
/// Database-backed collection for GeoServices Catalog / NAServer tests.
/// </summary>
[CollectionDefinition("Database.GeoServicesCatalog")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1711:Identifiers should not have incorrect suffix", Justification = "This is an xUnit collection definition which requires the Collection suffix")]
public class DatabaseGeoServicesCatalogCollection
{
}
