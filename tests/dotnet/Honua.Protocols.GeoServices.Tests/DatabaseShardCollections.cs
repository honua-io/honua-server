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
/// Legacy database collection for shared-catalog GeoServices tests that have not been
/// proven schema-parallel safe.
/// </summary>
[CollectionDefinition("Database", DisableParallelization = true)]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1711:Identifiers should not have incorrect suffix", Justification = "This is an xUnit collection definition which requires the Collection suffix")]
public class DatabaseGeoServicesSerialCollection
{
}

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

/// <summary>
/// Serialized database-backed collection for the GeoServices SceneServer catalog tests.
/// </summary>
/// <remarks>
/// honua-server#1568 (signature 2 — cross-collection visibility): unlike services,
/// layers, and the rest of the catalog (read through the schema-partitioned in-memory
/// Metadata v2 graph), the Esri I3S scene registry persists to and reads from the
/// literal, process-global <c>honua.scene_datasets</c> table — schema-qualified, so the
/// per-test <c>search_path</c> isolation does NOT scope it. Two scene-catalog tests that
/// register a fixed-id scene and then assert the directory contains <em>exactly one</em>
/// SceneServer therefore race each other across schemas: the second registration hits the
/// <c>scene_datasets_id_unique</c> constraint, or one test's row leaks into the other's
/// <c>ContainSingle()</c> assertion (or, after a failed/rolled-back peer, the read sees an
/// empty directory). These tests run in their own <c>DisableParallelization</c> collection
/// and truncate the global table around each case so the global registry state is
/// deterministic, without serializing the rest of the parallel catalog shard.
/// </remarks>
[CollectionDefinition("Database.GeoServicesSceneCatalog", DisableParallelization = true)]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1711:Identifiers should not have incorrect suffix", Justification = "This is an xUnit collection definition which requires the Collection suffix")]
public class DatabaseGeoServicesSceneCatalogCollection
{
}
