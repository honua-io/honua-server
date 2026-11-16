// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Db.Postgres.Features.Infrastructure.Migrations;

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
        string? initialSchemaMigration = null,
        string? sensorThingsIdSequencesMigration = null,
        string? sensorThingsSensingMigration = null)
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
        SensorThingsIdSequencesMigration = sensorThingsIdSequencesMigration;
        SensorThingsSensingMigration = sensorThingsSensingMigration;
    }

    public string ApplicationMigrationAssemblyName { get; }

    public PostgresAttachmentMigrationContract? Attachments { get; init; }

    public IReadOnlySet<string>? KnownMigrationNames { get; init; }

    public string MetadataV2SnapshotMigration { get; }

    public string MetadataV2ReleasePackagesMigration { get; }

    public string RasterExternalStorageMigration { get; }

    public string SensorThingsMigration { get; }

    public string RasterOverviewsMigration { get; }

    public string RasterFootprintsMigration { get; }

    public string ConfiguredSchemaAdoptionMigration { get; }

    public string GovernedLineageMigration { get; }

    public string? InitialSchemaMigration { get; }

    public string? SensorThingsIdSequencesMigration { get; }

    public string? SensorThingsSensingMigration { get; }
}
