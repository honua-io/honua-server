// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Features.Protocols.Ogc.Classic.Wms;

/// <summary>
/// Service responsible for handling WMS (Web Map Service) operations.
/// </summary>
internal interface IWmsService
{
    /// <summary>
    /// Handle WMS GetCapabilities requests
    /// </summary>
    /// <param name="context">HTTP context for the request</param>
    /// <param name="serviceId">Service identifier</param>
    /// <param name="version">WMS version</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>WMS capabilities document</returns>
    Task<IResult> HandleGetCapabilitiesAsync(
        HttpContext context,
        int serviceId,
        string? version,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Handle WMS GetMap requests
    /// </summary>
    /// <param name="context">HTTP context for the request</param>
    /// <param name="mapParameters">WMS map parameters</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Map image result</returns>
    Task<IResult> HandleGetMapAsync(
        HttpContext context,
        WmsMapParameters mapParameters,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Handle WMS GetFeatureInfo requests
    /// </summary>
    /// <param name="context">HTTP context for the request</param>
    /// <param name="featureInfoParameters">Feature info parameters</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Feature info result</returns>
    Task<IResult> HandleGetFeatureInfoAsync(
        HttpContext context,
        WmsFeatureInfoParameters featureInfoParameters,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Parameters for WMS GetMap operations
/// </summary>
public sealed record WmsMapParameters
{
    public required int ServiceId { get; init; }
    public required string Layers { get; init; }
    public required string Styles { get; init; }
    public required string Crs { get; init; }
    public required string BoundingBox { get; init; }
    public required string Width { get; init; }
    public required string Height { get; init; }
    public required string Format { get; init; }
    public string? Time { get; init; }
    public bool Transparent { get; init; }
}

/// <summary>
/// Parameters for WMS GetFeatureInfo operations
/// </summary>
public sealed record WmsFeatureInfoParameters
{
    public required WmsMapParameters MapParameters { get; init; }
    public required string QueryLayers { get; init; }
    public required string InfoFormat { get; init; }
    public required int X { get; init; }
    public required int Y { get; init; }
    public int FeatureCount { get; init; } = 10;
}
