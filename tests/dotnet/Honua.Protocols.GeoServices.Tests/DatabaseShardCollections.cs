// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.TestKit;

namespace Honua.Protocols.GeoServices.Tests;

// Parallelism note (#1359): see the matching file in Honua.Server.Tests for the full
// rationale. xUnit parallelizes ACROSS collections only. The GeoServices ImageServer CI
// shard (ImageServer + GPServer + GeometryService + Catalog + NAServer) was serialized by
// the single [Collection("Database")]. The metadata-v2 graph is now schema-partitioned, so
// audited schema-isolated classes are opted in to these parallel collections.
//
// OPT-IN safety rule: a class is parallelized only when it either (a) runs against an
// isolated, per-test WebAppFixture server with a mocked IRasterStore (GeometryService,
// most ImageServer endpoint/error/validation/metadata tests, Catalog), or (b) issues only
// schema-isolated reads / stateless computations against the shared server (NAServer route
// solve). Classes that mutate shared GLOBAL `honua.*` rows are kept serial:
//   - ImageServerMosaicIntegrationTests seeds the global honua.raster_data table
//     (RasterIntegrationTestData) keyed by a fixed layer id and would race other raster
//     tests.
//   - GPServer*Tests that submit GP jobs through CreateAdminClient touch shared job/runtime
//     state and stay serial.
//
// All collections share the TestKit PostgresFixture (single ref-counted, process-wide
// PostGIS container) so parallelism adds no extra containers; schema names are globally
// unique so concurrent schemas cannot collide.

/// <summary>
/// Parallel ImageServer-shard collection (bucket 1) for audited schema-isolated tests.
/// </summary>
[CollectionDefinition("Database.GeoServicesParallel1")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1711:Identifiers should not have incorrect suffix", Justification = "This is an xUnit collection definition which requires the Collection suffix")]
public class DatabaseGeoServicesParallel1Collection : ICollectionFixture<PostgresFixture>
{
}

/// <summary>
/// Parallel ImageServer-shard collection (bucket 2) for audited schema-isolated tests.
/// </summary>
[CollectionDefinition("Database.GeoServicesParallel2")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1711:Identifiers should not have incorrect suffix", Justification = "This is an xUnit collection definition which requires the Collection suffix")]
public class DatabaseGeoServicesParallel2Collection : ICollectionFixture<PostgresFixture>
{
}

/// <summary>
/// Parallel ImageServer-shard collection (bucket 3) for audited schema-isolated tests.
/// </summary>
[CollectionDefinition("Database.GeoServicesParallel3")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1711:Identifiers should not have incorrect suffix", Justification = "This is an xUnit collection definition which requires the Collection suffix")]
public class DatabaseGeoServicesParallel3Collection : ICollectionFixture<PostgresFixture>
{
}

/// <summary>
/// Parallel ImageServer-shard collection (bucket 4) for audited schema-isolated tests.
/// </summary>
[CollectionDefinition("Database.GeoServicesParallel4")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1711:Identifiers should not have incorrect suffix", Justification = "This is an xUnit collection definition which requires the Collection suffix")]
public class DatabaseGeoServicesParallel4Collection : ICollectionFixture<PostgresFixture>
{
}
