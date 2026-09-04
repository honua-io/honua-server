// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Db.Postgres.Features.Infrastructure.Migrations;

/// <summary>
/// Exact public-journal identities for application-owned migrations whose physical effects
/// the PostgreSQL guard verifies. The application composition root owns these values so the
/// provider never depends on an application assembly or namespace.
/// </summary>
internal sealed class PostgresCoreSchemaMigrationManifest
{
    public PostgresCoreSchemaMigrationManifest(
        string applicationMigrationAssemblyName,
        string metadataV2SnapshotMigration,
        string metadataV2ReleasePackagesMigration,
        string rasterExternalStorageMigration,
        string sensorThingsMigration,
        string rasterOverviewsMigration,
        string rasterFootprintsMigration,
        string configuredSchemaAdoptionMigration,
        string governedLineageMigration,
        string? initialSchemaMigration = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationMigrationAssemblyName);
        ArgumentException.ThrowIfNullOrWhiteSpace(metadataV2SnapshotMigration);
        ArgumentException.ThrowIfNullOrWhiteSpace(metadataV2ReleasePackagesMigration);
        ArgumentException.ThrowIfNullOrWhiteSpace(rasterExternalStorageMigration);
        ArgumentException.ThrowIfNullOrWhiteSpace(sensorThingsMigration);
        ArgumentException.ThrowIfNullOrWhiteSpace(rasterOverviewsMigration);
        ArgumentException.ThrowIfNullOrWhiteSpace(rasterFootprintsMigration);
        ArgumentException.ThrowIfNullOrWhiteSpace(configuredSchemaAdoptionMigration);
        ArgumentException.ThrowIfNullOrWhiteSpace(governedLineageMigration);

        ApplicationMigrationAssemblyName = applicationMigrationAssemblyName;
        MetadataV2SnapshotMigration = metadataV2SnapshotMigration;
        MetadataV2ReleasePackagesMigration = metadataV2ReleasePackagesMigration;
        RasterExternalStorageMigration = rasterExternalStorageMigration;
        SensorThingsMigration = sensorThingsMigration;
        RasterOverviewsMigration = rasterOverviewsMigration;
        RasterFootprintsMigration = rasterFootprintsMigration;
        ConfiguredSchemaAdoptionMigration = configuredSchemaAdoptionMigration;
        GovernedLineageMigration = governedLineageMigration;
        InitialSchemaMigration = initialSchemaMigration;
    }

    public string ApplicationMigrationAssemblyName { get; }

    public string MetadataV2SnapshotMigration { get; }

    public string MetadataV2ReleasePackagesMigration { get; }

    public string RasterExternalStorageMigration { get; }

    public string SensorThingsMigration { get; }

    public string RasterOverviewsMigration { get; }

    public string RasterFootprintsMigration { get; }

    public string ConfiguredSchemaAdoptionMigration { get; }

    public string GovernedLineageMigration { get; }

    /// <summary>
    /// Journal identity for the foundational Honua metadata schema. Older synthetic manifests
    /// may omit this because they intentionally exercise only provider-owned floors.
    /// </summary>
    public string? InitialSchemaMigration { get; }
}
