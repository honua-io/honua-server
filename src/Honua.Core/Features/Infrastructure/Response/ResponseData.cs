// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Core.Features.Metadata.Domain.V2;

namespace Honua.Core.Features.Infrastructure.Response;

/// <summary>
/// Unified response data model containing all information needed for protocol-specific serialization.
/// Acts as an intermediate representation that can be converted to any target format.
/// </summary>
public sealed record ResponseData
{
    /// <summary>
    /// Response type indicating the kind of data contained.
    /// </summary>
    public ResponseType Type { get; init; }

    /// <summary>
    /// Features included in the response.
    /// </summary>
    public ImmutableArray<ResponseFeature> Features { get; init; } = ImmutableArray<ResponseFeature>.Empty;

    /// <summary>
    /// Response metadata including counts, timestamps, and links.
    /// </summary>
    public ResponseMetadata Metadata { get; init; } = new();

    /// <summary>
    /// Error information if this is an error response.
    /// </summary>
    public ResponseError? Error { get; init; }

    /// <summary>
    /// Canonical resource for schema information.
    /// </summary>
    public MetadataV2Resource? Resource { get; init; }

    /// <summary>
    /// Pagination information for large result sets.
    /// </summary>
    public PaginationInfo? Pagination { get; init; }

    /// <summary>
    /// Links related to this response (self, next, previous, etc.).
    /// </summary>
    public ImmutableArray<ResponseLink> Links { get; init; } = ImmutableArray<ResponseLink>.Empty;

    /// <summary>
    /// Protocol-specific options and metadata.
    /// </summary>
    public IReadOnlyDictionary<string, object?>? ProtocolMetadata { get; init; }

    /// <summary>
    /// Creates a feature collection response.
    /// </summary>
    public static ResponseData FeatureCollection(
        IEnumerable<ResponseFeature> features,
        MetadataV2Resource resource,
        ResponseMetadata? metadata = null,
        PaginationInfo? pagination = null,
        IEnumerable<ResponseLink>? links = null,
        IReadOnlyDictionary<string, object?>? protocolMetadata = null) => new()
        {
            Type = ResponseType.FeatureCollection,
            Features = features.ToImmutableArray(),
            Resource = resource,
            Metadata = metadata ?? new(),
            Pagination = pagination,
            Links = links?.ToImmutableArray() ?? ImmutableArray<ResponseLink>.Empty,
            ProtocolMetadata = protocolMetadata
        };

    /// <summary>
    /// Creates a single feature response.
    /// </summary>
    public static ResponseData SingleFeature(
        ResponseFeature feature,
        MetadataV2Resource resource,
        ResponseMetadata? metadata = null,
        IEnumerable<ResponseLink>? links = null,
        IReadOnlyDictionary<string, object?>? protocolMetadata = null) => new()
        {
            Type = ResponseType.SingleFeature,
            Features = ImmutableArray.Create(feature),
            Resource = resource,
            Metadata = metadata ?? new(),
            Links = links?.ToImmutableArray() ?? ImmutableArray<ResponseLink>.Empty,
            ProtocolMetadata = protocolMetadata
        };

    /// <summary>
    /// Creates an error response.
    /// </summary>
    public static ResponseData CreateError(
        ResponseError error,
        ResponseMetadata? metadata = null,
        IReadOnlyDictionary<string, object?>? protocolMetadata = null) => new()
        {
            Type = ResponseType.Error,
            Error = error,
            Metadata = metadata ?? new(),
            ProtocolMetadata = protocolMetadata
        };
}

/// <summary>
/// Feature representation in unified response format.
/// </summary>
public sealed record ResponseFeature
{
    /// <summary>
    /// Feature identifier.
    /// </summary>
    public object Id { get; init; } = null!;

    /// <summary>
    /// Feature geometry in its raw format (WKB, GeoJSON, etc.).
    /// </summary>
    public ResponseGeometry? Geometry { get; init; }

