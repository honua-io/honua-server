// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

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

    public ICogArtifactLocator? CogArtifactLocator { get; }
}
