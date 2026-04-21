// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Shared.Models;

namespace Honua.Core.Features.Infrastructure.Response;

/// <summary>
/// Service for building unified response data that can be serialized to any protocol format.
/// Handles common response preparation including feature processing, pagination, and metadata.
/// </summary>
public interface IResponseBuilder
{
    /// <summary>
    /// Builds a feature collection response with standardized metadata and pagination.
    /// </summary>
    /// <param name="queryResult">Query result containing features and metadata</param>
    /// <param name="layer">Layer definition for schema information</param>
    /// <param name="options">Response building options</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Unified response data ready for protocol serialization</returns>
    ValueTask<ResponseData> BuildFeatureCollectionAsync(
        QueryResult<Feature> queryResult,
        LayerDefinition layer,
        ResponseBuildOptions options,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Builds a single feature response with appropriate metadata.
    /// </summary>
    /// <param name="feature">Feature to include in response</param>
    /// <param name="layer">Layer definition for schema information</param>
    /// <param name="options">Response building options</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Unified response data ready for protocol serialization</returns>
    ValueTask<ResponseData> BuildSingleFeatureAsync(
        Feature feature,
        LayerDefinition layer,
        ResponseBuildOptions options,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Builds an error response with standardized error information.
    /// </summary>
    /// <param name="error">Error details to include</param>
    /// <param name="options">Response building options</param>
    /// <returns>Unified error response data</returns>
    ResponseData BuildErrorResponse(
        ResponseError error,
        ResponseBuildOptions? options = null);

    /// <summary>
    /// Builds streaming response data for large result sets.
    /// </summary>
    /// <param name="features">Async enumerable of features</param>
    /// <param name="layer">Layer definition for schema information</param>
    /// <param name="metadata">Response metadata including counts and pagination</param>
    /// <param name="options">Response building options</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Unified streaming response data</returns>
    IAsyncEnumerable<StreamingResponseChunk> BuildStreamingResponseAsync(
        IAsyncEnumerable<Feature> features,
        LayerDefinition layer,
        ResponseMetadata metadata,
        ResponseBuildOptions options,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Options for controlling response building behavior.
/// </summary>
public sealed record ResponseBuildOptions
{
    /// <summary>
    /// Whether to include geometry in the response.
    /// </summary>
    public bool IncludeGeometry { get; init; } = true;

    /// <summary>
    /// Output spatial reference system ID.
    /// </summary>
    public int? OutputSrid { get; init; }

    /// <summary>
    /// Whether to include Z coordinates.
    /// </summary>
    public bool IncludeZ { get; init; } = true;

    /// <summary>
    /// Whether to include M coordinates.
    /// </summary>
    public bool IncludeM { get; init; } = true;

    /// <summary>
    /// Coordinate precision override.
    /// </summary>
    public int? GeometryPrecision { get; init; }

    /// <summary>
    /// Geometry generalization tolerance.
    /// </summary>
    public double? MaxAllowableOffset { get; init; }

    /// <summary>
    /// Fields to include in output (null means all fields).
    /// </summary>
    public string[]? OutFields { get; init; }

    /// <summary>
    /// Base URL for generating links.
    /// </summary>
    public string? BaseUrl { get; init; }

    /// <summary>
    /// Request path for generating continuation links.
    /// </summary>
    public string? RequestPath { get; init; }

    /// <summary>
    /// Query parameters for generating pagination links.
    /// </summary>
    public IReadOnlyDictionary<string, string>? QueryParameters { get; init; }

    /// <summary>
    /// Whether to include additional metadata (varies by protocol).
    /// </summary>
    public bool IncludeMetadata { get; init; } = true;

    /// <summary>
    /// Protocol-specific options.
    /// </summary>
    public IReadOnlyDictionary<string, object?>? ProtocolOptions { get; init; }

    /// <summary>
    /// Creates default options for GeoServices responses.
    /// </summary>
    public static ResponseBuildOptions ForGeoServices(
        bool returnGeometry = true,
        int? outputSrid = null,
        string[]? outFields = null,
        string? baseUrl = null) => new()
        {
            IncludeGeometry = returnGeometry,
            OutputSrid = outputSrid,
            OutFields = outFields,
            BaseUrl = baseUrl,
            ProtocolOptions = new Dictionary<string, object?>
            {
                ["Protocol"] = "GeoServices"
            }
        };

    /// <summary>
    /// Creates default options for OGC API Features responses.
    /// </summary>
    public static ResponseBuildOptions ForOgcApiFeatures(
        bool includeGeometry = true,
        string? baseUrl = null,
        IReadOnlyDictionary<string, string>? queryParams = null) => new()
        {
            IncludeGeometry = includeGeometry,
            BaseUrl = baseUrl,
            QueryParameters = queryParams,
            ProtocolOptions = new Dictionary<string, object?>
            {
                ["Protocol"] = "OGC API Features"
            }
        };

    /// <summary>
    /// Creates default options for WFS 2.0 responses.
    /// </summary>
    public static ResponseBuildOptions ForWfs20(
        bool includeGeometry = true,
        string? baseUrl = null) => new()
        {
            IncludeGeometry = includeGeometry,
            BaseUrl = baseUrl,
            ProtocolOptions = new Dictionary<string, object?>
            {
                ["Protocol"] = "WFS 2.0"
            }
        };

    /// <summary>
    /// Creates default options for OData responses.
    /// </summary>
    public static ResponseBuildOptions ForOData(
        bool includeGeometry = true,
        string? baseUrl = null,
        string[]? selectFields = null) => new()
        {
            IncludeGeometry = includeGeometry,
            BaseUrl = baseUrl,
            OutFields = selectFields,
            ProtocolOptions = new Dictionary<string, object?>
            {
                ["Protocol"] = "OData"
            }
        };
}

/// <summary>
/// Error information for response building.
/// </summary>
public sealed record ResponseError(
    int StatusCode,
    string Code,
    string Message,
    string? Detail = null,
    IReadOnlyList<string>? AdditionalDetails = null,
    string? Target = null)
{
    /// <summary>
    /// Creates a bad request error.
    /// </summary>
    public static ResponseError BadRequest(string message, string? detail = null) =>
        new(400, "BadRequest", message, detail);

    /// <summary>
    /// Creates a not found error.
    /// </summary>
    public static ResponseError NotFound(string message, string? detail = null) =>
        new(404, "NotFound", message, detail);

    /// <summary>
    /// Creates an internal server error.
    /// </summary>
    public static ResponseError InternalError(string message, string? detail = null) =>
        new(500, "InternalError", message, detail);
}

/// <summary>
/// Metadata for streaming responses.
/// </summary>
public sealed record ResponseMetadata(
    long? TotalCount = null,
    int? ReturnedCount = null,
    bool HasMoreResults = false,
    DateTimeOffset? Timestamp = null,
    string? NextLink = null,
    IReadOnlyDictionary<string, object?>? AdditionalMetadata = null);

/// <summary>
/// Chunk of data for streaming responses.
/// </summary>
public sealed record StreamingResponseChunk(
    StreamingChunkType Type,
    object? Data = null,
    IReadOnlyDictionary<string, object?>? Metadata = null);

/// <summary>
/// Type of streaming response chunk.
/// </summary>
public enum StreamingChunkType
{
    /// <summary>
    /// Response header with metadata.
    /// </summary>
    Header,

    /// <summary>
    /// Individual feature data.
    /// </summary>
    Feature,

    /// <summary>
    /// Response footer with final metadata.
    /// </summary>
    Footer,

    /// <summary>
    /// Error information.
    /// </summary>
    Error
}