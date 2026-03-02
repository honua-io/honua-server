// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;

namespace Honua.Admin.Models;

public sealed class AdminVersionResponse
{
    public string Version { get; init; } = string.Empty;
    public string MetadataApiVersion { get; init; } = string.Empty;
    public DateTimeOffset ServerTime { get; init; }
}

public sealed class AdminCapabilitiesResponse
{
    public string[] MetadataApiVersions { get; init; } = [];
    public string[] ResourceKinds { get; init; } = [];
    public bool ManifestSupported { get; init; }
    public bool ManifestDryRunSupported { get; init; }
    public bool ManifestPruneSupported { get; init; }
}

public sealed class MetadataManifest
{
    public string ApiVersion { get; init; } = string.Empty;
    public DateTimeOffset GeneratedAt { get; init; }
    public MetadataResource[] Resources { get; init; } = [];
    public MetadataResourceIdentifier[] DriftedResources { get; init; } = [];
    public string? ManifestHash { get; init; }
}

public sealed class ManifestApplyRequest
{
    public MetadataResource[] Resources { get; init; } = [];
    public bool DryRun { get; init; }
    public bool Prune { get; init; }
}

public sealed class ManifestApplyResult
{
    public bool DryRun { get; init; }
    public ManifestApplySummary Summary { get; init; } = new();
    public ManifestApplyEntry[] Entries { get; init; } = [];
}

public sealed class ManifestApplySummary
{
    public int Created { get; init; }
    public int Updated { get; init; }
    public int Deleted { get; init; }
    public int Skipped { get; init; }
}

public sealed class ManifestApplyEntry
{
    public string Action { get; init; } = string.Empty;
    public MetadataResourceIdentifier Resource { get; init; } = new(string.Empty, string.Empty, string.Empty);
    public string? Message { get; init; }
}

public sealed class MetadataResource
{
    public string? ApiVersion { get; init; }
    public string? Kind { get; init; }
    public ResourceMetadata? Metadata { get; init; }
    public JsonElement Spec { get; init; }
    public JsonElement? Status { get; init; }
}

public sealed class ResourceMetadata
{
    public string? Id { get; init; }
    public string? Name { get; init; }
    public string? Namespace { get; init; }
    public string? ResourceVersion { get; init; }
    public int Generation { get; init; }
    public DateTimeOffset? CreatedAt { get; init; }
    public DateTimeOffset? UpdatedAt { get; init; }
    public Dictionary<string, string>? Labels { get; init; }
    public Dictionary<string, string>? Annotations { get; init; }
}

public sealed record MetadataResourceIdentifier(
    string Kind,
    string Namespace,
    string Name);

public sealed record MetadataResourceWithEtag(
    MetadataResource Resource,
    string? ETag);
