// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Caching.Abstractions;

public interface IQueryResponseCacheInvalidator
{
    // This bumps the FeatureServer, OGC, and OData query-response namespaces so later reads miss entries stored before the change.
    Task InvalidateQueryResponsesAsync(CancellationToken cancellationToken = default);
}
