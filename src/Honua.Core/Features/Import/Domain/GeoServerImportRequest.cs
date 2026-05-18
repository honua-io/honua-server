// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Import.Domain;

/// <summary>
/// Request to discover workspaces, datastores, layers and styles from a GeoServer instance.
/// </summary>
public sealed record GeoServerDiscoveryRequest
{
    /// <summary>
    /// The base URL of the GeoServer REST API.
    /// Examples:
    /// - https://geoserver.example.com/geoserver/rest
    /// - https://my-geoserver:8080/geoserver/rest
    /// </summary>
    public required string GeoServerRestUrl { get; init; }

    /// <summary>
    /// Optional username for GeoServer authentication.
    /// </summary>
    public string? Username { get; init; }

    /// <summary>
    /// Optional password for GeoServer authentication.
    /// </summary>
    public string? Password { get; init; }

    /// <summary>
    /// Optional timeout for the discovery request in seconds.
    /// </summary>
    public int TimeoutSeconds { get; init; } = 120;

    /// <summary>
    /// Whether to include detailed analysis (compatibility assessment).
    /// </summary>
    public bool IncludeCompatibilityAnalysis { get; init; } = true;

    /// <summary>
    /// Whether to include SLD style content in discovery.
    /// </summary>
    public bool IncludeStyleContent { get; init; }

    /// <summary>
    /// Whether test-only unsafe local GeoServer URLs are allowed for this request.
    /// </summary>
    public bool AllowUnsafeLocalUrls { get; init; }
}

/// <summary>
/// Request to import GeoServer configuration into Honua.
/// </summary>
public sealed record GeoServerImportRequest
{
    /// <summary>
    /// Optional job identifier for progress tracking.
    /// </summary>
    public string? JobId { get; init; }

    /// <summary>
    /// The base URL of the GeoServer REST API.
    /// </summary>
    public required string GeoServerRestUrl { get; init; }

    /// <summary>
    /// Optional username for GeoServer authentication.
    /// </summary>
    public string? Username { get; init; }

    /// <summary>
    /// Optional password for GeoServer authentication.
    /// </summary>
    public string? Password { get; init; }

    /// <summary>
    /// Optional secret reference that resolves the GeoServer password at execution time.
    /// </summary>
    public string? PasswordSecretReference { get; init; }

    /// <summary>
    /// Target Honua server base URL for the migration.
    /// </summary>
    public required string TargetHonuaUrl { get; init; }

    /// <summary>
    /// Honua API key for authentication (if required).
    /// </summary>
    public string? HonuaApiKey { get; init; }

    /// <summary>
    /// Optional secret reference that resolves the Honua API key at execution time.
    /// </summary>
    public string? HonuaApiKeySecretReference { get; init; }

    /// <summary>
    /// Specific workspaces to import (null imports all).
    /// </summary>
    public string[]? WorkspaceNames { get; init; }

    /// <summary>
    /// Specific datastores to import (null imports all).
    /// Format: "workspace:datastore" or just "datastore" for global.
    /// </summary>
    public string[]? DataStoreNames { get; init; }

    /// <summary>
    /// Specific layers to import (null imports all).
    /// Format: "workspace:layer" or just "layer" for global.
    /// </summary>
    public string[]? LayerNames { get; init; }

    /// <summary>
    /// Whether to import SLD styles (requires issue #375 for automatic conversion).
    /// </summary>
    public bool ImportStyles { get; init; }

    /// <summary>
    /// Whether to overwrite existing resources in Honua.
    /// </summary>
    public bool OverwriteExisting { get; init; }

    /// <summary>
    /// Whether to perform a dry run (validation only, no actual import).
    /// </summary>
    public bool DryRun { get; init; }

    /// <summary>
    /// Target coordinate reference system ID (for transformation).
    /// Default is null (preserve source CRS).
    /// </summary>
    public int? TargetSrid { get; init; }

    /// <summary>
    /// Request timeout in seconds for each API call.
    /// </summary>
    public int RequestTimeoutSeconds { get; init; } = 120;

