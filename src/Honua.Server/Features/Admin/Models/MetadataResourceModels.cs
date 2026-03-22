// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;
using Honua.Core.Features.Metadata.Domain;

namespace Honua.Server.Features.Admin.Models;

/// <summary>
/// Response payload for admin metadata manifest export.
/// </summary>
public sealed class MetadataManifest
{
    /// <summary>
    /// API version of the manifest payload.
    /// </summary>
    [JsonPropertyName("apiVersion")]
    public string ApiVersion { get; init; } = string.Empty;

    /// <summary>
    /// Timestamp when the manifest was generated.
    /// </summary>
    [JsonPropertyName("generatedAt")]
    public DateTimeOffset GeneratedAt { get; init; }

    /// <summary>
    /// Resources included in the manifest.
    /// </summary>
    [JsonPropertyName("resources")]
    public IReadOnlyList<MetadataResource> Resources { get; init; } = Array.Empty<MetadataResource>();

    /// <summary>
    /// Resource identifiers that have drifted since the last applied manifest.
    /// </summary>
    [JsonPropertyName("driftedResources")]
    public IReadOnlyList<MetadataResourceIdentifier> DriftedResources { get; init; } = Array.Empty<MetadataResourceIdentifier>();

    /// <summary>
    /// Hash of the manifest content for change detection.
    /// </summary>
    [JsonPropertyName("manifestHash")]
    public string? ManifestHash { get; init; }
}

/// <summary>
/// Request payload for applying a metadata manifest.
/// </summary>
public sealed class ManifestApplyRequest
{
    /// <summary>
    /// Resources to apply.
    /// </summary>
    [JsonPropertyName("resources")]
    public IReadOnlyList<MetadataResource> Resources { get; init; } = Array.Empty<MetadataResource>();

    /// <summary>
    /// When true, no changes are persisted.
    /// </summary>
    [JsonPropertyName("dryRun")]
    public bool DryRun { get; init; }

    /// <summary>
    /// When true, resources not present in the manifest are removed.
    /// </summary>
    [JsonPropertyName("prune")]
    public bool Prune { get; init; }

    /// <summary>
    /// When true, the manifest is queued for approval instead of being applied immediately.
    /// Requires the enterprise edition approval workflow feature.
    /// </summary>
    [JsonPropertyName("approvalRequired")]
    public bool ApprovalRequired { get; init; }

    /// <summary>
    /// Identity of the actor requesting the manifest apply.
    /// </summary>
    [JsonPropertyName("requestedBy")]
    public string? RequestedBy { get; init; }

    /// <summary>
    /// Free-form reason for the manifest change request.
    /// </summary>
    [JsonPropertyName("requestedReason")]
    public string? RequestedReason { get; init; }
}

/// <summary>
/// Result payload for manifest apply operations.
/// </summary>
public sealed class ManifestApplyResult
{
    /// <summary>
    /// Indicates whether the apply was a dry run.
    /// </summary>
    [JsonPropertyName("dryRun")]
    public bool DryRun { get; init; }

    /// <summary>
    /// Summary counts for applied changes.
    /// </summary>
    [JsonPropertyName("summary")]
    public ManifestApplySummary Summary { get; init; } = new();

    /// <summary>
    /// Entries describing per-resource actions.
    /// </summary>
    [JsonPropertyName("entries")]
    public IReadOnlyList<ManifestApplyEntry> Entries { get; init; } = Array.Empty<ManifestApplyEntry>();
}

/// <summary>
/// Summary of manifest apply actions.
/// </summary>
public sealed class ManifestApplySummary
{
    /// <summary>
    /// Number of resources created.
    /// </summary>
    [JsonPropertyName("created")]
    public int Created { get; init; }

    /// <summary>
    /// Number of resources updated.
    /// </summary>
    [JsonPropertyName("updated")]
    public int Updated { get; init; }

    /// <summary>
    /// Number of resources deleted.
    /// </summary>
    [JsonPropertyName("deleted")]
    public int Deleted { get; init; }

    /// <summary>
    /// Number of resources skipped (no change).
    /// </summary>
    [JsonPropertyName("skipped")]
    public int Skipped { get; init; }
}

/// <summary>
/// Manifest apply entry describing a single resource action.
/// </summary>
public sealed class ManifestApplyEntry
{
    /// <summary>
    /// Action performed (create/update/delete/skip).
    /// </summary>
    [JsonPropertyName("action")]
    public string Action { get; init; } = string.Empty;

    /// <summary>
    /// Resource identifier.
    /// </summary>
    [JsonPropertyName("resource")]
    public MetadataResourceIdentifier Resource { get; init; } = new(string.Empty, string.Empty, string.Empty);

    /// <summary>
    /// Optional message for the entry.
    /// </summary>
    [JsonPropertyName("message")]
    public string? Message { get; init; }
}

/// <summary>
/// Admin version information response.
/// </summary>
public sealed class AdminVersionResponse
{
    /// <summary>
    /// Server version string.
    /// </summary>
    [JsonPropertyName("version")]
    public string Version { get; init; } = string.Empty;

    /// <summary>
    /// Metadata API version supported by the server.
    /// </summary>
    [JsonPropertyName("metadataApiVersion")]
    public string MetadataApiVersion { get; init; } = string.Empty;

    /// <summary>
    /// Server time in UTC.
    /// </summary>
    [JsonPropertyName("serverTime")]
    public DateTimeOffset ServerTime { get; init; }
}

