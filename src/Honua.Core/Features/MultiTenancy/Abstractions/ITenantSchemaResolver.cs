// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.MultiTenancy.Abstractions;

/// <summary>
/// Maps a resolved tenant identifier to the PostgreSQL schema that physically isolates
/// that tenant's data (schema-per-tenant isolation, issue #346).
/// </summary>
/// <remarks>
/// This is the first increment of the schema-per-tenant rail: it deterministically derives
/// (or looks up) a SQL-safe schema name from the tenant id resolved by the tenant context
/// rail (#1144). The resolved schema is fed into the existing request-scoped
/// <see cref="Honua.Core.Features.Infrastructure.Abstractions.ISchemaContext"/> so the proven
/// <c>SET search_path</c> data path enforces isolation — the same primitive used for test
/// schema isolation. Provisioning, onboarding flow, and billing are deferred (Refs #346).
/// </remarks>
public interface ITenantSchemaResolver
{
    /// <summary>
    /// Resolves the database schema name for the supplied tenant id.
    /// </summary>
    /// <param name="tenantId">The tenant identifier resolved for the current request.</param>
    /// <param name="schemaName">
    /// When the method returns <see langword="true"/>, the SQL-safe schema name to route to.
    /// </param>
    /// <returns>
    /// <see langword="true"/> when a valid schema name could be resolved; otherwise
    /// <see langword="false"/> (for example when the tenant id does not map to a safe
    /// identifier or is excluded from routing).
    /// </returns>
    bool TryResolveSchema(string tenantId, out string schemaName);
}
