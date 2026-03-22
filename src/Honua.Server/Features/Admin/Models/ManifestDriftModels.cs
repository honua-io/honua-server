// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;
using Honua.Core.Features.Metadata.Domain;

namespace Honua.Server.Features.Admin.Models;

/// <summary>
/// Response payload for drift detection between declared and actual state.
/// </summary>
public sealed class ManifestDriftReport
{
    /// <summary>
    /// Timestamp when the drift report was generated.
    /// </summary>
    [JsonPropertyName("generatedAt")]
    public DateTimeOffset GeneratedAt { get; init; }

    /// <summary>
    /// Version ID of the latest applied manifest used as the baseline.
    /// </summary>
    [JsonPropertyName("baselineVersionId")]
    public string? BaselineVersionId { get; init; }

    /// <summary>
    /// Whether any drift was detected.
    /// </summary>
    [JsonPropertyName("hasDrift")]
    public bool HasDrift { get; init; }

    /// <summary>
    /// Per-resource drift records.
    /// </summary>
    [JsonPropertyName("resources")]
    public IReadOnlyList<ManifestDriftRecord> Resources { get; init; } = Array.Empty<ManifestDriftRecord>();
}

/// <summary>
/// Drift record for a single resource.
/// </summary>
public sealed class ManifestDriftRecord
{
    /// <summary>
    /// Resource identifier (kind/namespace/name).
    /// </summary>
    [JsonPropertyName("identifier")]
    public MetadataResourceIdentifier Identifier { get; init; } = new(string.Empty, string.Empty, string.Empty);

    /// <summary>
    /// Type of drift detected.
    /// </summary>
    [JsonPropertyName("driftType")]
    public string DriftType { get; init; } = string.Empty;

    /// <summary>
    /// Hash of the declared spec in the manifest.
    /// </summary>
    [JsonPropertyName("declaredHash")]
    public string? DeclaredHash { get; init; }

    /// <summary>
    /// Hash of the actual spec in the resource store.
    /// </summary>
    [JsonPropertyName("actualHash")]
    public string? ActualHash { get; init; }

    /// <summary>
    /// Declared spec from the manifest (when verbose mode is requested).
    /// </summary>
    [JsonPropertyName("declaredSpec")]
    public JsonElement? DeclaredSpec { get; init; }

    /// <summary>
    /// Actual spec from the resource store (when verbose mode is requested).
    /// </summary>
    [JsonPropertyName("actualSpec")]
    public JsonElement? ActualSpec { get; init; }
}

/// <summary>
/// Well-known drift type values.
/// </summary>
public static class DriftTypes
{
    /// <summary>
    /// Resource exists in manifest but not in actual store.
    /// </summary>
    public const string Missing = "missing";

    /// <summary>
    /// Resource exists in actual store but not in manifest.
    /// </summary>
    public const string Extra = "extra";

    /// <summary>
    /// Resource exists in both but spec hashes differ.
    /// </summary>
    public const string SpecDrift = "spec-drift";
}

/// <summary>
/// Lightweight manifest version summary for list responses.
/// </summary>
public sealed class ManifestVersionResponse
{
    /// <summary>
    /// Unique identifier for this manifest version.
    /// </summary>
    [JsonPropertyName("versionId")]
    public string VersionId { get; init; } = string.Empty;

    /// <summary>
    /// Hash of the manifest content.
    /// </summary>
    [JsonPropertyName("manifestHash")]
    public string ManifestHash { get; init; } = string.Empty;

    /// <summary>
    /// Optional summary text.
    /// </summary>
    [JsonPropertyName("summary")]
    public string? Summary { get; init; }

    /// <summary>
    /// Optional actor identity.
    /// </summary>
    [JsonPropertyName("actor")]
    public string? Actor { get; init; }

    /// <summary>
    /// Timestamp when the manifest was applied.
    /// </summary>
    [JsonPropertyName("appliedAt")]
    public DateTimeOffset AppliedAt { get; init; }

    /// <summary>
    /// Number of resources in the manifest.
    /// </summary>
    [JsonPropertyName("resourceCount")]
    public int ResourceCount { get; init; }
}

/// <summary>
/// Full manifest version detail including stored manifest JSON.
/// </summary>
public sealed class ManifestVersionDetailResponse
{
    /// <summary>
    /// Unique identifier for this manifest version.
    /// </summary>
    [JsonPropertyName("versionId")]
    public string VersionId { get; init; } = string.Empty;

    /// <summary>
    /// Hash of the manifest content.
    /// </summary>
    [JsonPropertyName("manifestHash")]
    public string ManifestHash { get; init; } = string.Empty;

    /// <summary>
    /// Optional summary text.
    /// </summary>
    [JsonPropertyName("summary")]
    public string? Summary { get; init; }

    /// <summary>
    /// Optional actor identity.
    /// </summary>
    [JsonPropertyName("actor")]
    public string? Actor { get; init; }

    /// <summary>
    /// Timestamp when the manifest was applied.
    /// </summary>
    [JsonPropertyName("appliedAt")]
    public DateTimeOffset AppliedAt { get; init; }

    /// <summary>
    /// Number of resources in the manifest.
    /// </summary>
    [JsonPropertyName("resourceCount")]
    public int ResourceCount { get; init; }

    /// <summary>
    /// Full manifest JSON as stored at apply time.
    /// </summary>
    [JsonPropertyName("manifest")]
    public JsonElement Manifest { get; init; }
}

/// <summary>
/// List response for manifest versions.
/// </summary>
public sealed class ManifestVersionListResponse
{
    /// <summary>
    /// List of manifest version summaries.
    /// </summary>
    [JsonPropertyName("versions")]
    public IReadOnlyList<ManifestVersionResponse> Versions { get; init; } = Array.Empty<ManifestVersionResponse>();
}
