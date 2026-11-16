// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Core.Features.Spec.Canonical;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = false,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(CanonicalSpecHeader))]
[JsonSerializable(typeof(CanonicalSpecCapabilities))]
public sealed partial class SpecJsonContext : JsonSerializerContext
{
}

public sealed record CanonicalSpecHeader(
    [property: JsonPropertyName("$schema")] string Schema,
    string Grammar,
    CanonicalSpecCapabilities Capabilities,
    string? Kind,
    string? Title);

public sealed record CanonicalSpecCapabilities(string Operators);
