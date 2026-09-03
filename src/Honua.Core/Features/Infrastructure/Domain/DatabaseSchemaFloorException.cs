// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Infrastructure.Domain;

/// <summary>
/// Identifies a migration-owned core-schema capability required by a runtime operation.
/// </summary>
public enum DatabaseSchemaRequirement
{
    /// <summary>Persisted layer-level raster statistics owned by provider migration 003.</summary>
    RasterLayerStatistics = 0,

    /// <summary>Metadata v2 snapshots and lookup projections owned by server migration 031.</summary>
    MetadataV2Snapshot = 1,

    /// <summary>External TOAST storage policy owned by server migration 055.</summary>
    RasterExternalStorage = 2,

    /// <summary>SensorThings catalog and observation storage owned by server migration 059.</summary>
    SensorThings = 3,

    /// <summary>Metadata v2 release-package storage owned by server migration 034.</summary>
    MetadataV2ReleasePackages = 4,
}

/// <summary>
/// Identifies why the live database schema cannot be trusted against the migration journal.
/// </summary>
public enum DatabaseSchemaFloorFailureKind
{
    /// <summary>The runtime requires a numbered migration that is not journaled as applied.</summary>
    MigrationNotApplied = 0,

    /// <summary>The journal claims a migration ran, but its required physical schema is absent or incomplete.</summary>
    JournalClaimsMissingSchema = 1,

    /// <summary>Migration-owned physical schema exists even though the migration is not journaled.</summary>
    SchemaExistsWithoutJournal = 2,
}

/// <summary>
/// Raised when a core store's required schema floor is absent or the physical schema diverges from
/// the numbered-migration journal. This failure is terminal: callers must run or reconcile the
/// canonical migration rather than repairing schema from an ordinary read or write path.
/// </summary>
public sealed class DatabaseSchemaFloorException : InvalidOperationException
{
    /// <summary>
    /// Initializes a schema-floor failure.
    /// </summary>
    /// <param name="migrationScript">Numbered migration that owns the required schema.</param>
    /// <param name="failureKind">Kind of journal/physical-schema mismatch.</param>
    /// <param name="detail">Sanitized description of the missing or unexpected physical state.</param>
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

    /// <summary>Numbered migration that owns the required schema.</summary>
    public string MigrationScript { get; }

    /// <summary>Kind of journal/physical-schema mismatch.</summary>
    public DatabaseSchemaFloorFailureKind FailureKind { get; }

    /// <summary>Sanitized description of the missing or unexpected physical state.</summary>
    public string Detail { get; }

    private static string BuildMessage(
        string migrationScript,
        DatabaseSchemaFloorFailureKind failureKind,
        string detail)
        => $"Database schema floor check failed for migration '{migrationScript}' ({failureKind}): {detail} " +
           "The core schema is migration-owned and will not be repaired by an ordinary store operation.";
}
