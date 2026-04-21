// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Features.MapServer.Services;

/// <summary>
/// Service responsible for handling MapServer export operations.
/// Segregated interface following the Interface Segregation Principle.
/// </summary>
internal interface IMapServerExportService
{
    /// <summary>
    /// Handle MapServer export (map image generation) requests
    /// </summary>
    /// <param name="context">HTTP context for the request</param>
    /// <param name="exportParameters">Export parameters extracted from request</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Export result with generated image or error</returns>
    Task<IResult> HandleExportAsync(
        HttpContext context,
        MapServerExportParameters exportParameters,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Parameters for MapServer export operations
/// </summary>
public sealed record MapServerExportParameters
{
    public required int ServiceId { get; init; }
    public required string ResponseFormat { get; init; }
    public string? BoundingBox { get; init; }
    public string? ImageSize { get; init; }
    public string? SpatialReference { get; init; }
    public string? Layers { get; init; }
    public string? LayerDefs { get; init; }
    public string? Time { get; init; }
    public Dictionary<string, string> AdditionalParameters { get; init; } = new();
}