// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

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
