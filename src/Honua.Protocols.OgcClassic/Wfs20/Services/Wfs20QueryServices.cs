// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Configuration;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Query;
using Honua.Core.Queries.Filters;
using Microsoft.Extensions.Options;

namespace Honua.Protocols.Ogc.Classic.Wfs20.Services;

/// <summary>
/// Feature-local facade that groups the shared read/query collaborators used by the WFS handler.
/// Paired with <see cref="Wfs20EditServices"/> and <see cref="Wfs20SpatialServices"/> so the handler
/// composes a small number of cohesive facades instead of one large aggregate, without changing behavior.
/// </summary>
internal sealed class Wfs20QueryServices(
    IFeatureReader featureReader,
    IGmlFeatureStore gmlFeatureStore,
    IMetadataV2GraphProvider metadataV2GraphProvider,
    IFilterExpressionService filterExpressionService,
    Wfs20QueryParameterAdapter queryParameterAdapter,
    IQueryProcessor queryProcessor,
    IOptions<Wfs20Options> wfs20Options)
{
    internal IFeatureReader FeatureReader { get; } = featureReader;

    internal IGmlFeatureStore GmlFeatureStore { get; } = gmlFeatureStore;

    internal IMetadataV2GraphProvider MetadataV2GraphProvider { get; } = metadataV2GraphProvider;

    internal IFilterExpressionService FilterExpressionService { get; } = filterExpressionService;

    internal Wfs20QueryParameterAdapter QueryParameterAdapter { get; } = queryParameterAdapter;

    internal IQueryProcessor QueryProcessor { get; } = queryProcessor;

    internal Wfs20Options Wfs20Options { get; } = wfs20Options?.Value ?? throw new ArgumentNullException(nameof(wfs20Options));
}
