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
// concurrently without clobbering each other. With that guarantee in place the
// database-backed tests are safe to run under multiple parallel collections.
//
// These sibling collections all share the same DatabaseFixtureAdapter fixture type.
// PostgresFixture keeps a single ref-counted, process-wide PostGIS container, so
// using several collections does NOT spin up several containers — it only unlocks
// xUnit cross-collection parallelism. Classes are assigned to a collection by CI
// shard area so the previously-serial Core, GeoServices ImageServer, and OGC API
// Maps/Tiles shards parallelize within their own filtered test set.

/// <summary>
/// Database-backed collection for Core-shard feature-store / query / CRS tests.
/// </summary>
[CollectionDefinition("Database.CoreFeatureStore")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1711:Identifiers should not have incorrect suffix", Justification = "This is an xUnit collection definition which requires the Collection suffix")]
public class DatabaseCoreFeatureStoreCollection : ICollectionFixture<DatabaseFixtureAdapter>
{
}

/// <summary>
/// Database-backed collection for Core-shard protocol-endpoint and seed tests.
/// </summary>
[CollectionDefinition("Database.CoreEndpoints")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1711:Identifiers should not have incorrect suffix", Justification = "This is an xUnit collection definition which requires the Collection suffix")]
public class DatabaseCoreEndpointsCollection : ICollectionFixture<DatabaseFixtureAdapter>
{
}

/// <summary>
/// Database-backed collection for Core-shard spatial-correctness tests.
/// </summary>
[CollectionDefinition("Database.CoreSpatial")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1711:Identifiers should not have incorrect suffix", Justification = "This is an xUnit collection definition which requires the Collection suffix")]
public class DatabaseCoreSpatialCollection : ICollectionFixture<DatabaseFixtureAdapter>
{
}
