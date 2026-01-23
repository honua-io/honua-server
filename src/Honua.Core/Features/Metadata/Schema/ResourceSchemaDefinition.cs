// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Metadata.Schema;

/// <summary>
/// Defines required spec fields for a resource kind at a specific API version.
/// </summary>
public sealed record ResourceSchemaDefinition
{
    /// <summary>
    /// API version for this schema definition.
    /// </summary>
    public required string ApiVersion { get; init; }

    /// <summary>
    /// Resource kind for this schema definition.
    /// </summary>
    public required string Kind { get; init; }

    /// <summary>
    /// Human-readable description of the schema.
    /// </summary>
    public string? Description { get; init; }

    /// <summary>
    /// Required top-level fields within the spec object.
    /// </summary>
    public IReadOnlyList<string> RequiredSpecFields { get; init; } = Array.Empty<string>();
}
