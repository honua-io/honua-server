// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;

namespace Honua.Core.Features.Metadata.Domain;

/// <summary>
/// Full manifest version snapshot including the stored manifest JSON.
/// </summary>
public sealed class ManifestVersionEntry
{
    /// <summary>
    /// Unique identifier for this manifest version.
    /// </summary>
    public required string VersionId { get; init; }

    /// <summary>
    /// Hash of the manifest content for deduplication.
    /// </summary>
    public required string ManifestHash { get; init; }

    /// <summary>
    /// Full manifest JSON as stored at apply time.
    /// </summary>
    public required JsonElement ManifestJson { get; init; }

    /// <summary>
    /// Optional human-readable summary of the apply operation.
    /// </summary>
    public string? Summary { get; init; }

    /// <summary>
    /// Optional actor identity that applied the manifest.
    /// </summary>
    public string? Actor { get; init; }

    /// <summary>
    /// Timestamp when the manifest was applied.
    /// </summary>
    public required DateTimeOffset AppliedAt { get; init; }

    /// <summary>
    /// Number of resources in the manifest.
    /// </summary>
    public required int ResourceCount { get; init; }
}

/// <summary>
/// Lightweight summary of a manifest version for list responses.
/// </summary>
public sealed class ManifestVersionSummary
{
    /// <summary>
    /// Unique identifier for this manifest version.
    /// </summary>
    public required string VersionId { get; init; }

    /// <summary>
    /// Hash of the manifest content.
    /// </summary>
    public required string ManifestHash { get; init; }

    /// <summary>
    /// Optional summary text.
    /// </summary>
    public string? Summary { get; init; }

    /// <summary>
    /// Optional actor identity.
    /// </summary>
    public string? Actor { get; init; }

    /// <summary>
    /// Timestamp when the manifest was applied.
    /// </summary>
    public required DateTimeOffset AppliedAt { get; init; }

    /// <summary>
    /// Number of resources in the manifest.
    /// </summary>
    public required int ResourceCount { get; init; }
}
