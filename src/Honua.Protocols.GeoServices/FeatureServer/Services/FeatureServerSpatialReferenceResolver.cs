// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Abstractions;

namespace Honua.Protocols.GeoServices.FeatureServer.Services;

/// <summary>
/// FeatureServer wrapper over the shared spatial reference resolver.
/// </summary>
internal sealed class FeatureServerSpatialReferenceResolver : Honua.Infrastructure.Services.SpatialReferenceResolver
{
    public FeatureServerSpatialReferenceResolver(
        ICrsDetectionService crsDetectionService,
        ICrsRegistry crsRegistry)
        : base(crsDetectionService, crsRegistry)
    {
    }
}
