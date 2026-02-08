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
    /// MapServer rendering configuration.
    /// </summary>
    public required MapServerSettingsResponse MapServer { get; init; }
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
    /// Protocols to enable. Valid values: "FeatureServer", "MapServer", "OgcFeatures", "OData".
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
}
