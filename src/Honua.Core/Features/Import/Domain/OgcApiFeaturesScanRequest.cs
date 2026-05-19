// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Import.Domain;

/// <summary>
/// Request to scan an OGC API Features landing page for migration planning.
/// </summary>
public sealed record OgcApiFeaturesScanRequest
{
    /// <summary>
    /// Source OGC API Features landing page URL.
    /// </summary>
    public required string ServiceUrl { get; init; }

    /// <summary>
    /// Optional scan timeout in seconds.
    /// </summary>
    public int TimeoutSeconds { get; init; } = 60;

    /// <summary>
    /// Maximum number of collections to inspect during the bounded scan.
    /// </summary>
    public int MaxCollections { get; init; } = 100;

    /// <summary>
    /// Number of features requested from each items endpoint probe.
    /// </summary>
    public int ItemsProbeLimit { get; init; } = 1;

    /// <summary>
    /// Whether HTTP and local URLs are allowed for test or controlled operator environments.
    /// </summary>
    public bool AllowUnsafeLocalUrls { get; init; }
}
