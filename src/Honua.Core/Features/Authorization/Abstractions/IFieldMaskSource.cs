// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Core.Features.Metadata.Domain.V2;

namespace Honua.Core.Features.Authorization.Abstractions;

/// <summary>
/// Request-scoped source of the field-level security (column masking) set (#1940) for a
/// resource. Resolved per query from the current principal's roles and the layer's
/// <see cref="Domain.FieldMaskPolicy"/> set, the returned attribute names are dropped from
/// the projected attributes inside the feature store so the same masking rules apply across
/// every query protocol (GeoServices, OGC, OData) and output format. This is the field-level
/// companion to <see cref="IRowLevelSecurityFilterSource"/>.
/// </summary>
/// <remarks>
/// An empty result means "no masking applies" (the principal/role has no matching policy).
/// Masking is enforced server-side at the shared projection seam, so a masked attribute is
/// removed even when the caller explicitly requests it via <c>outFields</c> /
/// <c>$select</c> / <c>properties</c> — masking is fail-secure.
/// </remarks>
public interface IFieldMaskSource
{
    /// <summary>
    /// Resolves the set of attribute (field) names to mask for the current request
    /// against <paramref name="resource"/>, or an empty set when no policy applies.
    /// Field names are compared case-insensitively by the enforcing provider.
    /// </summary>
    /// <param name="resource">The resource (layer) being queried.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<ImmutableArray<string>> ResolveAsync(MetadataV2Resource resource, CancellationToken cancellationToken = default);
}
