// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Features.Wfs20.Services;

/// <summary>
/// Service responsible for handling WFS 2.0 schema operations.
/// Segregated interface following the Interface Segregation Principle.
/// </summary>
internal interface IWfs20SchemaService
{
    /// <summary>
    /// Generate schema description for feature types
    /// </summary>
    /// <param name="context">HTTP context for authorization and request details</param>
    /// <param name="typeNames">Comma-separated list of type names to describe</param>
    /// <param name="outputFormat">Output format for the schema</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Schema description as XML string</returns>
    Task<string> DescribeFeatureTypeAsync(
        HttpContext context,
        string? typeNames,
        string? outputFormat,
        CancellationToken cancellationToken = default);
}