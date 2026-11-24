// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Db.Postgres.Features.Infrastructure.Migrations;

namespace Honua.Server.Startup;

internal static class ServerCoreSchemaMigrations
{
    internal static readonly PostgresCoreSchemaMigrationManifest Manifest = new(
        typeof(ServerCoreSchemaMigrations).Assembly.GetName().Name!,
        "Honua.Server.Migrations.031_CreateMetadataV2Snapshot.sql",
        "Honua.Server.Migrations.034_CreateMetadataV2ReleasePackages.sql",
        "Honua.Server.Migrations.055_SetRasterDataExternalStorage.sql",
        "Honua.Server.Migrations.059_CreateSensorThings.sql",
        "Honua.Server.Migrations.063_CreateRasterOverviews.sql",
        "Honua.Server.Migrations.064_CreateRasterFootprints.sql",
        "Honua.Server.Migrations.109_AdoptConfiguredGuardedSchema.sql",
        "Honua.Server.Migrations.110_PreserveGovernedLineage.sql",
        "Honua.Server.Migrations.001_CreateHonuaSchema.sql",
        "Honua.Server.Migrations.116_AddSensorThingsIdSequences.sql",
        "Honua.Server.Migrations.127_CompleteSensorThingsSensing.sql")
    {
        ConfiguredRegistryAdoptionMigration = "Honua.Server.Migrations.227_AdoptConfiguredRegistrySchema.sql",
        ConfiguredFeatureStoreAdoptionMigration = "Honua.Server.Migrations.231_AdoptConfiguredFeatureStoreSchema.sql",
        Attachments = new(
            "Honua.Server.Migrations.002_CreateAttachmentsTable.sql",
            "Honua.Server.Migrations.125_AddImportedAttachmentIdentity.sql",
            "Honua.Server.MigrationContracts.attachment-schema-preamble.v1.sql.template",
            "1695edc30a7aac84af47adb03b3d9a4028cdf811d57af9b91c6778ad0195f102"),
        KnownMigrationNames = typeof(ServerCoreSchemaMigrations).Assembly.GetManifestResourceNames()
            .Concat(typeof(PostgresDatabaseMigrationRunner).Assembly.GetManifestResourceNames())
            .Where(name => name.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
            .ToHashSet(StringComparer.Ordinal),
        ResidentTables =
        [
            new HonuaResidentTableBinding(
                "Honua.Server.Migrations.157_FeatureChangeRealtimeStream.sql",
                "feature_changes",
                "feature_change_outbox"),
            new HonuaResidentTableBinding(
                "Honua.Server.Migrations.199_AddAnalysisContentTenantOwnership.sql",
                "analysis_content_items"),
            new HonuaResidentTableBinding(
                "Honua.Server.Migrations.215_AddConsoleAccessEnvironmentOwnership.sql",
                "rbac_role_permissions")
        ]
    };
}