/// <summary>
/// Admin capabilities response payload.
/// </summary>
public sealed class AdminCapabilitiesResponse
{
    /// <summary>
    /// Supported metadata API versions.
    /// </summary>
    [JsonPropertyName("metadataApiVersions")]
    public IReadOnlyList<string> MetadataApiVersions { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Supported resource kinds.
    /// </summary>
    [JsonPropertyName("resourceKinds")]
    public IReadOnlyList<string> ResourceKinds { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Indicates manifest export/apply support.
    /// </summary>
    [JsonPropertyName("manifestSupported")]
    public bool ManifestSupported { get; init; }

    /// <summary>
    /// Indicates whether dry-run is supported for manifest apply.
    /// </summary>
    [JsonPropertyName("manifestDryRunSupported")]
    public bool ManifestDryRunSupported { get; init; }

    /// <summary>
    /// Indicates whether prune is supported for manifest apply.
    /// </summary>
    [JsonPropertyName("manifestPruneSupported")]
    public bool ManifestPruneSupported { get; init; }

    /// <summary>
    /// Canonical compatibility contract for SDK startup handshakes.
    /// </summary>
    [JsonPropertyName("compatibility")]
    public AdminCompatibilityMetadata Compatibility { get; init; } = new();
}

/// <summary>
/// SDK-facing compatibility metadata for the control plane.
/// </summary>
public sealed class AdminCompatibilityMetadata
{
    /// <summary>
    /// Server version reported by the runtime build.
    /// </summary>
    [JsonPropertyName("serverVersion")]
    public string ServerVersion { get; init; } = string.Empty;

    /// <summary>
    /// Release channel inferred from build metadata.
    /// </summary>
    [JsonPropertyName("releaseChannel")]
    public string ReleaseChannel { get; init; } = "stable";

    /// <summary>
    /// Control-plane API compatibility markers.
    /// </summary>
    [JsonPropertyName("controlPlaneApi")]
    public AdminControlPlaneApiCompatibility ControlPlaneApi { get; init; } = new();

    /// <summary>
    /// Supported metadata schema versions and their deprecation state.
    /// </summary>
    [JsonPropertyName("metadataSchemas")]
    public IReadOnlyList<AdminMetadataSchemaCompatibility> MetadataSchemas { get; init; } = Array.Empty<AdminMetadataSchemaCompatibility>();

    /// <summary>
    /// Coarse feature flags for SDK capability branching.
    /// </summary>
    [JsonPropertyName("features")]
    public AdminCompatibilityFeatureFlags Features { get; init; } = new();
}

/// <summary>
/// Control-plane API compatibility markers.
/// </summary>
public sealed class AdminControlPlaneApiCompatibility
{
    /// <summary>
    /// Supported control-plane major version.
    /// </summary>
    [JsonPropertyName("major")]
    public int Major { get; init; }

    /// <summary>
    /// Versioned base path for this control-plane API major.
    /// </summary>
    [JsonPropertyName("basePath")]
    public string BasePath { get; init; } = string.Empty;

    /// <summary>
    /// Indicates that this control-plane API major is deprecated.
    /// </summary>
    [JsonPropertyName("deprecated")]
    public bool Deprecated { get; init; }
}

/// <summary>
/// Metadata schema compatibility markers.
/// </summary>
public sealed class AdminMetadataSchemaCompatibility
{
    /// <summary>
    /// Metadata schema version.
    /// </summary>
    [JsonPropertyName("version")]
    public string Version { get; init; } = string.Empty;

    /// <summary>
    /// Indicates that the schema version is deprecated.
    /// </summary>
    [JsonPropertyName("deprecated")]
    public bool Deprecated { get; init; }
}

/// <summary>
/// Coarse feature flags used by SDKs to branch without endpoint probing.
/// </summary>
public sealed class AdminCompatibilityFeatureFlags
{
    /// <summary>
    /// Indicates support for metadata resource CRUD endpoints.
    /// </summary>
    [JsonPropertyName("metadataResources")]
    public bool MetadataResources { get; init; }

    /// <summary>
    /// Indicates support for manifest export.
    /// </summary>
    [JsonPropertyName("manifestExport")]
    public bool ManifestExport { get; init; }

    /// <summary>
    /// Indicates support for manifest apply.
    /// </summary>
    [JsonPropertyName("manifestApply")]
    public bool ManifestApply { get; init; }

    /// <summary>
    /// Indicates support for manifest dry runs.
    /// </summary>
    [JsonPropertyName("manifestDryRun")]
    public bool ManifestDryRun { get; init; }

    /// <summary>
    /// Indicates support for manifest pruning.
    /// </summary>
    [JsonPropertyName("manifestPrune")]
    public bool ManifestPrune { get; init; }

    /// <summary>
    /// Indicates support for manifest approval workflows (enterprise feature).
    /// </summary>
    [JsonPropertyName("manifestApproval")]
    public bool ManifestApproval { get; init; }
}

/// <summary>
/// Compilation status representation for metadata resources.
/// </summary>
public sealed class MetadataCompilationStatus
{
    /// <summary>
    /// Indicates whether compilation was successful.
    /// </summary>
    [JsonPropertyName("ready")]
    public bool Ready { get; init; }

    /// <summary>
    /// Timestamp when compilation occurred.
    /// </summary>
    [JsonPropertyName("compiledAt")]
    public DateTimeOffset CompiledAt { get; init; }

    /// <summary>
    /// Compiler version string.
    /// </summary>
    [JsonPropertyName("compilerVersion")]
    public string? CompilerVersion { get; init; }
}
