// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.FeatureStore.Services;

/// <summary>
/// Read-side feature provider operations that participate in capability checks.
/// </summary>
public enum FeatureProviderReadOperation
{
    /// <summary>
    /// General feature query/read operation.
    /// </summary>
    Query,

    /// <summary>
    /// Feature count operation.
    /// </summary>
    Count,

    /// <summary>
    /// Spatial extent operation.
    /// </summary>
    Extent,

    /// <summary>
    /// Aggregate statistics operation.
    /// </summary>
    Statistics
}
