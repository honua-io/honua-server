// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Features.Admin.Models;

/// <summary>
/// Response model for service settings (protocols + MapServer configuration).
/// </summary>
internal sealed class ServiceSettingsResponse
{
    /// <summary>
    /// The service name.
    /// </summary>
    public required string ServiceName { get; init; }

    /// <summary>
    /// Protocols currently enabled for this service.
    /// </summary>
    public required string[] EnabledProtocols { get; init; }

    /// <summary>
    /// Protocols supported by this server build.
    /// </summary>
    public required string[] AvailableProtocols { get; init; }

    /// <summary>
    /// Access policy for the service.
    /// </summary>
    public AccessPolicyResponse? AccessPolicy { get; init; }

    /// <summary>
    /// Temporal metadata for time-aware layers.
    /// </summary>
    public TimeInfoResponse? TimeInfo { get; init; }

    /// <summary>
    /// MapServer rendering configuration.
    /// </summary>
    public required MapServerSettingsResponse MapServer { get; init; }
}

/// <summary>
/// Access policy response model.
/// </summary>
internal sealed class AccessPolicyResponse
{
    /// <summary>Whether anonymous access is allowed.</summary>
    public bool AllowAnonymous { get; init; }

    /// <summary>Whether anonymous write access is allowed.</summary>
    public bool AllowAnonymousWrite { get; init; }

    /// <summary>Allowed role names for access.</summary>
    public string[]? AllowedRoles { get; init; }

    /// <summary>Allowed role names for write access.</summary>
    public string[]? AllowedWriteRoles { get; init; }
}

/// <summary>
/// Temporal metadata response model.
/// </summary>
internal sealed class TimeInfoResponse
{
    /// <summary>Field name containing the start time.</summary>
    public string? StartTimeField { get; init; }

    /// <summary>Field name containing the end time.</summary>
    public string? EndTimeField { get; init; }

    /// <summary>Track identifier field for temporal visualization.</summary>
    public string? TrackIdField { get; init; }
}

/// <summary>
/// Request to update the access policy for a service.
/// </summary>
internal sealed class UpdateAccessPolicyRequest
{
    /// <summary>Whether anonymous access is allowed.</summary>
    public bool? AllowAnonymous { get; init; }

    /// <summary>Whether anonymous write access is allowed.</summary>
    public bool? AllowAnonymousWrite { get; init; }

    /// <summary>Allowed role names for access.</summary>
    public string[]? AllowedRoles { get; init; }

    /// <summary>Allowed role names for write access.</summary>
    public string[]? AllowedWriteRoles { get; init; }
}

/// <summary>
/// Request to update the time info for a service.
/// </summary>
internal sealed class UpdateTimeInfoRequest
{
    /// <summary>Field name containing the start time. Empty string clears the value.</summary>
    public string? StartTimeField { get; init; }

    /// <summary>Field name containing the end time. Empty string clears the value.</summary>
    public string? EndTimeField { get; init; }

    /// <summary>Track identifier field. Empty string clears the value.</summary>
    public string? TrackIdField { get; init; }
}

/// <summary>
/// Response model for layer metadata.
/// </summary>
internal sealed class LayerMetadataResponse
{
    /// <summary>The layer identifier.</summary>
    public required int LayerId { get; init; }

    /// <summary>The layer name.</summary>
    public required string LayerName { get; init; }

    /// <summary>Access policy for the layer.</summary>
    public AccessPolicyResponse? AccessPolicy { get; init; }

    /// <summary>Temporal metadata for the layer.</summary>
    public TimeInfoResponse? TimeInfo { get; init; }

    /// <summary>Raster mosaic defaults for the layer.</summary>
    public RasterMosaicResponse? RasterMosaic { get; init; }
}

/// <summary>
/// Request to update layer metadata.
/// </summary>
internal sealed class UpdateLayerMetadataRequest
{
    /// <summary>Access policy updates.</summary>
    public UpdateAccessPolicyRequest? AccessPolicy { get; init; }

    /// <summary>Time info updates.</summary>
    public UpdateTimeInfoRequest? TimeInfo { get; init; }

    /// <summary>Raster mosaic updates.</summary>
    public UpdateRasterMosaicRequest? RasterMosaic { get; init; }
}

/// <summary>
/// Raster mosaic defaults surfaced through the admin layer metadata API.
/// </summary>
internal sealed class RasterMosaicResponse
{
    /// <summary>Default merge strategy for overlapping rasters.</summary>
    public string? MergeStrategy { get; init; }
}

/// <summary>
/// Request to update raster mosaic defaults for a layer.
/// </summary>
internal sealed class UpdateRasterMosaicRequest
{
    /// <summary>Default merge strategy. Empty string clears the value.</summary>
    public string? MergeStrategy { get; init; }
}

/// <summary>
/// MapServer rendering settings for a service.
/// </summary>
internal sealed class MapServerSettingsResponse
{
    /// <summary>Maximum allowed image width in pixels.</summary>
    public int MaxImageWidth { get; init; }

    /// <summary>Maximum allowed image height in pixels.</summary>
    public int MaxImageHeight { get; init; }

    /// <summary>Default image width when not specified.</summary>
    public int DefaultImageWidth { get; init; }

    /// <summary>Default image height when not specified.</summary>
    public int DefaultImageHeight { get; init; }

    /// <summary>Default DPI for rendered images.</summary>
    public int DefaultDpi { get; init; }

    /// <summary>Default output format.</summary>
    public required string DefaultFormat { get; init; }

    /// <summary>Whether the background is transparent by default.</summary>
    public bool DefaultTransparent { get; init; }

    /// <summary>Maximum features rendered per layer.</summary>
    public int MaxFeaturesPerLayer { get; init; }
}

/// <summary>
/// Request to update enabled protocols for a service.
/// </summary>
internal sealed class UpdateProtocolsRequest
{
    /// <summary>
    /// Protocols to enable. Valid values: "FeatureServer", "MapServer", "OgcFeatures", "OData", "Grpc".
    /// </summary>
    public required string[] EnabledProtocols { get; init; }
}

/// <summary>
/// Request to update MapServer rendering settings. Null fields are not updated.
/// </summary>
internal sealed class UpdateMapServerSettingsRequest
{
    /// <summary>Maximum allowed image width in pixels.</summary>
    public int? MaxImageWidth { get; init; }

    /// <summary>Maximum allowed image height in pixels.</summary>
    public int? MaxImageHeight { get; init; }

    /// <summary>Default image width when not specified.</summary>
    public int? DefaultImageWidth { get; init; }

    /// <summary>Default image height when not specified.</summary>
    public int? DefaultImageHeight { get; init; }

    /// <summary>Default DPI for rendered images.</summary>
    public int? DefaultDpi { get; init; }

    /// <summary>Default output format.</summary>
    public string? DefaultFormat { get; init; }

    /// <summary>Whether the background is transparent by default.</summary>
    public bool? DefaultTransparent { get; init; }

    /// <summary>Maximum features rendered per layer.</summary>
    public int? MaxFeaturesPerLayer { get; init; }
}

/// <summary>
/// Lightweight service summary for the service list endpoint.
/// </summary>
internal sealed class ServiceSummary
{
    /// <summary>
    /// The service name.
    /// </summary>
    public required string ServiceName { get; init; }

    /// <summary>
    /// The service description.
    /// </summary>
    public required string Description { get; init; }

    /// <summary>
    /// Number of layers in the service.
    /// </summary>
    public int LayerCount { get; init; }

    /// <summary>
    /// Protocols enabled for this service.
    /// </summary>
    public string[]? EnabledProtocols { get; init; }
}
