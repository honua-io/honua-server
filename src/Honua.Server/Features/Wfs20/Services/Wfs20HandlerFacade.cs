// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Server.Features.Wfs20.Models;

namespace Honua.Server.Features.Wfs20.Services;

/// <summary>
/// Facade that coordinates WFS 2.0 operations across specialized services.
/// Replaces the original Wfs20Handler god class with a composition-based approach
/// following the Single Responsibility and Interface Segregation principles.
/// </summary>
internal sealed class Wfs20HandlerFacade
{
    private readonly IWfs20CapabilitiesService _capabilitiesService;
    private readonly IWfs20SchemaService _schemaService;
    private readonly IWfs20QueryService _queryService;
    private readonly IWfs20TransactionService _transactionService;
    private readonly ILogger<Wfs20HandlerFacade> _logger;

    public Wfs20HandlerFacade(
        IWfs20CapabilitiesService capabilitiesService,
        IWfs20SchemaService schemaService,
        IWfs20QueryService queryService,
        IWfs20TransactionService transactionService,
        ILogger<Wfs20HandlerFacade> logger)
    {
        _capabilitiesService = capabilitiesService;
        _schemaService = schemaService;
        _queryService = queryService;
        _transactionService = transactionService;
        _logger = logger;
    }

    /// <summary>
    /// Delegates capabilities requests to the specialized capabilities service
    /// </summary>
    public async Task<WfsCapabilities> HandleGetCapabilitiesAsync(
        HttpContext context,
        string? acceptVersions,
        IReadOnlySet<string>? requestedSections,
        string baseUrl,
        CancellationToken cancellationToken = default)
    {
        return await _capabilitiesService.GetCapabilitiesAsync(
            context, acceptVersions, requestedSections, baseUrl, cancellationToken);
    }

    /// <summary>
    /// Delegates schema description requests to the specialized schema service
    /// </summary>
    public async Task<string> HandleDescribeFeatureTypeAsync(
        HttpContext context,
        string? typeNames,
        string? outputFormat,
        CancellationToken cancellationToken = default)
    {
        return await _schemaService.DescribeFeatureTypeAsync(
            context, typeNames, outputFormat, cancellationToken);
    }

    /// <summary>
    /// Delegates stored query listing to the specialized query service
    /// </summary>
    public async Task<IResult> HandleListStoredQueriesAsync(
        HttpContext context,
        CancellationToken cancellationToken = default)
    {
        return await _queryService.ListStoredQueriesAsync(context, cancellationToken);
    }

    /// <summary>
    /// Delegates stored query description to the specialized query service
    /// </summary>
    public async Task<IResult> HandleDescribeStoredQueriesAsync(
        HttpContext context,
        string? storedQueryIds,
        CancellationToken cancellationToken = default)
    {
        return await _queryService.DescribeStoredQueriesAsync(context, storedQueryIds, cancellationToken);
    }

    /// <summary>
    /// Delegates stored query execution to the specialized query service
    /// </summary>
    public async Task<IResult> HandleStoredQueryGetFeatureAsync(
        HttpContext context,
        string storedQueryId,
        string? featureId,
        string? outputFormat,
        string? count,
        CancellationToken cancellationToken = default)
    {
        return await _queryService.HandleStoredQueryGetFeatureAsync(
            context, storedQueryId, featureId, outputFormat, count, cancellationToken);
    }

    /// <summary>
    /// Delegates feature queries to the specialized query service
    /// </summary>
    public async Task<IResult> HandleGetFeatureAsync(
        HttpContext context,
        Dictionary<string, object> queryParameters,
        CancellationToken cancellationToken = default)
    {
        return await _queryService.HandleGetFeatureAsync(context, queryParameters, cancellationToken);
    }

    /// <summary>
    /// Delegates property value queries to the specialized query service
    /// </summary>
    public async Task<IResult> HandleGetPropertyValueAsync(
        HttpContext context,
        Dictionary<string, object> queryParameters,
        CancellationToken cancellationToken = default)
    {
        return await _queryService.HandleGetPropertyValueAsync(context, queryParameters, cancellationToken);
    }

    /// <summary>
    /// Delegates transaction operations to the specialized transaction service
    /// </summary>
    public async Task<IResult> HandleTransactionAsync(
        HttpContext context,
        string transactionXml,
        CancellationToken cancellationToken = default)
    {
        return await _transactionService.HandleTransactionAsync(context, transactionXml, cancellationToken);
    }
}