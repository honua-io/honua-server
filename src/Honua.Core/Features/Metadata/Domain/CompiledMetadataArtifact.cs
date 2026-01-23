// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Honua.Core.Features.Metadata.Domain;

/// <summary>
/// Represents a compiled artifact derived from a metadata resource spec.
/// </summary>
public sealed class CompiledMetadataArtifact
{
    /// <summary>
    /// Resource identifier associated with this artifact.
    /// </summary>
    [JsonPropertyName("resourceId")]
    public string? ResourceId { get; init; }

    /// <summary>
    /// API version used to compile the artifact.
    /// </summary>
    [JsonPropertyName("apiVersion")]
    public string? ApiVersion { get; init; }

    /// <summary>
    /// Resource kind used to compile the artifact.
    /// </summary>
    [JsonPropertyName("kind")]
    public string? Kind { get; init; }

    /// <summary>
    /// Resource version used to compile the artifact.
    /// </summary>
    [JsonPropertyName("resourceVersion")]
    public string? ResourceVersion { get; init; }

    /// <summary>
    /// Normalized spec used for compilation.
    /// </summary>
    [JsonPropertyName("spec")]
    public JsonElement Spec { get; init; }

    /// <summary>
    /// Timestamp when the artifact was generated.
    /// </summary>
    [JsonPropertyName("generatedAt")]
    public DateTimeOffset GeneratedAt { get; init; }

    /// <summary>
    /// Compiler version for compatibility checks.
    /// </summary>
    [JsonPropertyName("compilerVersion")]
    public string? CompilerVersion { get; init; }
}
