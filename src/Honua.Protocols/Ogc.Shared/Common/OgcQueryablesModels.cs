// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace Honua.Protocols.Ogc.Common;

public sealed record QueryablesSchema
{
    [JsonPropertyName("$id")]
    public string? Id { get; init; }

    [JsonPropertyName("$schema")]
    public string Schema { get; init; } = "https://json-schema.org/draft/2020-12/schema";

    [JsonPropertyName("type")]
    public string Type { get; init; } = "object";

    [JsonPropertyName("title")]
    public required string Title { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("properties")]
    public required ImmutableDictionary<string, JsonSchemaProperty> Properties { get; init; }

    [JsonPropertyName("required")]
    public ImmutableArray<string>? Required { get; init; }

    [JsonPropertyName("additionalProperties")]
    public bool AdditionalProperties { get; init; } = false;

    [JsonPropertyName("queryables")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ImmutableArray<LegacyQueryable>? LegacyQueryables { get; init; }
}

public sealed record LegacyQueryable
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("type")]
    public required string Type { get; init; }
}

public sealed record JsonSchemaProperty
{
    [JsonPropertyName("type")]
    public required string Type { get; init; }

    [JsonPropertyName("title")]
    public string? Title { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("format")]
    public string? Format { get; init; }

    [JsonPropertyName("maxLength")]
    public int? MaxLength { get; init; }

    [JsonPropertyName("default")]
    public object? Default { get; init; }

    [JsonPropertyName("enum")]
    public ImmutableArray<object>? Enum { get; init; }

    [JsonPropertyName("$ref")]
    public string? Ref { get; init; }
}
