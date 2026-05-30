// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Configuration;
using Honua.Core.Features.Edit;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Query;
using Honua.Core.Queries.Filters;
using Honua.Server.Features.Infrastructure.Events;
using Honua.Server.Features.Infrastructure.Validation;
using Honua.Server.Features.Protocols.Ogc.Api.Features;
using Honua.Server.Features.Protocols.Ogc.Api.Features.Services;
using Microsoft.Extensions.Options;

namespace Honua.Server.Features.Protocols.Ogc.Classic.Wfs20.Services;

/// <summary>
/// Feature-local facade that groups the shared query services used by the WFS handler.
/// Keeps the handler constructor within the architecture test limit without changing behavior.
/// </summary>
internal sealed class Wfs20QueryServices(
    IFeatureReader featureReader,
    IFeatureWriter featureWriter,
    IGmlFeatureStore gmlFeatureStore,
    IMetadataV2GraphProvider metadataV2GraphProvider,
    IFilterExpressionService filterExpressionService,
    Wfs20QueryParameterAdapter queryParameterAdapter,
    IQueryProcessor queryProcessor,
    Wfs20EditParameterAdapter editParameterAdapter,
    IEditProcessor editProcessor,
    OgcFeaturesGeometryServices geometryServices,
    FeatureMutationValidator mutationValidator,
    FeatureMutationEventService mutationEventService,
    ICoordinateTransformService coordinateTransformService,
    ICrsRegistry crsRegistry,
    IOptions<Wfs20Options> wfs20Options,
    IOptions<LimitsOptions> limitsOptions)
{
    internal IFeatureReader FeatureReader { get; } = featureReader;

    internal IFeatureWriter FeatureWriter { get; } = featureWriter;

    internal IGmlFeatureStore GmlFeatureStore { get; } = gmlFeatureStore;

    internal IMetadataV2GraphProvider MetadataV2GraphProvider { get; } = metadataV2GraphProvider;

    internal IFilterExpressionService FilterExpressionService { get; } = filterExpressionService;

    internal Wfs20QueryParameterAdapter QueryParameterAdapter { get; } = queryParameterAdapter;

    internal IQueryProcessor QueryProcessor { get; } = queryProcessor;

    internal Wfs20EditParameterAdapter EditParameterAdapter { get; } = editParameterAdapter;

    internal IEditProcessor EditProcessor { get; } = editProcessor;

    internal OgcFeaturesGeometryServices GeometryServices { get; } = geometryServices;

    internal FeatureMutationValidator MutationValidator { get; } = mutationValidator;

    internal FeatureMutationEventService MutationEventService { get; } = mutationEventService;

    internal ICoordinateTransformService CoordinateTransformService { get; } = coordinateTransformService;

    internal ICrsRegistry CrsRegistry { get; } = crsRegistry;

    internal Wfs20Options Wfs20Options { get; } = wfs20Options?.Value ?? throw new ArgumentNullException(nameof(wfs20Options));

    internal EditLimits EditLimits { get; } = limitsOptions?.Value?.Edits ?? throw new ArgumentNullException(nameof(limitsOptions));
}
