// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.Infrastructure.Http;
using Honua.Core.Features.Metadata.Domain;

namespace Honua.Core.Features.Metadata.Abstractions;

/// <summary>
/// Provides unified metadata collection from services and layers for protocol-specific formatting.
/// Centralizes metadata gathering to ensure consistency across all protocols.
/// </summary>
public interface IMetadataProvider
{
    /// <summary>
    /// Collects comprehensive service metadata for protocol-specific formatting.
    /// </summary>
    /// <param name="context">HTTP context for request details and authorization</param>
    /// <param name="service">Service definition to generate metadata for</param>
    /// <param name="options">Provider options for controlling metadata generation</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Unified service metadata containing all information needed by protocols</returns>
    Task<ServiceMetadata> GetServiceMetadataAsync(
        IRequestContext context,
        ServiceDefinition service,
        MetadataProviderOptions options,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Collects comprehensive layer metadata for protocol-specific formatting.
    /// </summary>
    /// <param name="context">HTTP context for request details and authorization</param>
    /// <param name="service">Parent service containing the layer</param>
    /// <param name="layer">Layer definition to generate metadata for</param>
    /// <param name="options">Provider options for controlling metadata generation</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Unified layer metadata containing all information needed by protocols</returns>
    Task<LayerMetadata> GetLayerMetadataAsync(
        IRequestContext context,
        ServiceDefinition service,
        LayerDefinition layer,
        MetadataProviderOptions options,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets global capabilities information shared across all services.
    /// Includes server-wide settings, supported operations, and system limitations.
    /// </summary>
    /// <param name="context">HTTP context for request details</param>
    /// <param name="options">Provider options for controlling metadata generation</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Global capabilities metadata</returns>
    Task<GlobalCapabilities> GetGlobalCapabilitiesAsync(
        IRequestContext context,
        MetadataProviderOptions options,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Options for controlling metadata provider behavior.
/// </summary>
public record MetadataProviderOptions
{
    /// <summary>
    /// Base URL for generating protocol links and references.
    /// </summary>
    public string BaseUrl { get; init; } = string.Empty;

    /// <summary>
    /// Whether to include detailed field metadata (schema information).
    /// </summary>
    public bool IncludeFields { get; init; } = true;

    /// <summary>
    /// Whether to include spatial extent information.
    /// </summary>
    public bool IncludeExtents { get; init; } = true;

    /// <summary>
    /// Whether to include temporal metadata (time fields, temporal capabilities).
    /// </summary>
    public bool IncludeTimeInfo { get; init; } = true;

    /// <summary>
    /// Whether to include styling and drawing information.
    /// </summary>
    public bool IncludeDrawingInfo { get; init; } = true;

    /// <summary>
    /// Whether to include relationship metadata.
    /// </summary>
    public bool IncludeRelationships { get; init; } = true;

    /// <summary>
    /// Whether to calculate expensive metadata (like feature counts and computed extents).
    /// </summary>
    public bool IncludeExpensiveMetadata { get; init; }

    /// <summary>
    /// Maximum time to spend on expensive metadata calculations.
    /// </summary>
    public TimeSpan ExpensiveMetadataTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Additional context-specific parameters for metadata generation.
    /// </summary>
    public Dictionary<string, object> Context { get; init; } = new();

    /// <summary>
    /// Creates default options for fast metadata generation.
    /// </summary>
    public static MetadataProviderOptions Fast(string baseUrl) => new()
    {
        BaseUrl = baseUrl,
        IncludeExpensiveMetadata = false
    };

    /// <summary>
    /// Creates options for comprehensive metadata generation.
    /// </summary>
    public static MetadataProviderOptions Comprehensive(string baseUrl) => new()
    {
        BaseUrl = baseUrl,
        IncludeExpensiveMetadata = true,
        ExpensiveMetadataTimeout = TimeSpan.FromSeconds(10)
    };
}
