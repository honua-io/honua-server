// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Sdk.Admin.Models;

/// <summary>
/// Lightweight service summary for the service list endpoint.
/// </summary>
public sealed class ServiceSummary
{
    /// <summary>
    /// The service name.
    /// </summary>
    [JsonPropertyName("serviceName")]
    public string ServiceName { get; init; } = string.Empty;

    /// <summary>
    /// The service description.
    /// </summary>
    [JsonPropertyName("description")]
    public string Description { get; init; } = string.Empty;

    /// <summary>
    /// Number of layers in the service.
    /// </summary>
    [JsonPropertyName("layerCount")]
    public int LayerCount { get; init; }

    /// <summary>
    /// Protocols enabled for this service.
    /// </summary>
    [JsonPropertyName("enabledProtocols")]
    public string[]? EnabledProtocols { get; init; }
}

/// <summary>
/// Response model for service settings including protocols and MapServer configuration.
/// </summary>
public sealed class ServiceSettingsResponse
{
    /// <summary>
    /// The service name.
    /// </summary>
    [JsonPropertyName("serviceName")]
    public string ServiceName { get; init; } = string.Empty;

    /// <summary>
    /// Protocols currently enabled for this service.
    /// </summary>
    [JsonPropertyName("enabledProtocols")]
    public string[] EnabledProtocols { get; init; } = [];

    /// <summary>
    /// Protocols supported by this server build.
    /// </summary>
    [JsonPropertyName("availableProtocols")]
    public string[] AvailableProtocols { get; init; } = [];

    /// <summary>
    /// Access policy for the service.
    /// </summary>
    [JsonPropertyName("accessPolicy")]
    public AccessPolicyResponse? AccessPolicy { get; init; }

    /// <summary>
    /// Temporal metadata for time-aware layers.
    /// </summary>
    [JsonPropertyName("timeInfo")]
    public TimeInfoResponse? TimeInfo { get; init; }

    /// <summary>
    /// MapServer rendering configuration.
    /// </summary>
    [JsonPropertyName("mapServer")]
    public MapServerSettingsResponse MapServer { get; init; } = new();
}

/// <summary>
/// Layer metadata response model.
/// </summary>
public sealed class LayerMetadataResponse
{
    /// <summary>
    /// The layer identifier.
    /// </summary>
    [JsonPropertyName("layerId")]
    public int LayerId { get; init; }

    /// <summary>
    /// The layer name.
    /// </summary>
    [JsonPropertyName("layerName")]
    public string LayerName { get; init; } = string.Empty;

    /// <summary>
    /// Access policy for the layer.
    /// </summary>
    [JsonPropertyName("accessPolicy")]
    public AccessPolicyResponse? AccessPolicy { get; init; }

    /// <summary>
    /// Temporal metadata for the layer.
    /// </summary>
    [JsonPropertyName("timeInfo")]
    public TimeInfoResponse? TimeInfo { get; init; }
}

/// <summary>
/// Access policy response model.
/// </summary>
public sealed class AccessPolicyResponse
{
    /// <summary>
    /// Whether anonymous access is allowed.
    /// </summary>
    [JsonPropertyName("allowAnonymous")]
    public bool AllowAnonymous { get; init; }

    /// <summary>
    /// Whether anonymous write access is allowed.
    /// </summary>
    [JsonPropertyName("allowAnonymousWrite")]
    public bool AllowAnonymousWrite { get; init; }

    /// <summary>
    /// Allowed role names for access.
    /// </summary>
    [JsonPropertyName("allowedRoles")]
    public string[]? AllowedRoles { get; init; }

    /// <summary>
    /// Allowed role names for write access.
    /// </summary>
    [JsonPropertyName("allowedWriteRoles")]
    public string[]? AllowedWriteRoles { get; init; }
}

/// <summary>
/// Temporal metadata response model.
/// </summary>
public sealed class TimeInfoResponse
{
    /// <summary>
    /// Field name containing the start time.
    /// </summary>
    [JsonPropertyName("startTimeField")]
    public string? StartTimeField { get; init; }

    /// <summary>
    /// Field name containing the end time.
    /// </summary>
    [JsonPropertyName("endTimeField")]
    public string? EndTimeField { get; init; }

    /// <summary>
    /// Track identifier field for temporal visualization.
    /// </summary>
    [JsonPropertyName("trackIdField")]
    public string? TrackIdField { get; init; }
}

/// <summary>
/// MapServer rendering settings for a service.
/// </summary>
public sealed class MapServerSettingsResponse
{
    /// <summary>
    /// Maximum allowed image width in pixels.
    /// </summary>
    [JsonPropertyName("maxImageWidth")]
    public int MaxImageWidth { get; init; }

    /// <summary>
    /// Maximum allowed image height in pixels.
    /// </summary>
    [JsonPropertyName("maxImageHeight")]
    public int MaxImageHeight { get; init; }

    /// <summary>
    /// Default image width when not specified.
    /// </summary>
    [JsonPropertyName("defaultImageWidth")]
    public int DefaultImageWidth { get; init; }