    /// <summary>
    /// Maximum number of retry attempts for failed requests.
    /// </summary>
    public int MaxRetries { get; init; } = 3;

    /// <summary>
    /// Whether to automatically publish imported layers.
    /// </summary>
    public bool AutoPublishLayers { get; init; } = true;

    /// <summary>
    /// Batch size for processing multiple resources.
    /// </summary>
    public int BatchSize { get; init; } = 10;

    /// <summary>
    /// Options for handling unsupported resources.
    /// </summary>
    public GeoServerImportOptions? ImportOptions { get; init; }

    /// <summary>
    /// Whether test-only unsafe local GeoServer URLs are allowed for this request.
    /// </summary>
    public bool AllowUnsafeLocalUrls { get; init; }
}

/// <summary>
/// Additional options for controlling GeoServer import behavior.
/// </summary>
public sealed record GeoServerImportOptions
{
    /// <summary>
    /// How to handle unsupported datastore types.
    /// </summary>
    public UnsupportedResourceBehavior UnsupportedDataStoreBehavior { get; init; } = UnsupportedResourceBehavior.Skip;

    /// <summary>
    /// How to handle unsupported layer types.
    /// </summary>
    public UnsupportedResourceBehavior UnsupportedLayerBehavior { get; init; } = UnsupportedResourceBehavior.Skip;

    /// <summary>
    /// How to handle SLD styles when ImportStyles is true but conversion is not available.
    /// </summary>
    public UnsupportedResourceBehavior UnsupportedStyleBehavior { get; init; } = UnsupportedResourceBehavior.LogWarning;

    /// <summary>
    /// Whether to continue import if individual resources fail.
    /// </summary>
    public bool ContinueOnResourceFailure { get; init; } = true;

    /// <summary>
    /// Custom workspace name mappings for import.
    /// Key: GeoServer workspace name, Value: Target Honua workspace name
    /// </summary>
    public IReadOnlyDictionary<string, string>? WorkspaceNameMappings { get; init; }

    /// <summary>
    /// Default workspace name to use for global resources.
    /// </summary>
    public string DefaultWorkspaceName { get; init; } = "geoserver-import";
}

/// <summary>
/// Behavior for handling unsupported resources during import.
/// </summary>
public enum UnsupportedResourceBehavior
{
    /// <summary>
    /// Skip unsupported resources silently.
    /// </summary>
    Skip,

    /// <summary>
    /// Log warning for unsupported resources and continue.
    /// </summary>
    LogWarning,

    /// <summary>
    /// Fail the entire import if unsupported resources are encountered.
    /// </summary>
    FailImport
}

/// <summary>
/// Result of a GeoServer import operation.
/// </summary>
public sealed record GeoServerImportResult
{
    /// <summary>
    /// Whether the import was successful.
    /// </summary>
    public required bool Success { get; init; }

    /// <summary>
    /// Number of workspaces imported.
    /// </summary>
    public int WorkspacesImported { get; init; }

    /// <summary>
    /// Number of datastores imported.
    /// </summary>
    public int DataStoresImported { get; init; }

    /// <summary>
    /// Number of layers imported.
    /// </summary>
    public int LayersImported { get; init; }

    /// <summary>
    /// Number of styles imported.
    /// </summary>
    public int StylesImported { get; init; }

    /// <summary>
    /// Number of resources that failed to import.
    /// </summary>
    public int FailedResources { get; init; }

    /// <summary>
    /// Number of deterministic apply-plan steps generated without mutating the target catalog.
    /// </summary>
    public int ResourcesPlanned { get; init; }

    /// <summary>
    /// The source GeoServer URL that was imported from.
    /// </summary>
    public required string SourceGeoServerUrl { get; init; }

    /// <summary>
    /// The target Honua URL that was imported to.
    /// </summary>
    public required string TargetHonuaUrl { get; init; }

    /// <summary>
    /// GeoServer version information.
    /// </summary>
    public string? SourceGeoServerVersion { get; init; }

