// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Catalog.Abstractions;
using Honua.Core.Features.GeometryService.Abstractions;
using Honua.Core.Features.Licensing.Abstractions;
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
        IOptions<FeatureChangeEventOptions> eventOptions,
        IFilterExpressionService filterExpressionService,
        ILayerCatalog layerCatalog,
        IGeometryOperationService geometryOperationService,
        ILicenseStatusProvider licenseStatusProvider)
    {
        SessionManager = sessionManager ?? throw new ArgumentNullException(nameof(sessionManager));
        EventStore = eventStore ?? throw new ArgumentNullException(nameof(eventStore));
        Options = options ?? throw new ArgumentNullException(nameof(options));
        EventOptions = eventOptions ?? throw new ArgumentNullException(nameof(eventOptions));
        FilterExpressionService = filterExpressionService ?? throw new ArgumentNullException(nameof(filterExpressionService));
        LayerCatalog = layerCatalog ?? throw new ArgumentNullException(nameof(layerCatalog));
        GeometryOperationService = geometryOperationService ?? throw new ArgumentNullException(nameof(geometryOperationService));
        LicenseStatusProvider = licenseStatusProvider ?? throw new ArgumentNullException(nameof(licenseStatusProvider));
    }

    public FeatureStreamSessionManager SessionManager { get; }
    public IFeatureChangeEventStore EventStore { get; }
    public IOptions<FeatureStreamOptions> Options { get; }
    public IOptions<FeatureChangeEventOptions> EventOptions { get; }
    public IFilterExpressionService FilterExpressionService { get; }
    public ILayerCatalog LayerCatalog { get; }
    public IGeometryOperationService GeometryOperationService { get; }
    public ILicenseStatusProvider LicenseStatusProvider { get; }
}
