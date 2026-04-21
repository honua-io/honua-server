// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Server.Features.Wfs20.Models;

namespace Honua.Server.Features.Wfs20.Services;

/// <summary>
/// Compatibility wrapper that provides the original Wfs20Handler interface
/// while delegating to the refactored facade pattern implementation.
/// Ensures backward compatibility during the transition period.
/// Since the original Wfs20Handler is sealed, this provides the same public interface
/// without inheritance.
/// </summary>
internal sealed class Wfs20HandlerCompatibilityWrapper
{
    private readonly Wfs20HandlerFacade _facade;

    public Wfs20HandlerCompatibilityWrapper(Wfs20HandlerFacade facade)
    {
        _facade = facade;
    }

    // Delegate all operations to the facade
    public async Task<WfsCapabilities> HandleGetCapabilitiesAsync(
        HttpContext context,
        string? acceptVersions,
        IReadOnlySet<string>? requestedSections,
        string baseUrl,
        CancellationToken cancellationToken = default)
    {
        return await _facade.HandleGetCapabilitiesAsync(
            context, acceptVersions, requestedSections, baseUrl, cancellationToken);
    }

    public async Task<string> HandleDescribeFeatureTypeAsync(
        HttpContext context,
        string? typeNames,
        string? outputFormat,
        CancellationToken cancellationToken = default)
    {
        return await _facade.HandleDescribeFeatureTypeAsync(
            context, typeNames, outputFormat, cancellationToken);
    }

    public async Task<IResult> HandleListStoredQueriesAsync(
        HttpContext context,
        CancellationToken cancellationToken = default)
    {
        return await _facade.HandleListStoredQueriesAsync(context, cancellationToken);
    }

    public async Task<IResult> HandleDescribeStoredQueriesAsync(
        HttpContext context,
        string? storedQueryIds,
        CancellationToken cancellationToken = default)
    {
        return await _facade.HandleDescribeStoredQueriesAsync(context, storedQueryIds, cancellationToken);
    }

    public async Task<IResult> HandleStoredQueryGetFeatureAsync(
        HttpContext context,
        string storedQueryId,
        string? featureId,
        string? outputFormat,
        string? count,
        CancellationToken cancellationToken = default)
    {
        return await _facade.HandleStoredQueryGetFeatureAsync(
            context, storedQueryId, featureId, outputFormat, count, cancellationToken);
    }

    public async Task<IResult> HandleGetFeatureAsync(
        HttpContext context,
        Dictionary<string, object> queryParameters,
        CancellationToken cancellationToken = default)
    {
        return await _facade.HandleGetFeatureAsync(context, queryParameters, cancellationToken);
    }

    public async Task<IResult> HandleGetPropertyValueAsync(
        HttpContext context,
        Dictionary<string, object> queryParameters,
        CancellationToken cancellationToken = default)
    {
        return await _facade.HandleGetPropertyValueAsync(context, queryParameters, cancellationToken);
    }

    public async Task<IResult> HandleTransactionAsync(
        HttpContext context,
        string transactionXml,
        CancellationToken cancellationToken = default)
    {
        return await _facade.HandleTransactionAsync(context, transactionXml, cancellationToken);
    }
}