    /// <summary>
    /// Error message if import failed.
    /// </summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// Import duration.
    /// </summary>
    public TimeSpan Duration { get; init; }

    /// <summary>
    /// Warnings encountered during import.
    /// </summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];

    /// <summary>
    /// Detailed breakdown of what was imported.
    /// </summary>
    public IReadOnlyList<GeoServerImportedResource> ImportedResources { get; init; } = [];

    /// <summary>
    /// Resources that failed to import.
    /// </summary>
    public IReadOnlyList<GeoServerFailedResource> FailedResourceDetails { get; init; } = [];

    /// <summary>
    /// Deterministic apply plan generated for non-dry-run migration requests.
    /// </summary>
    public MigrationApplyPlanArtifact? ApplyPlan { get; init; }

    /// <summary>
    /// Whether this was a dry run (no actual changes made).
    /// </summary>
    public bool WasDryRun { get; init; }

    /// <summary>
    /// Create successful import result.
    /// </summary>
    public static GeoServerImportResult CreateSuccess(
        string sourceGeoServerUrl,
        string targetHonuaUrl,
        int workspacesImported = 0,
        int dataStoresImported = 0,
        int layersImported = 0,
        int stylesImported = 0,
        string? sourceGeoServerVersion = null,
        TimeSpan duration = default,
        IReadOnlyList<string>? warnings = null,
        IReadOnlyList<GeoServerImportedResource>? importedResources = null,
        bool wasDryRun = false) =>
        new()
        {
            Success = true,
            SourceGeoServerUrl = sourceGeoServerUrl,
            TargetHonuaUrl = targetHonuaUrl,
            WorkspacesImported = workspacesImported,
            DataStoresImported = dataStoresImported,
            LayersImported = layersImported,
            StylesImported = stylesImported,
            SourceGeoServerVersion = sourceGeoServerVersion,
            Duration = duration,
            Warnings = warnings ?? [],
            ImportedResources = importedResources ?? [],
            WasDryRun = wasDryRun
        };

    /// <summary>
    /// Create failed import result.
    /// </summary>
    public static GeoServerImportResult CreateFailure(
        string sourceGeoServerUrl,
        string targetHonuaUrl,
        string errorMessage,
        TimeSpan duration = default,
        IReadOnlyList<GeoServerFailedResource>? failedResourceDetails = null,
        bool wasDryRun = false) =>
        new()
        {
            Success = false,
            SourceGeoServerUrl = sourceGeoServerUrl,
            TargetHonuaUrl = targetHonuaUrl,
            ErrorMessage = errorMessage,
            Duration = duration,
            FailedResourceDetails = failedResourceDetails ?? [],
            WasDryRun = wasDryRun
        };
}

/// <summary>
/// Information about a successfully imported resource.
/// </summary>
public sealed record GeoServerImportedResource
{
    /// <summary>
    /// Type of resource (Workspace, DataStore, Layer, Style).
    /// </summary>
    public required string ResourceType { get; init; }

    /// <summary>
    /// Name of the resource.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Workspace the resource belongs to.
    /// </summary>
    public string? WorkspaceName { get; init; }

    /// <summary>
    /// ID assigned in Honua (if applicable).
    /// </summary>
    public int? HonuaResourceId { get; init; }

    /// <summary>
    /// Any transformation or modification notes.
    /// </summary>
    public string? Notes { get; init; }
}

/// <summary>
/// Information about a resource that failed to import.
/// </summary>
public sealed record GeoServerFailedResource
{
    /// <summary>
    /// Type of resource (Workspace, DataStore, Layer, Style).
    /// </summary>
    public required string ResourceType { get; init; }

    /// <summary>
    /// Name of the resource.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Workspace the resource belongs to.
    /// </summary>
    public string? WorkspaceName { get; init; }

    /// <summary>
    /// Reason for failure.
    /// </summary>
    public required string ErrorMessage { get; init; }

    /// <summary>
    /// Whether this failure was expected (e.g., unsupported resource type).
    /// </summary>
    public bool IsExpectedFailure { get; init; }
}
