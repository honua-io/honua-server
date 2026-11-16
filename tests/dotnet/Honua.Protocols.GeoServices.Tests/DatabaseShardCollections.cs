// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.TestKit;
using Xunit;

namespace Honua.Protocols.GeoServices.Tests;

// Parallelism note:
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
// These collections exist mainly to give xUnit distinct, parallelizable collection
// names. The MapServer collection also holds a default WebAppFixture reference so its
// serial default-host cases reuse the shared server instead of rebuilding it per case;
// each case still creates its own WebAppFixture and schema.

[CollectionDefinition("Database", DisableParallelization = true)]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1711:Identifiers should not have incorrect suffix", Justification = "This is an xUnit collection definition which requires the Collection suffix")]
public class DatabaseGeoServicesSerialCollection
{
}

[CollectionDefinition("Database.GeoServicesRaster")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1711:Identifiers should not have incorrect suffix", Justification = "This is an xUnit collection definition which requires the Collection suffix")]
public class DatabaseGeoServicesRasterCollection
{
}

[CollectionDefinition("Database.GeoServicesMapServer")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1711:Identifiers should not have incorrect suffix", Justification = "This is an xUnit collection definition which requires the Collection suffix")]
public class DatabaseGeoServicesMapServerCollection : ICollectionFixture<WebAppFixture>
{
}

[CollectionDefinition("Database.GeoServicesCatalog")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1711:Identifiers should not have incorrect suffix", Justification = "This is an xUnit collection definition which requires the Collection suffix")]
public class DatabaseGeoServicesCatalogCollection
{
}

[CollectionDefinition("Database.GeoServicesSceneCatalog", DisableParallelization = true)]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1711:Identifiers should not have incorrect suffix", Justification = "This is an xUnit collection definition which requires the Collection suffix")]
public class DatabaseGeoServicesSceneCatalogCollection
{
}
