// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Abstractions.Features.Rasters.Abstractions;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.Geometry.Abstractions;
using Honua.Infrastructure.Filtering;

namespace Honua.Protocols.Stac;

internal sealed class StacSearchDependencies
{
    public StacSearchDependencies(
        IFeatureReader featureReader,
        IGeometryService geometryService,
        Cql2FilterProcessor filterProcessor,
        ILogger<StacEndpoints.StacEndpointsLog> logger,
        ICogArtifactLocator? cogArtifactLocator = null)
    {
        FeatureReader = featureReader ?? throw new ArgumentNullException(nameof(featureReader));
        GeometryService = geometryService ?? throw new ArgumentNullException(nameof(geometryService));
        FilterProcessor = filterProcessor ?? throw new ArgumentNullException(nameof(filterProcessor));
        Logger = logger ?? throw new ArgumentNullException(nameof(logger));
        CogArtifactLocator = cogArtifactLocator;
    }

    public IFeatureReader FeatureReader { get; }
    public IGeometryService GeometryService { get; }
    public Cql2FilterProcessor FilterProcessor { get; }
    public ILogger<StacEndpoints.StacEndpointsLog> Logger { get; }

    /// <summary>
    /// Resolves a layer's published COG so search results advertise it exactly as the
    /// items endpoint does. Optional: absent, search behaves as it did before.
    /// </summary>
    /// <remarks>
    /// Search spans collections, so a caller resolves per layer and should memoise -
    /// each lookup confirms the artifact against storage, and a page can revisit the
    /// same layer many times.
    /// </remarks>
    public ICogArtifactLocator? CogArtifactLocator { get; }
}
