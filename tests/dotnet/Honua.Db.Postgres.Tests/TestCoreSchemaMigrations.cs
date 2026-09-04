// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Db.Postgres.Features.Infrastructure.Migrations;

namespace Honua.Db.Postgres.Tests;

internal static class TestCoreSchemaMigrations
{
    internal static readonly PostgresCoreSchemaMigrationManifest Manifest = new(
        "Honua.Server",
        "Honua.Server.Migrations.031_CreateMetadataV2Snapshot.sql",
        "Honua.Server.Migrations.034_CreateMetadataV2ReleasePackages.sql",
        "Honua.Server.Migrations.055_SetRasterDataExternalStorage.sql",
        "Honua.Server.Migrations.059_CreateSensorThings.sql",
        "Honua.Server.Migrations.063_CreateRasterOverviews.sql",
        "Honua.Server.Migrations.064_CreateRasterFootprints.sql",
        "Honua.Server.Migrations.109_AdoptConfiguredGuardedSchema.sql",
        "Honua.Server.Migrations.110_PreserveGovernedLineage.sql");
}
