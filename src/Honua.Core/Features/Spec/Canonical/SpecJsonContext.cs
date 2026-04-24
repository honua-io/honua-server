// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Core.Features.Spec.Canonical;

/// <summary>
/// Source-generated JSON context used for canonical-form DTOs. The canonical
/// emitter writes its own <see cref="System.Text.Json.Utf8JsonWriter"/>
/// stream, but a small handful of reader helpers and DTO round-trip paths
/// benefit from a precompiled context to stay AOT-safe.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = false,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(CanonicalSpecHeader))]
[JsonSerializable(typeof(CanonicalSpecCapabilities))]
public sealed partial class SpecJsonContext : JsonSerializerContext
{
}

/// <summary>
/// Header fields embedded at the top of every canonical spec. Present as a
/// serializable DTO to keep AOT round-trip consistency (<c>grammar</c> and
/// <c>schema</c> values are written by the canonical emitter but can be
/// read back structurally via this context).
/// </summary>
/// <param name="Schema">JSON Schema URL (<c>$schema</c>).</param>
/// <param name="Grammar">Grammar version (e.g. <c>v1.0</c>).</param>
/// <param name="Capabilities">Capability-version envelope.</param>
/// <param name="Kind">Declared document kind.</param>
/// <param name="Title">Optional human title.</param>
public sealed record CanonicalSpecHeader(
    [property: JsonPropertyName("$schema")] string Schema,
    string Grammar,
    CanonicalSpecCapabilities Capabilities,
    string? Kind,
    string? Title);

/// <summary>
/// Operator-capability envelope embedded in the canonical header.
/// </summary>
/// <param name="Operators">Operator-catalog capability version.</param>
public sealed record CanonicalSpecCapabilities(string Operators);
