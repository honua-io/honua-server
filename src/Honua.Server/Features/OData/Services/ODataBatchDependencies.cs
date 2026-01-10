// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Configuration;
using Honua.Core.Features.Catalog.Abstractions;
using Honua.Core.Features.Geometry.Abstractions;
using Honua.Core.Features.FeatureStore.Abstractions;

namespace Honua.Server.Features.OData.Services;

/// <summary>
/// Aggregates OData batch dependencies to keep handler constructor limits small.
/// </summary>
internal sealed class ODataBatchDependencies
{
    public ODataBatchDependencies(
        ILayerCatalog layerCatalog,
        IFeatureReader featureReader,
        IFeatureWriter featureWriter,
        IGeometryValidator geometryValidator,
        EditLimits editLimits)
    {
        LayerCatalog = layerCatalog ?? throw new ArgumentNullException(nameof(layerCatalog));
        FeatureReader = featureReader ?? throw new ArgumentNullException(nameof(featureReader));
        FeatureWriter = featureWriter ?? throw new ArgumentNullException(nameof(featureWriter));
        GeometryValidator = geometryValidator ?? throw new ArgumentNullException(nameof(geometryValidator));
        EditLimits = editLimits ?? throw new ArgumentNullException(nameof(editLimits));
    }

    public ILayerCatalog LayerCatalog { get; }

    public IFeatureReader FeatureReader { get; }

    public IFeatureWriter FeatureWriter { get; }

    public IGeometryValidator GeometryValidator { get; }

    public EditLimits EditLimits { get; }
}
