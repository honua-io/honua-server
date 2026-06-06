// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Server.Tests.Infrastructure;

namespace Honua.Server.Tests;

// Parallelism note (#1359):
//
// xUnit parallelizes ACROSS collections, never within a single collection. The
// historical single [Collection("Database")] therefore forced every database-backed
// Server test to run serially, even though each test already gets its own isolated
// PostgreSQL schema (via PostgresFixture.CreateIsolatedSchemaAsync) and the shared
// in-memory test server is schema-routed per request (X-Honua-Test-Schema header).
//
// The metadata-v2 graph — the one piece of shared, mutable, process-wide singleton
// state on the shared test server — is now partitioned by schema in
// TestMetadataV2GraphProvider, so two schema-isolated tests can mutate metadata
// concurrently without clobbering each other.
//
// IMPORTANT — this is OPT-IN parallelism. A class is moved into one of these parallel
// collections ONLY if it has been audited to be fully schema-isolated: it operates
// solely within its own per-test PostgreSQL schema (feature-store query/edit, OGC API
// Features query, spatial queries, CRS, attachments) and the per-fixture
// schema-partitioned in-memory metadata graph, and it NEVER mutates shared, global
// `honua.*` catalog rows (honua.layers / honua.services / honua.raster_data /
// honua.secure_connections / metadata-v2 snapshots) or publishes through the admin API.
// Any class that does is left in the serial [Collection("Database")] collection. The
// prior broad split (commit 85b3d11e, reverted) parallelized admin-publish and global
// catalog-mutating tests and deadlocked with 40P01; the classification below is the fix.
//
// These sibling collections all share the same DatabaseFixtureAdapter fixture type.
// PostgresFixture keeps a single ref-counted, process-wide PostGIS container, so using
// several collections does NOT spin up several containers — it only unlocks xUnit
// cross-collection parallelism. Schema names are globally unique (per-process counter +
// GUID), so concurrent collections cannot collide on schema-qualified objects.
//
// A small fixed number of buckets (rather than one-collection-per-class) bounds the
// concurrent database connection pressure against the shared container while still
// removing the serial barrier for the audited classes.

/// <summary>
/// Parallel Core-shard collection (bucket 1) for audited schema-isolated tests.
/// </summary>
[CollectionDefinition("Database.CoreParallel1")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1711:Identifiers should not have incorrect suffix", Justification = "This is an xUnit collection definition which requires the Collection suffix")]
public class DatabaseCoreParallel1Collection : ICollectionFixture<DatabaseFixtureAdapter>
{
}

/// <summary>
/// Parallel Core-shard collection (bucket 2) for audited schema-isolated tests.
/// </summary>
[CollectionDefinition("Database.CoreParallel2")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1711:Identifiers should not have incorrect suffix", Justification = "This is an xUnit collection definition which requires the Collection suffix")]
public class DatabaseCoreParallel2Collection : ICollectionFixture<DatabaseFixtureAdapter>
{
}

/// <summary>
/// Parallel Core-shard collection (bucket 3) for audited schema-isolated tests.
/// </summary>
[CollectionDefinition("Database.CoreParallel3")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1711:Identifiers should not have incorrect suffix", Justification = "This is an xUnit collection definition which requires the Collection suffix")]
public class DatabaseCoreParallel3Collection : ICollectionFixture<DatabaseFixtureAdapter>
{
}

/// <summary>
/// Parallel Core-shard collection (bucket 4) for audited schema-isolated tests.
/// </summary>
[CollectionDefinition("Database.CoreParallel4")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1711:Identifiers should not have incorrect suffix", Justification = "This is an xUnit collection definition which requires the Collection suffix")]
public class DatabaseCoreParallel4Collection : ICollectionFixture<DatabaseFixtureAdapter>
{
}
