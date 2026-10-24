// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Data.Common;
using Honua.Core.Features.Infrastructure.Domain;

namespace Honua.Core.Features.Infrastructure.Abstractions;

/// <summary>
/// Verifies that migration-owned physical database state agrees with the numbered-migration journal.
/// </summary>
public interface IDatabaseSchemaGuard
{
    /// <summary>
    /// Checks the complete guarded schema floor without applying or repairing schema changes.
    /// </summary>
    /// <param name="connectionString">Connection string for the target database.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes when the schema and journal agree.</returns>
    /// <exception cref="Domain.DatabaseSchemaFloorException">
    /// The physical schema diverges from the migration journal.
    /// </exception>
    Task VerifyAsync(string connectionString, CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks the complete guarded schema floor over an already-open connection without applying
    /// or repairing schema changes.
    /// </summary>
    /// <param name="connection">Open connection to the target database.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes when the complete schema floor and journal agree.</returns>
    /// <exception cref="DatabaseSchemaFloorException">
    /// A required migration is absent or the physical schema diverges from the journal.
    /// </exception>
    Task VerifyAsync(DbConnection connection, CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks the complete guarded schema floor, treating the listed journal entries as qualified
    /// because the migration runner has already re-verified their forward-compatibility declarations
    /// (#6477). Only the migration runner supplies this set. A guard that models forward compatibility
    /// still qualifies any other unknown entry itself, read-only, through the recorded declarations.
    /// </summary>
    /// <param name="connection">Open connection to the target database.</param>
    /// <param name="readerVerifiedMigrations">Unknown journal entries the runner verified as expand-only.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes when the complete schema floor and journal agree.</returns>
    /// <remarks>
    /// The default implementation ignores the set and keeps the strict check, so a guard that does not
    /// model forward compatibility fails closed.
    /// </remarks>
    Task VerifyAsync(
        DbConnection connection,
        IReadOnlySet<string> readerVerifiedMigrations,
        CancellationToken cancellationToken = default)
        => VerifyAsync(connection, cancellationToken);

    /// <summary>
    /// Checks journal/physical-schema consistency while permitting migrations that are genuinely
    /// pending (both their journal row and owned objects are absent).
    /// </summary>
    /// <param name="connection">Open connection to the target database.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes when no partial or contradictory migration state exists.</returns>
    /// <exception cref="DatabaseSchemaFloorException">
    /// The physical schema contradicts the migration journal.
    /// </exception>
    Task VerifyConsistencyAsync(DbConnection connection, CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks journal/physical-schema consistency, treating the listed journal entries as qualified
    /// because the migration runner has already re-verified their forward-compatibility declarations.
    /// </summary>
    /// <param name="connection">Open connection to the target database.</param>
    /// <param name="readerVerifiedMigrations">Unknown journal entries the runner verified as expand-only.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes when no partial or contradictory migration state exists.</returns>
    /// <remarks>The default implementation ignores the set and keeps the strict check (fail closed).</remarks>
    Task VerifyConsistencyAsync(
        DbConnection connection,
        IReadOnlySet<string> readerVerifiedMigrations,
        CancellationToken cancellationToken = default)
        => VerifyConsistencyAsync(connection, cancellationToken);

    /// <summary>
    /// Checks a database that has never been migrated (its journal is empty) whose migration-owned
    /// tables were created up front by a seed, so the pending migrations can adopt them. Every
    /// present migration-owned table must match its creation-time column definitions, constraints
    /// and table properties; existing indexes must also match. Absent tables and indexes, and
    /// later schema alterations, are left to the pending migrations. The complete floor
    /// is still enforced by <see cref="VerifyAsync(DbConnection, CancellationToken)"/> after they run.
    /// On a database whose journal is not empty this is identical to
    /// <see cref="VerifyConsistencyAsync(DbConnection, CancellationToken)"/>.
    /// </summary>
    /// <param name="connection">Open connection to the target database.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes when every present migration-owned table can be adopted.</returns>
    /// <exception cref="DatabaseSchemaFloorException">
    /// A present migration-owned table is incomplete, or the journal contradicts the physical schema.
    /// </exception>
    /// <remarks>
    /// The default implementation adopts nothing and keeps the strict consistency check, so a guard
    /// that does not model adoption fails closed.
    /// </remarks>
    Task VerifyFirstMigrationAdoptionAsync(DbConnection connection, CancellationToken cancellationToken = default)
        => VerifyConsistencyAsync(connection, cancellationToken);

    /// <summary>
    /// Records an already-created raster provider baseline in the migration journal when its tables
    /// match the creation contract and the journal does not yet name that migration. Does not execute
    /// the migration script, drop or alter tables, or report a new apply. A missing, partial, or
    /// non-matching raster schema is not journaled; the floor check stays in force.
    /// </summary>
    /// <param name="connectionString">Connection string for the target database.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// The adopted script name when a journal receipt was recorded; otherwise <see langword="null"/>.
    /// </returns>
    /// <remarks>
    /// The default adopts nothing. A guard that does not model the raster baseline keeps the strict
    /// floor check.
    /// </remarks>
    Task<string?> TryAdoptMatchingRasterBaselineAsync(
        string connectionString,
        CancellationToken cancellationToken = default)
        => Task.FromResult<string?>(null);

    /// <summary>
    /// Checks the migration-owned schema required by one ordinary runtime operation.
    /// </summary>
    /// <param name="connection">Open connection to the target database.</param>
    /// <param name="requirement">Required journaled schema capability.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes when the required migration and physical schema agree.</returns>
    /// <exception cref="DatabaseSchemaFloorException">
    /// The required migration is absent or its physical schema diverges from the journal.
    /// </exception>
    Task VerifyRequirementAsync(
        DbConnection connection,
        DatabaseSchemaRequirement requirement,
        CancellationToken cancellationToken = default);
}
