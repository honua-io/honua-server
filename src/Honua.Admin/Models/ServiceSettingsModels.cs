// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Admin.Models;

/// <summary>
/// Response model for service settings (protocols + MapServer configuration).
/// </summary>
public sealed class ServiceSettingsResponse
{
    /// <summary>The service name.</summary>
    public string ServiceName { get; init; } = "";

    /// <summary>Protocols currently enabled for this service.</summary>
    public string[] EnabledProtocols { get; init; } = [];

    /// <summary>Protocols supported by this server build.</summary>
    public string[] AvailableProtocols { get; init; } = [];

    /// <summary>MapServer rendering configuration.</summary>
    public MapServerSettingsResponse MapServer { get; init; } = new();
}

/// <summary>
/// MapServer rendering settings for a service.
/// </summary>
public sealed class MapServerSettingsResponse
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
    public string DefaultFormat { get; init; } = "png";

    /// <summary>Whether the background is transparent by default.</summary>
    public bool DefaultTransparent { get; init; }

    /// <summary>Maximum features rendered per layer.</summary>
    public int MaxFeaturesPerLayer { get; init; }
}

/// <summary>
/// Request to update enabled protocols for a service.
/// </summary>
public sealed class UpdateProtocolsRequest
{
    /// <summary>Protocols to enable.</summary>
    public string[] EnabledProtocols { get; init; } = [];
}

/// <summary>
/// Request to update MapServer rendering settings.
/// </summary>
public sealed class UpdateMapServerSettingsRequest
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
/// Lightweight service summary.
/// </summary>
public sealed class ServiceSummary
{
    /// <summary>The service name.</summary>
    public string ServiceName { get; init; } = "";

    /// <summary>The service description.</summary>
    public string Description { get; init; } = "";
}