    /// <summary>
    /// Default image height when not specified.
    /// </summary>
    [JsonPropertyName("defaultImageHeight")]
    public int DefaultImageHeight { get; init; }

    /// <summary>
    /// Default DPI for rendered images.
    /// </summary>
    [JsonPropertyName("defaultDpi")]
    public int DefaultDpi { get; init; }

    /// <summary>
    /// Default output format.
    /// </summary>
    [JsonPropertyName("defaultFormat")]
    public string DefaultFormat { get; init; } = string.Empty;

    /// <summary>
    /// Whether the background is transparent by default.
    /// </summary>
    [JsonPropertyName("defaultTransparent")]
    public bool DefaultTransparent { get; init; }

    /// <summary>
    /// Maximum features rendered per layer.
    /// </summary>
    [JsonPropertyName("maxFeaturesPerLayer")]
    public int MaxFeaturesPerLayer { get; init; }
}

/// <summary>
/// Request to update enabled protocols for a service.
/// </summary>
public sealed class UpdateProtocolsRequest
{
    /// <summary>
    /// Protocols to enable. Valid values: "FeatureServer", "MapServer", "OgcFeatures", "OData", "Grpc".
    /// </summary>
    [JsonPropertyName("enabledProtocols")]
    public string[] EnabledProtocols { get; init; } = [];
}

/// <summary>
/// Request to update MapServer rendering settings. Null fields are not updated.
/// </summary>
public sealed class UpdateMapServerSettingsRequest
{
    /// <summary>
    /// Maximum allowed image width in pixels.
    /// </summary>
    [JsonPropertyName("maxImageWidth")]
    public int? MaxImageWidth { get; init; }

    /// <summary>
    /// Maximum allowed image height in pixels.
    /// </summary>
    [JsonPropertyName("maxImageHeight")]
    public int? MaxImageHeight { get; init; }

    /// <summary>
    /// Default image width when not specified.
    /// </summary>
    [JsonPropertyName("defaultImageWidth")]
    public int? DefaultImageWidth { get; init; }

    /// <summary>
    /// Default image height when not specified.
    /// </summary>
    [JsonPropertyName("defaultImageHeight")]
    public int? DefaultImageHeight { get; init; }

    /// <summary>
    /// Default DPI for rendered images.
    /// </summary>
    [JsonPropertyName("defaultDpi")]
    public int? DefaultDpi { get; init; }

    /// <summary>
    /// Default output format.
    /// </summary>
    [JsonPropertyName("defaultFormat")]
    public string? DefaultFormat { get; init; }

    /// <summary>
    /// Whether the background is transparent by default.
    /// </summary>
    [JsonPropertyName("defaultTransparent")]
    public bool? DefaultTransparent { get; init; }

    /// <summary>
    /// Maximum features rendered per layer.
    /// </summary>
    [JsonPropertyName("maxFeaturesPerLayer")]
    public int? MaxFeaturesPerLayer { get; init; }
}

/// <summary>
/// Request to update the access policy for a service.
/// </summary>
public sealed class UpdateAccessPolicyRequest
{
    /// <summary>
    /// Whether anonymous access is allowed.
    /// </summary>
    [JsonPropertyName("allowAnonymous")]
    public bool? AllowAnonymous { get; init; }

    /// <summary>
    /// Whether anonymous write access is allowed.
    /// </summary>
    [JsonPropertyName("allowAnonymousWrite")]
    public bool? AllowAnonymousWrite { get; init; }

    /// <summary>
    /// Allowed role names for access.
    /// </summary>
    [JsonPropertyName("allowedRoles")]
    public string[]? AllowedRoles { get; init; }

    /// <summary>
    /// Allowed role names for write access.
    /// </summary>
    [JsonPropertyName("allowedWriteRoles")]
    public string[]? AllowedWriteRoles { get; init; }
}

/// <summary>
/// Request to update the time info for a service.
/// </summary>
public sealed class UpdateTimeInfoRequest
{
    /// <summary>
    /// Field name containing the start time. Empty string clears the value.
    /// </summary>
    [JsonPropertyName("startTimeField")]
    public string? StartTimeField { get; init; }

    /// <summary>
    /// Field name containing the end time. Empty string clears the value.
    /// </summary>
    [JsonPropertyName("endTimeField")]
    public string? EndTimeField { get; init; }

    /// <summary>
    /// Track identifier field. Empty string clears the value.
    /// </summary>
    [JsonPropertyName("trackIdField")]
    public string? TrackIdField { get; init; }
}

/// <summary>
/// Request to update layer metadata.
/// </summary>
public sealed class UpdateLayerMetadataRequest
{
    /// <summary>
    /// Access policy updates.
    /// </summary>
    [JsonPropertyName("accessPolicy")]
    public UpdateAccessPolicyRequest? AccessPolicy { get; init; }

    /// <summary>
    /// Time info updates.
    /// </summary>
    [JsonPropertyName("timeInfo")]
    public UpdateTimeInfoRequest? TimeInfo { get; init; }
}
