// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Http;
using Honua.Core.Features.Metadata.Domain;

namespace Honua.Core.Features.Metadata.Abstractions;

/// <summary>
/// Protocol-specific formatter for converting unified metadata to protocol capabilities format.
/// Implementations handle the specifics of each protocol while working from shared metadata.
/// </summary>
/// <typeparam name="TCapabilities">Protocol-specific capabilities type</typeparam>
public interface ICapabilitiesFormatter<TCapabilities>
{
    /// <summary>
    /// Formats unified service metadata into protocol-specific capabilities document.
    /// </summary>
    /// <param name="serviceMetadata">Unified service metadata</param>
    /// <param name="globalCapabilities">Global server capabilities</param>
    /// <param name="context">HTTP context for request details</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Protocol-specific capabilities document</returns>
    Task<TCapabilities> FormatServiceCapabilitiesAsync(
        ServiceMetadata serviceMetadata,
        GlobalCapabilities globalCapabilities,
        IRequestContext context,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Formats unified layer metadata into protocol-specific layer description.
    /// </summary>
    /// <param name="layerMetadata">Unified layer metadata</param>
    /// <param name="serviceMetadata">Parent service metadata</param>
    /// <param name="globalCapabilities">Global server capabilities</param>
    /// <param name="context">HTTP context for request details</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Protocol-specific layer description</returns>
    Task<TCapabilities> FormatLayerCapabilitiesAsync(
        LayerMetadata layerMetadata,
        ServiceMetadata serviceMetadata,
        GlobalCapabilities globalCapabilities,
        IRequestContext context,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Protocol identifier for this formatter.
    /// </summary>
    string Protocol { get; }

    /// <summary>
    /// Supported MIME types for the formatted output.
    /// </summary>
    IReadOnlyList<string> SupportedMediaTypes { get; }
}

/// <summary>
/// Extended capabilities formatter that supports global capabilities generation.
/// Used for protocols that need server-wide capabilities documents.
/// </summary>
/// <typeparam name="TCapabilities">Protocol-specific capabilities type</typeparam>
public interface IGlobalCapabilitiesFormatter<TCapabilities> : ICapabilitiesFormatter<TCapabilities>
{
    /// <summary>
    /// Formats global server capabilities into protocol-specific format.
    /// Used for endpoints like WFS GetCapabilities or OGC API Features landing page.
    /// </summary>
    /// <param name="globalCapabilities">Global server capabilities</param>
    /// <param name="availableServices">All available services</param>
    /// <param name="context">HTTP context for request details</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Protocol-specific global capabilities document</returns>
    Task<TCapabilities> FormatGlobalCapabilitiesAsync(
        GlobalCapabilities globalCapabilities,
        IReadOnlyList<ServiceMetadata> availableServices,
        IRequestContext context,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Capabilities formatter that supports multiple output formats.
/// </summary>
/// <typeparam name="TCapabilities">Protocol-specific capabilities type</typeparam>
public interface IMultiFormatCapabilitiesFormatter<TCapabilities> : ICapabilitiesFormatter<TCapabilities>
{
    /// <summary>
    /// Formats capabilities with specific output format.
    /// </summary>
    /// <param name="serviceMetadata">Unified service metadata</param>
    /// <param name="globalCapabilities">Global server capabilities</param>
    /// <param name="outputFormat">Requested output format (e.g., "json", "xml", "html")</param>
    /// <param name="context">HTTP context for request details</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Protocol-specific capabilities document in requested format</returns>
    Task<object> FormatCapabilitiesAsync(
        ServiceMetadata serviceMetadata,
        GlobalCapabilities globalCapabilities,
        string outputFormat,
        IRequestContext context,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets supported output formats for this formatter.
    /// </summary>
    /// <returns>List of supported format identifiers</returns>
    IReadOnlyList<string> GetSupportedFormats();
}