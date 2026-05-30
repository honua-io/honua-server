// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Abstractions;

namespace Honua.Server.Features.Protocols.GeoServices.FeatureServer.Services;

/// <summary>
/// Backward-compatible FeatureServer wrapper over the shared spatial reference resolver.
/// </summary>
internal sealed class SpatialReferenceResolver : Infrastructure.Services.SpatialReferenceResolver
{
    public SpatialReferenceResolver(
        ICrsDetectionService crsDetectionService,
        ICrsRegistry crsRegistry)
        : base(crsDetectionService, crsRegistry)
    {
    }
}
