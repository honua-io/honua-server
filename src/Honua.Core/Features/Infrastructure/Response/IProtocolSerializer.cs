// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.IO.Pipelines;
using Honua.Core.Features.Infrastructure.Http;

namespace Honua.Core.Features.Infrastructure.Response;

/// <summary>
/// Interface for protocol-specific serialization of unified response data.
/// Each protocol (GeoServices, OGC API Features, WFS, OData) implements this to generate
/// their specific response format from the unified ResponseData model.
/// </summary>
/// <typeparam name="TOptions">Protocol-specific serialization options</typeparam>
public interface IProtocolSerializer<in TOptions>
    where TOptions : class
{
    /// <summary>
    /// Protocol identifier for this serializer.
    /// </summary>
    string Protocol { get; }

    /// <summary>
    /// Serializes response data to the target protocol format.
    /// </summary>
    /// <param name="responseData">Unified response data to serialize</param>
    /// <param name="options">Protocol-specific serialization options</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Serialized response with content type</returns>
    ValueTask<SerializedResponse> SerializeAsync(
        ResponseData responseData,
        TOptions options,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Serializes response data to a stream for large responses.
    /// </summary>
    /// <param name="responseData">Unified response data to serialize</param>
    /// <param name="outputStream">Target stream for output</param>
    /// <param name="options">Protocol-specific serialization options</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Content type of the streamed response</returns>
    ValueTask<string> SerializeToStreamAsync(
        ResponseData responseData,
        PipeWriter outputStream,
        TOptions options,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Serializes streaming response data as it becomes available.
    /// </summary>
    /// <param name="streamingResponse">Async enumerable of response chunks</param>
    /// <param name="outputStream">Target stream for output</param>
    /// <param name="options">Protocol-specific serialization options</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Content type of the streamed response</returns>
    ValueTask<string> SerializeStreamingAsync(
        IAsyncEnumerable<StreamingResponseChunk> streamingResponse,
        PipeWriter outputStream,
        TOptions options,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the appropriate content type for the given response data and options.
    /// </summary>
    /// <param name="responseData">Response data to inspect</param>
    /// <param name="options">Protocol-specific options</param>
    /// <returns>MIME content type</returns>
    string GetContentType(ResponseData responseData, TOptions options);

    /// <summary>
    /// Gets additional HTTP headers required for this protocol.
    /// </summary>
    /// <param name="responseData">Response data to inspect</param>
    /// <param name="options">Protocol-specific options</param>
    /// <returns>Dictionary of header names and values</returns>
    IReadOnlyDictionary<string, string> GetHeaders(ResponseData responseData, TOptions options);
}

/// <summary>
/// Result of protocol serialization.
/// </summary>
public sealed record SerializedResponse(
    object Data,
    string ContentType,
    IReadOnlyDictionary<string, string>? Headers = null,
    int? StatusCode = null);

/// <summary>
/// Base class for protocol serialization options.
/// </summary>
public abstract record ProtocolSerializationOptions
{
    /// <summary>
    /// Output format preference (json, xml, html, etc.).
    /// </summary>
    public string? Format { get; init; }

    /// <summary>
    /// Whether to include debug information.
    /// </summary>
    public bool IncludeDebugInfo { get; init; }

    /// <summary>
    /// Custom headers to include in the response.
    /// </summary>
    public IReadOnlyDictionary<string, string>? AdditionalHeaders { get; init; }

    /// <summary>
    /// Request context for generating links and metadata.
    /// </summary>
    public IRequestContext? RequestContext { get; init; }
}

/// <summary>
/// Serialization options for GeoServices protocol.
/// </summary>
public sealed record GeoServicesSerializationOptions : ProtocolSerializationOptions
{
    /// <summary>
    /// Whether to use pretty-printed JSON.
    /// </summary>
    public bool PrettyPrint { get; init; }

    /// <summary>
    /// JSONP callback function name.
    /// </summary>
    public string? Callback { get; init; }

    /// <summary>
    /// Whether to include field metadata in responses.
    /// </summary>
    public bool IncludeFieldMetadata { get; init; } = true;

    /// <summary>
    /// Creates default GeoServices options.
    /// </summary>
    public static GeoServicesSerializationOptions Default => new();
}

/// <summary>
/// Serialization options for OGC API Features protocol.
/// </summary>
public sealed record OgcApiFeaturesSerializationOptions : ProtocolSerializationOptions
{
    /// <summary>
    /// Collection ID for generating proper links.
    /// </summary>
    public string? CollectionId { get; init; }

    /// <summary>
    /// Whether to include bbox in feature collection.
    /// </summary>
    public bool IncludeBbox { get; init; }

    /// <summary>
    /// Whether to generate HTML representation for browsers.
    /// </summary>
    public bool AllowHtml { get; init; } = true;

    /// <summary>
    /// Creates default OGC API Features options.
    /// </summary>
    public static OgcApiFeaturesSerializationOptions Default => new();
}

/// <summary>
/// Serialization options for WFS 2.0 protocol.
/// </summary>
public sealed record Wfs20SerializationOptions : ProtocolSerializationOptions
{
    /// <summary>
    /// Feature type name for GML generation.
    /// </summary>
    public string? FeatureTypeName { get; init; }

    /// <summary>
    /// Target namespace URI.
    /// </summary>
    public string? NamespaceUri { get; init; }

    /// <summary>
    /// GML version (3.2 is default for WFS 2.0).
    /// </summary>
    public string GmlVersion { get; init; } = "3.2";

    /// <summary>
    /// Whether to include schema location in output.
    /// </summary>
    public bool IncludeSchemaLocation { get; init; } = true;

    /// <summary>
    /// Creates default WFS 2.0 options.
    /// </summary>
    public static Wfs20SerializationOptions Default => new();
}

/// <summary>
/// Serialization options for OData protocol.
/// </summary>
public sealed record ODataSerializationOptions : ProtocolSerializationOptions
{
    /// <summary>
    /// Metadata level (minimal, full, none).
    /// </summary>
    public string MetadataLevel { get; init; } = "minimal";

    /// <summary>
    /// Whether to include @odata.context.
    /// </summary>
    public bool IncludeContext { get; init; } = true;

    /// <summary>
    /// Whether to include @odata.count.
    /// </summary>
    public bool IncludeCount { get; init; }

    /// <summary>
    /// Entity set name for context generation.
    /// </summary>
    public string? EntitySet { get; init; }

    /// <summary>
    /// Creates default OData options.
    /// </summary>
    public static ODataSerializationOptions Default => new();
}
