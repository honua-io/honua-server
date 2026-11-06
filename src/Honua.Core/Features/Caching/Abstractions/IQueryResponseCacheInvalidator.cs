// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Caching.Abstractions;

/// <summary>
/// Invalidates shared query-response cache namespaces after a change that can
/// alter query results across services.
/// </summary>
public interface IQueryResponseCacheInvalidator
{
    /// <summary>
    /// Bumps the FeatureServer, OGC, and OData query-response namespaces so later
    /// reads miss entries stored before the change.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task InvalidateQueryResponsesAsync(CancellationToken cancellationToken = default);
}
