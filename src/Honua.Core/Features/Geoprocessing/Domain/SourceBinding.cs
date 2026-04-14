// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Geoprocessing.Domain;

/// <summary>
/// Describes a data source bound to a package, including its protocol and network location.
/// </summary>
public sealed record SourceBinding
{
    /// <summary>
    /// Unique identifier for this source within the package.
    /// </summary>
    public required string SourceId { get; init; }

    /// <summary>
    /// Protocol used to access the source.
    /// </summary>
    public required SourceProtocol Protocol { get; init; }

    /// <summary>
    /// Network locator for the source endpoint.
    /// </summary>
    public required SourceLocator Locator { get; init; }

    /// <summary>
    /// Optional server-side filter expression applied to the source.
    /// </summary>
    public string? Filter { get; init; }

    /// <summary>
    /// Opaque metadata associated with the source binding.
    /// </summary>
    public IReadOnlyDictionary<string, string> Metadata { get; init; } = new Dictionary<string, string>();
}

/// <summary>
/// Network locator for a source endpoint.
/// </summary>
public sealed record SourceLocator
{
    /// <summary>
    /// Base URL of the source service.
    /// </summary>
    public required string Url { get; init; }

    /// <summary>
    /// Optional service identifier within the endpoint.
    /// </summary>
    public string? ServiceId { get; init; }

    /// <summary>
    /// Optional layer identifier within the service.
    /// </summary>
    public string? LayerId { get; init; }
}
