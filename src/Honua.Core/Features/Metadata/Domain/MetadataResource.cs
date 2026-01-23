// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Honua.Core.Features.Metadata.Domain;

/// <summary>
/// Represents a versioned metadata resource envelope with spec and status separation.
/// </summary>
public sealed class MetadataResource
{
    /// <summary>
    /// API version for the resource schema (e.g. "honua.io/v1alpha1").
    /// </summary>
    [JsonPropertyName("apiVersion")]
    public string? ApiVersion { get; init; }

    /// <summary>
    /// Resource kind (e.g. "Layer", "Service").
    /// </summary>
    [JsonPropertyName("kind")]
    public string? Kind { get; init; }

    /// <summary>
    /// Resource metadata including identity and versioning.
    /// </summary>
    [JsonPropertyName("metadata")]
    public ResourceMetadata? Metadata { get; init; }

    /// <summary>
    /// Desired state for the resource.
    /// </summary>
    [JsonPropertyName("spec")]
    public JsonElement Spec { get; init; }

    /// <summary>
    /// Computed or observed state for the resource.
    /// </summary>
    [JsonPropertyName("status")]
    public JsonElement? Status { get; init; }
}
