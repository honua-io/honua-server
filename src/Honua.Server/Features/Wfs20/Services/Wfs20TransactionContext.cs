// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Catalog.Abstractions;
using Honua.Core.Features.FeatureStore.Abstractions;

namespace Honua.Server.Features.Wfs20.Services;

/// <summary>
/// Composite context containing related services for WFS 2.0 transaction processing.
/// Reduces constructor parameter complexity while maintaining clear separation of concerns.
/// </summary>
internal sealed class Wfs20TransactionContext
{
    public Wfs20TransactionContext(
        IFeatureWriter featureWriter,
        ILayerCatalog layerCatalog,
        IWfs20FeatureFormatConverter formatConverter)
    {
        FeatureWriter = featureWriter ?? throw new ArgumentNullException(nameof(featureWriter));
        LayerCatalog = layerCatalog ?? throw new ArgumentNullException(nameof(layerCatalog));
        FormatConverter = formatConverter ?? throw new ArgumentNullException(nameof(formatConverter));
    }

    public IFeatureWriter FeatureWriter { get; }
    public ILayerCatalog LayerCatalog { get; }
    public IWfs20FeatureFormatConverter FormatConverter { get; }
}