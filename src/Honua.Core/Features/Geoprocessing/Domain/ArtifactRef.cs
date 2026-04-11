// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Geoprocessing.Domain;

/// <summary>
/// Typed reference to an output artifact produced by a geoprocessing workflow.
/// </summary>
public sealed record ArtifactRef
{
    /// <summary>
    /// Unique identifier for this artifact.
    /// </summary>
    public required string ArtifactId { get; init; }

    /// <summary>
    /// Category of the artifact.
    /// </summary>
    public required ArtifactKind Kind { get; init; }

    /// <summary>
    /// Human-readable name for the artifact.
    /// </summary>
    public required string Label { get; init; }

    /// <summary>
    /// Location of the artifact when materialized.
    /// </summary>
    public string? Uri { get; init; }

    /// <summary>
    /// MIME type of the artifact content when applicable.
    /// </summary>
    public string? ContentType { get; init; }

    /// <summary>
    /// Opaque metadata associated with the artifact.
    /// </summary>
    public IReadOnlyDictionary<string, string> Metadata { get; init; } = new Dictionary<string, string>();
}
