// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Import.Domain;

/// <summary>
/// Request to scan an OGC service endpoint for migration planning.
/// </summary>
public sealed record OgcServiceScanRequest
{
    /// <summary>
    /// OGC service kind to scan. Supported values are <c>WFS</c>, <c>WMS</c>, and <c>WMTS</c>.
    /// </summary>
    public required string ServiceType { get; init; }

    /// <summary>
    /// Source service URL. It may be a service root or an existing GetCapabilities URL.
    /// </summary>
    public required string ServiceUrl { get; init; }

    /// <summary>
    /// Optional preferred service version such as <c>2.0.0</c>, <c>1.1.0</c>, or <c>1.0.0</c>.
    /// </summary>
    public string? Version { get; init; }

    /// <summary>
    /// Optional scan timeout in seconds.
    /// </summary>
    public int TimeoutSeconds { get; init; } = 60;

    /// <summary>
    /// Whether HTTP and local URLs are allowed for test or controlled operator environments.
    /// </summary>
    public bool AllowUnsafeLocalUrls { get; init; }
}
