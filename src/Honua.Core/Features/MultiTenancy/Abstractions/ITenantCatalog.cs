// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.MultiTenancy.Domain;

namespace Honua.Core.Features.MultiTenancy.Abstractions;

/// <summary>
/// Durable registry of provisioned tenants and their lifecycle state (issue #2156).
/// </summary>
/// <remarks>
/// The catalog is the persistence seam consumed by the tenant lifecycle service and the
/// tenant-status enforcement middleware. The shipped implementation is process-local/in-memory;
/// a durable, cross-node implementation (schema/migration backed) can replace it without changing
/// callers, mirroring how the rate-limit policy store and usage meter ship today.
/// </remarks>
public interface ITenantCatalog
{
    /// <summary>Gets a tenant by id, or <see langword="null"/> when it is not provisioned.</summary>
    /// <param name="tenantId">The tenant identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<TenantRecord?> GetAsync(string tenantId, CancellationToken cancellationToken = default);

    /// <summary>Lists all provisioned tenants.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<IReadOnlyList<TenantRecord>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds a new tenant. Returns <see langword="false"/> when a tenant with the same id already
    /// exists (the caller maps this to a conflict).
    /// </summary>
    /// <param name="tenant">The tenant to add.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<bool> TryAddAsync(TenantRecord tenant, CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces an existing tenant record. Returns the stored record, or <see langword="null"/>
    /// when no tenant with that id exists.
    /// </summary>
    /// <param name="tenant">The updated tenant record.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<TenantRecord?> UpdateAsync(TenantRecord tenant, CancellationToken cancellationToken = default);
}
