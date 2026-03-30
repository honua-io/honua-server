// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Catalog.Abstractions;
using Honua.Core.Queries.Filters;
using Honua.Server.Features.Infrastructure.Events;
using Microsoft.Extensions.Options;

namespace Honua.Server.Features.Streaming;

/// <summary>
/// Dependency bundle for feature stream endpoints. Keeps the endpoint method
/// signature within the 5-dependency limit (follows <c>FeatureServerEditsDependencies</c> pattern).
/// </summary>
internal sealed class FeatureStreamDependencies
{
    public FeatureStreamDependencies(
        FeatureStreamSessionManager sessionManager,
        IFeatureChangeEventStore eventStore,
        IOptions<FeatureStreamOptions> options,
        IFilterExpressionService filterExpressionService,
        ILayerCatalog layerCatalog)
    {
        SessionManager = sessionManager ?? throw new ArgumentNullException(nameof(sessionManager));
        EventStore = eventStore ?? throw new ArgumentNullException(nameof(eventStore));
        Options = options ?? throw new ArgumentNullException(nameof(options));
        FilterExpressionService = filterExpressionService ?? throw new ArgumentNullException(nameof(filterExpressionService));
        LayerCatalog = layerCatalog ?? throw new ArgumentNullException(nameof(layerCatalog));
    }

    public FeatureStreamSessionManager SessionManager { get; }
    public IFeatureChangeEventStore EventStore { get; }
    public IOptions<FeatureStreamOptions> Options { get; }
    public IFilterExpressionService FilterExpressionService { get; }
    public ILayerCatalog LayerCatalog { get; }
}
