// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using Honua.Core.Features.Shared.Models;

namespace Honua.Protocols.OData.Models;

public sealed class ServiceDocument
{
    [JsonPropertyName("@odata.context")]
    public string? Context { get; init; }

    [JsonPropertyName("value")]
    public required EntitySet[] Value { get; init; }
}

public sealed class EntitySet
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("kind")]
    public string Kind { get; init; } = "EntitySet";

    [JsonPropertyName("url")]
    public required string Url { get; init; }
}

public sealed record ODataLink : ILink
{
    public required string Href { get; init; }

    public string? Rel { get; init; }

    public string? Type { get; init; }

    public string? Title { get; init; }

    public static ODataLink CreateNextLink(string href)
        => new()
        {
            Href = href,
            Rel = "next",
            Type = "application/json",
            Title = "Next Page"
        };
}

public sealed class ODataResponse : ICollectionResponse<object>
{
    [JsonPropertyName("@odata.context")]
    public string? Context { get; init; }

    [JsonPropertyName("@odata.count")]
    public long? Count { get; init; }

    [JsonPropertyName("@odata.nextLink")]
    public string? NextLink { get; init; }

    [JsonPropertyName("@odata.deltaLink")]
    public string? DeltaLink { get; init; }

    [JsonPropertyName("value")]
    public required object[] Value { get; init; }

    // ICollectionResponse implementation
    ImmutableArray<object> ICollectionResponse<object>.Items => Value.ToImmutableArray();
    ImmutableArray<ILink>? ICollectionResponse<object>.Links =>
        NextLink != null
            ? ImmutableArray.Create<ILink>(ODataLink.CreateNextLink(NextLink))
            : null;
    IPaginationMetadata? ICollectionResponse<object>.Pagination =>
        Count.HasValue || Value.Length > 0
            ? PaginationMetadata.Create(Count, Value.Length, NextLink != null)
            : null;
}

public sealed class ODataError
{
    [JsonPropertyName("error")]
    public required ErrorDetails Error { get; init; }
}

public sealed class ErrorDetails
{
    [JsonPropertyName("code")]
    public required string Code { get; init; }

    [JsonPropertyName("message")]
    public required string Message { get; init; }

    [JsonPropertyName("details")]
    public ErrorDetail[]? Details { get; init; }

    [JsonPropertyName("retryable")]
    public bool? Retryable { get; init; }

    [JsonPropertyName("retryAfterSeconds")]
    public int? RetryAfterSeconds { get; init; }
}

public sealed class ErrorDetail
{
    [JsonPropertyName("code")]
    public required string Code { get; init; }

    [JsonPropertyName("message")]
    public required string Message { get; init; }

    [JsonPropertyName("target")]
    public string? Target { get; init; }
}

public sealed class ODataFeatureRequest
{
    private ODataSpatialGeometry? _geometry;

    [JsonPropertyName("Geometry")]
    public ODataSpatialGeometry? Geometry
    {
        get => _geometry;
        set
        {
            _geometry = value;
            GeometrySpecified = true;
        }
    }

    [JsonIgnore]
    public bool GeometrySpecified { get; private set; }

    [JsonPropertyName("Attributes")]
    public Dictionary<string, object?>? Attributes { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
}

public sealed class ODataFeatureResponse
{
    [JsonPropertyName("@odata.context")]
    public string? Context { get; init; }

    [JsonPropertyName("ObjectId")]
    public long ObjectId { get; init; }

    [JsonPropertyName("LayerId")]
    public int LayerId { get; init; }

    [JsonPropertyName("Geometry")]
    public ODataSpatialGeometry? Geometry { get; init; }

    [JsonPropertyName("Attributes")]
    public Dictionary<string, object?>? Attributes { get; init; }
}

public sealed class ODataSpatialCrs
{
    [JsonPropertyName("type")]
    public string? Type { get; init; }

    [JsonPropertyName("properties")]
    public ODataSpatialCrsProperties? Properties { get; init; }
}

public sealed class ODataSpatialCrsProperties
{
    [JsonPropertyName("name")]
    public string? Name { get; init; }
}

public sealed class ODataSpatialGeometry
{
    [JsonPropertyName("type")]
    public required string Type { get; init; }

    [JsonPropertyName("coordinates")]
    [JsonConverter(typeof(RawJsonStringConverter))]
    public string? CoordinatesJson { get; init; }

    [JsonPropertyName("geometries")]
    [JsonConverter(typeof(RawJsonStringConverter))]
    public string? GeometriesJson { get; init; }

    [JsonPropertyName("crs")]
    public ODataSpatialCrs? Crs { get; init; }
}

internal sealed class RawJsonStringConverter : JsonConverter<string?>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        return reader.TokenType switch
        {
            JsonTokenType.Null => null,
            JsonTokenType.String => reader.GetString(),
            JsonTokenType.StartObject or JsonTokenType.StartArray => JsonDocument.ParseValue(ref reader).RootElement.GetRawText(),
            _ => throw new JsonException("Unsupported JSON token for raw JSON value.")
        };
    }

    public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteNullValue();
        }
        else
        {
            writer.WriteRawValue(value);
        }
    }
}
