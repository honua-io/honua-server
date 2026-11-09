// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Infrastructure.Domain;

public enum DatabaseSchemaRequirement
{
    RasterLayerStatistics = 0,

    MetadataV2Snapshot = 1,

    RasterExternalStorage = 2,

    SensorThings = 3,

    MetadataV2ReleasePackages = 4,
}

public enum DatabaseSchemaFloorFailureKind
{
    MigrationNotApplied = 0,

    // The journal claims the migration ran, but its required physical schema is absent or incomplete.
    JournalClaimsMissingSchema = 1,

    SchemaExistsWithoutJournal = 2,
}

// Callers must run the canonical migration; an ordinary read or write must not repair the schema.
public sealed class DatabaseSchemaFloorException : InvalidOperationException
{
    public DatabaseSchemaFloorException(
        string migrationScript,
        DatabaseSchemaFloorFailureKind failureKind,
        string detail)
        : base(BuildMessage(migrationScript, failureKind, detail))
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(migrationScript);
        ArgumentException.ThrowIfNullOrWhiteSpace(detail);

        MigrationScript = migrationScript;
        FailureKind = failureKind;
        Detail = detail;
    }

    public string MigrationScript { get; }

    public DatabaseSchemaFloorFailureKind FailureKind { get; }

    public string Detail { get; }

    private static string BuildMessage(
        string migrationScript,
        DatabaseSchemaFloorFailureKind failureKind,
        string detail)
        => $"Database schema floor check failed for migration '{migrationScript}' ({failureKind}): {detail} " +
           "The core schema is migration-owned and will not be repaired by an ordinary store operation.";
}
