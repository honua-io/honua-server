// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

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
}
