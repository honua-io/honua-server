// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Features.Wfs20.Services;

/// <summary>
/// Service responsible for handling WFS 2.0 feature queries and stored queries.
/// Segregated interface following the Interface Segregation Principle.
/// </summary>
internal interface IWfs20QueryService
{
    /// <summary>
    /// Handle GetFeature requests
    /// </summary>
    /// <param name="context">HTTP context for the request</param>
    /// <param name="queryParameters">Query parameters from the request</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Query result</returns>
    Task<IResult> HandleGetFeatureAsync(
        HttpContext context,
        Dictionary<string, object> queryParameters,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Handle stored query requests
    /// </summary>
    /// <param name="context">HTTP context for the request</param>
    /// <param name="storedQueryId">Stored query identifier</param>
    /// <param name="featureId">Feature ID for GetFeatureById queries</param>
    /// <param name="outputFormat">Output format</param>
    /// <param name="count">Maximum number of features to return</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Query result</returns>
    Task<IResult> HandleStoredQueryGetFeatureAsync(
        HttpContext context,
        string storedQueryId,
        string? featureId,
        string? outputFormat,
        string? count,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// List available stored queries
    /// </summary>
    /// <param name="context">HTTP context for authorization</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>List of stored queries as XML</returns>
    Task<IResult> ListStoredQueriesAsync(
        HttpContext context,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Describe stored queries
    /// </summary>
    /// <param name="context">HTTP context for authorization</param>
    /// <param name="storedQueryIds">Comma-separated list of stored query IDs</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Stored query descriptions as XML</returns>
    Task<IResult> DescribeStoredQueriesAsync(
        HttpContext context,
        string? storedQueryIds,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Handle GetPropertyValue requests
    /// </summary>
    /// <param name="context">HTTP context for the request</param>
    /// <param name="queryParameters">Query parameters from the request</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Property values result</returns>
    Task<IResult> HandleGetPropertyValueAsync(
        HttpContext context,
        Dictionary<string, object> queryParameters,
        CancellationToken cancellationToken = default);
}