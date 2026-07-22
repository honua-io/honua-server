// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Abstractions;

namespace Honua.Protocols.GeoServices.FeatureServer.Services;

/// <summary>
/// Backward-compatible FeatureServer wrapper over the shared spatial reference resolver.
/// </summary>
/// <remarks>
/// Intentionally shares its name with <see cref="Honua.Infrastructure.Services.SpatialReferenceResolver"/>
/// (the base class it wraps): this is a namespace-scoped FeatureServer alias kept for existing DI
/// registrations/call sites in this protocol, not an accidental collision or duplicated implementation.
/// </remarks>
// codeql[cs/class-name-matches-base-class] -- the protocol adapter intentionally specializes the shared resolver under its canonical service name.
internal sealed class SpatialReferenceResolver : Honua.Infrastructure.Services.SpatialReferenceResolver
{
    public SpatialReferenceResolver(
        ICrsDetectionService crsDetectionService,
        ICrsRegistry crsRegistry)
        : base(crsDetectionService, crsRegistry)
    {
    }
}
