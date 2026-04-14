// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Caching;
using Honua.Core.Features.Infrastructure.Caching;
using Honua.Core.Features.Infrastructure.Validation;
using Honua.Core.Features.Validation.Abstractions;
using Honua.Core.Queries.Filters;
using Honua.Server.Features.FeatureServer.Services;
using Honua.Server.Features.Infrastructure.Caching;
using Microsoft.Extensions.Options;

namespace Honua.Server.Features.FeatureServer;

internal sealed class FeatureServerQueryDependencies
{
    public FeatureServerQueryDependencies(
        IResourceValidator resourceValidator,
        IFeatureServerQueryServices queryServices,
        IFilterExpressionService filterExpressionService,
        FeatureServerQueryExecutor queryExecutor,
        IResponseCache responseCache,
        IETagService etagService,
        IOptions<CacheOptions> cacheOptions)
    {
        // Validation framework eliminates 7 lines of duplicate null checks
        ResourceValidator = resourceValidator.ThrowIfNull();
        QueryServices = queryServices.ThrowIfNull();
        FilterExpressionService = filterExpressionService.ThrowIfNull();
        QueryExecutor = queryExecutor.ThrowIfNull();
        ResponseCache = responseCache.ThrowIfNull();
        ETagService = etagService.ThrowIfNull();
        CacheOptions = cacheOptions.ValidateAndGetValue();
    }

    public IResourceValidator ResourceValidator { get; }
    public IFeatureServerQueryServices QueryServices { get; }
    public IFilterExpressionService FilterExpressionService { get; }
    public FeatureServerQueryExecutor QueryExecutor { get; }
    public IResponseCache ResponseCache { get; }
    public IETagService ETagService { get; }
    public CacheOptions CacheOptions { get; }
}
