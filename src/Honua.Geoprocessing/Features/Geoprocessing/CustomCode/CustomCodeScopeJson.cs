// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Geoprocessing.CustomCode;

/// <summary>
/// Wire shape of one <c>customcode.declared_scope</c> entry:
/// <c>{"serviceId":"x","layerId":"y","access":"read|write"}</c>.
/// </summary>
internal sealed class DeclaredScopeDto
{
    /// <summary>Target service identifier (required).</summary>
    [JsonPropertyName("serviceId")]
    public string? ServiceId { get; set; }

    /// <summary>Target layer identifier, or null for a service-wide entry.</summary>
    [JsonPropertyName("layerId")]
    public string? LayerId { get; set; }

    /// <summary>Requested access (<c>read</c> or <c>write</c>); defaults to read.</summary>
    [JsonPropertyName("access")]
    public string? Access { get; set; }
}

/// <summary>
/// Source-generated JSON context for parsing the custom-code declared scope,
/// keeping the submit path reflection-free (AOT-safe).
/// </summary>
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(List<DeclaredScopeDto>))]
internal sealed partial class CustomCodeScopeJsonContext : JsonSerializerContext
{
}
