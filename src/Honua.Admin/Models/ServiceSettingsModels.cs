// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Admin.Models;

/// <summary>
/// Lightweight service summary for the service list endpoint.
/// </summary>
public sealed class ServiceSummary
{
    [JsonPropertyName("serviceName")]
    public string ServiceName { get; init; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; init; } = string.Empty;

    [JsonPropertyName("layerCount")]
    public int LayerCount { get; init; }

    [JsonPropertyName("enabledProtocols")]
    public string[]? EnabledProtocols { get; init; }
}

/// <summary>
/// Response model for service settings including protocols and MapServer configuration.
/// </summary>
public sealed class ServiceSettingsResponse
{
    [JsonPropertyName("serviceName")]
    public string ServiceName { get; init; } = string.Empty;

    [JsonPropertyName("enabledProtocols")]
    public string[] EnabledProtocols { get; init; } = [];

    [JsonPropertyName("availableProtocols")]
    public string[] AvailableProtocols { get; init; } = [];

    [JsonPropertyName("accessPolicy")]
    public AccessPolicyResponse? AccessPolicy { get; init; }

    [JsonPropertyName("timeInfo")]
    public TimeInfoResponse? TimeInfo { get; init; }

    [JsonPropertyName("mapServer")]
    public MapServerSettingsResponse MapServer { get; init; } = new();
}

/// <summary>
/// Layer metadata response model.
/// </summary>
public sealed class LayerMetadataResponse
{
    [JsonPropertyName("layerId")]
    public int LayerId { get; init; }

    [JsonPropertyName("layerName")]
    public string LayerName { get; init; } = string.Empty;

    [JsonPropertyName("accessPolicy")]
    public AccessPolicyResponse? AccessPolicy { get; init; }

    [JsonPropertyName("timeInfo")]
    public TimeInfoResponse? TimeInfo { get; init; }
}

/// <summary>
/// Access policy response model.
/// </summary>
public sealed class AccessPolicyResponse
{
    [JsonPropertyName("allowAnonymous")]
    public bool AllowAnonymous { get; init; }

    [JsonPropertyName("allowAnonymousWrite")]
    public bool AllowAnonymousWrite { get; init; }

    [JsonPropertyName("allowedRoles")]
    public string[]? AllowedRoles { get; init; }

    [JsonPropertyName("allowedWriteRoles")]
    public string[]? AllowedWriteRoles { get; init; }
}

/// <summary>
/// Temporal metadata response model.
/// </summary>
public sealed class TimeInfoResponse
{
    [JsonPropertyName("startTimeField")]
    public string? StartTimeField { get; init; }

    [JsonPropertyName("endTimeField")]
    public string? EndTimeField { get; init; }

    [JsonPropertyName("trackIdField")]
    public string? TrackIdField { get; init; }
}

/// <summary>
/// MapServer rendering settings for a service.
/// </summary>
public sealed class MapServerSettingsResponse
{
    [JsonPropertyName("maxImageWidth")]
    public int MaxImageWidth { get; init; }

    [JsonPropertyName("maxImageHeight")]
    public int MaxImageHeight { get; init; }

    [JsonPropertyName("defaultImageWidth")]
    public int DefaultImageWidth { get; init; }

    [JsonPropertyName("defaultImageHeight")]
    public int DefaultImageHeight { get; init; }

    [JsonPropertyName("defaultDpi")]
    public int DefaultDpi { get; init; }

    [JsonPropertyName("defaultFormat")]
    public string DefaultFormat { get; init; } = string.Empty;

    [JsonPropertyName("defaultTransparent")]
    public bool DefaultTransparent { get; init; }

    [JsonPropertyName("maxFeaturesPerLayer")]
    public int MaxFeaturesPerLayer { get; init; }
}

/// <summary>
/// Request to update enabled protocols for a service.
/// </summary>
public sealed class UpdateProtocolsRequest
{
    [JsonPropertyName("enabledProtocols")]
    public string[] EnabledProtocols { get; init; } = [];
}

/// <summary>
/// Request to update MapServer rendering settings.
/// </summary>
public sealed class UpdateMapServerSettingsRequest
{
    [JsonPropertyName("maxImageWidth")]
    public int? MaxImageWidth { get; init; }

    [JsonPropertyName("maxImageHeight")]
    public int? MaxImageHeight { get; init; }

    [JsonPropertyName("defaultImageWidth")]
    public int? DefaultImageWidth { get; init; }

    [JsonPropertyName("defaultImageHeight")]
    public int? DefaultImageHeight { get; init; }

    [JsonPropertyName("defaultDpi")]
    public int? DefaultDpi { get; init; }

    [JsonPropertyName("defaultFormat")]
    public string? DefaultFormat { get; init; }

    [JsonPropertyName("defaultTransparent")]
    public bool? DefaultTransparent { get; init; }

    [JsonPropertyName("maxFeaturesPerLayer")]
    public int? MaxFeaturesPerLayer { get; init; }
}

/// <summary>
/// Request to update the access policy for a service.
/// </summary>
public sealed class UpdateAccessPolicyRequest
{
    [JsonPropertyName("allowAnonymous")]
    public bool? AllowAnonymous { get; init; }

    [JsonPropertyName("allowAnonymousWrite")]
    public bool? AllowAnonymousWrite { get; init; }

    [JsonPropertyName("allowedRoles")]
    public string[]? AllowedRoles { get; init; }

    [JsonPropertyName("allowedWriteRoles")]
    public string[]? AllowedWriteRoles { get; init; }
}

/// <summary>
/// Request to update the time info for a service.
/// </summary>
public sealed class UpdateTimeInfoRequest
{
    [JsonPropertyName("startTimeField")]
    public string? StartTimeField { get; init; }

    [JsonPropertyName("endTimeField")]
    public string? EndTimeField { get; init; }

    [JsonPropertyName("trackIdField")]
    public string? TrackIdField { get; init; }
}

/// <summary>
/// Request to update layer metadata.
/// </summary>
public sealed class UpdateLayerMetadataRequest
{
    [JsonPropertyName("accessPolicy")]
    public UpdateAccessPolicyRequest? AccessPolicy { get; init; }

    [JsonPropertyName("timeInfo")]
    public UpdateTimeInfoRequest? TimeInfo { get; init; }
}
