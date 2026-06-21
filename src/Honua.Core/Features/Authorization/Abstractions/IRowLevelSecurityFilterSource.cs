// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Queries.Filters;

namespace Honua.Core.Features.Authorization.Abstractions;

/// <summary>
/// Request-scoped source of the row-level security (RLS) predicate (#502) for a
/// resource. Resolved per query from the current principal's roles/claims and the
/// layer's <see cref="Domain.RlsPolicy"/> set, the returned fragment is AND-ed into the
/// provider's enforced WHERE clause alongside any metadata permanent filter so the same
/// row-visibility rules apply across every query protocol (GeoServices, OGC, OData).
/// </summary>
/// <remarks>
/// The fragment is always parameterized: attribute names are validated against the
/// resource schema and claim values are bound as query parameters, so the predicate is
/// injection-safe regardless of claim contents. A <see langword="null"/> result means
/// "no RLS applies" (the principal/role has no matching policy); a fragment that
/// resolves to <c>FALSE</c> means "matching policy but no permitted rows" (fail-secure).
/// </remarks>
public interface IRowLevelSecurityFilterSource
{
    /// <summary>
    /// Resolves the enforced RLS predicate for the current request against
    /// <paramref name="resource"/>, or <see langword="null"/> when no policy applies.
    /// </summary>
    /// <param name="resource">The resource (layer) being queried.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<SqlFragment?> ResolveAsync(MetadataV2Resource resource, CancellationToken cancellationToken = default);
}