    /// <summary>
    /// Feature attributes/properties.
    /// </summary>
    public ImmutableDictionary<string, object?> Attributes { get; init; } =
        ImmutableDictionary<string, object?>.Empty;

    /// <summary>
    /// Computed fields or runtime properties.
    /// </summary>
    public ImmutableDictionary<string, object?> ComputedFields { get; init; } =
        ImmutableDictionary<string, object?>.Empty;

    /// <summary>
    /// Feature-specific metadata (ETag, update timestamp, etc.).
    /// </summary>
    public IReadOnlyDictionary<string, object?>? Metadata { get; init; }

    /// <summary>
    /// Creates a response feature from a domain feature.
    /// </summary>
    public static ResponseFeature FromDomain(
        Core.Features.FeatureStore.Domain.Feature domainFeature,
        ResponseGeometry? geometry = null,
        IReadOnlyDictionary<string, object?>? metadata = null) => new()
        {
            Id = domainFeature.Id,
            Geometry = geometry,
            Attributes = domainFeature.Attributes,
            Metadata = metadata
        };
}

/// <summary>
/// Geometry representation in unified response format.
/// </summary>
public sealed record ResponseGeometry
{
    /// <summary>
    /// Geometry type (Point, LineString, Polygon, etc.).
    /// </summary>
    public string Type { get; init; } = null!;

    /// <summary>
    /// Spatial reference system identifier.
    /// </summary>
    public int? Srid { get; init; }

    /// <summary>
    /// Whether geometry has Z coordinates.
    /// </summary>
    public bool HasZ { get; init; }

    /// <summary>
    /// Whether geometry has M coordinates.
    /// </summary>
    public bool HasM { get; init; }

    /// <summary>
    /// Raw geometry data in original format (WKB).
    /// </summary>
    public byte[]? RawData { get; init; }

    /// <summary>
    /// Cached format-specific representations.
    /// </summary>
    public IReadOnlyDictionary<string, object?> FormatCache { get; init; } =
        new Dictionary<string, object?>();

    /// <summary>
    /// Geometry envelope/bounds.
    /// </summary>
    public GeometryEnvelope? Envelope { get; init; }

    /// <summary>
    /// Creates a response geometry from WKB data.
    /// </summary>
    public static ResponseGeometry FromWkb(
        byte[] wkbData,
        string geometryType,
        int? srid = null,
        bool hasZ = false,
        bool hasM = false,
        GeometryEnvelope? envelope = null) => new()
        {
            Type = geometryType,
            Srid = srid,
            HasZ = hasZ,
            HasM = hasM,
            RawData = wkbData,
            Envelope = envelope
        };
}

/// <summary>
/// Geometry bounding envelope.
/// </summary>
public sealed record GeometryEnvelope(
    double MinX,
    double MinY,
    double MaxX,
    double MaxY,
    double? MinZ = null,
    double? MaxZ = null,
    double? MinM = null,
    double? MaxM = null);

/// <summary>
/// Pagination information for responses.
/// </summary>
public sealed record PaginationInfo(
    int? Skip = null,
    int? Top = null,
    string? NextToken = null,
    string? PreviousToken = null,
    long? TotalEstimate = null);

/// <summary>
/// Link information for hypermedia responses.
/// </summary>
public sealed record ResponseLink(
    string Rel,
    string Href,
    string? Type = null,
    string? Title = null,
    string? Method = null,
    IReadOnlyDictionary<string, string>? AdditionalProperties = null);

/// <summary>
/// Type of response data.
/// </summary>
public enum ResponseType
{
    /// <summary>
    /// Collection of features.
    /// </summary>
    FeatureCollection,

    /// <summary>
    /// Single feature.
    /// </summary>
    SingleFeature,

    /// <summary>
    /// Error response.
    /// </summary>
    Error,

    /// <summary>
    /// Metadata-only response.
    /// </summary>
    Metadata
}
