// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.Migration.Services;
using Honua.Core.Features.FileImport.Abstractions;
using Honua.Core.Features.FileImport.Domain;
using Honua.Core.Features.FileImport.Services;
using Honua.Import;
using Honua.Migration;
using Honua.Import.FileImport;
using Honua.Import.RasterImport;

namespace Honua.Import.Models;

/// <summary>
/// API request model for GeoServer service discovery.
/// </summary>
public sealed record GeoServerDiscoveryApiRequest
{
    /// <summary>
    /// The base URL of the GeoServer REST API.
    /// </summary>
    public string? GeoServerRestUrl { get; init; }

    /// <summary>
    /// Optional username for GeoServer authentication.
    /// </summary>
    public string? Username { get; init; }

    /// <summary>
    /// Optional password for GeoServer authentication.
    /// </summary>
    public string? Password { get; init; }

    /// <summary>
    /// Optional timeout for the discovery request in seconds (default: 120).
    /// </summary>
    public int? TimeoutSeconds { get; init; }

    /// <summary>
    /// Whether to include detailed compatibility analysis (default: true).
    /// </summary>
    public bool? IncludeCompatibilityAnalysis { get; init; }

    /// <summary>
    /// Whether to include SLD style content in discovery (default: false).
    /// </summary>
    public bool? IncludeStyleContent { get; init; }
}

/// <summary>
/// API request model for starting GeoServer import.
/// </summary>
public sealed record GeoServerImportApiRequest
{
    /// <summary>
    /// The base URL of the GeoServer REST API.
    /// </summary>
    public string? GeoServerRestUrl { get; init; }

    /// <summary>
    /// Optional username for GeoServer authentication.
    /// </summary>
    public string? Username { get; init; }

    /// <summary>
    /// Optional password for GeoServer authentication. Discovery accepts plaintext credentials, but queued imports must use <see cref="PasswordSecretReference"/>.
    /// </summary>
    public string? Password { get; init; }

    /// <summary>
    /// Optional secret reference for the GeoServer password used by queued import jobs.
    /// </summary>
    public string? PasswordSecretReference { get; init; }

    /// <summary>
    /// Target Honua server base URL for the migration.
    /// </summary>
    public string? TargetHonuaUrl { get; init; }

    /// <summary>
    /// Honua API key for authentication (if required). Queued imports must use <see cref="HonuaApiKeySecretReference"/>.
    /// </summary>
    public string? HonuaApiKey { get; init; }

    /// <summary>
    /// Optional secret reference for the Honua API key used by queued import jobs.
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
    public bool? ImportStyles { get; init; }

    /// <summary>
    /// Whether to overwrite existing resources in Honua.
    /// </summary>
    public bool? OverwriteExisting { get; init; }

    /// <summary>
    /// Whether to perform a dry run (validation only, no actual import).
    /// </summary>
    public bool? DryRun { get; init; }

    /// <summary>
    /// Explicit operator opt-in for catalog mutation. Non-dry-run imports require
    /// <c>ApplyMode = true</c>; otherwise the endpoint returns a 400 safety-gate error.
    /// </summary>
    public bool? ApplyMode { get; init; }

    /// <summary>
    /// Target coordinate reference system ID (for transformation).
    /// Default is null (preserve source CRS).
    /// </summary>
    public int? TargetSrid { get; init; }

    /// <summary>
    /// Request timeout in seconds for each API call.
    /// </summary>
    public int? RequestTimeoutSeconds { get; init; }

    /// <summary>
    /// Maximum number of retry attempts for failed requests.
    /// </summary>
    public int? MaxRetries { get; init; }

    /// <summary>
    /// Whether to automatically publish imported layers.
    /// </summary>
    public bool? AutoPublishLayers { get; init; }

    /// <summary>
    /// Batch size for processing multiple resources.
    /// </summary>
    public int? BatchSize { get; init; }

    /// <summary>
    /// Options for handling unsupported resources.
    /// </summary>
    public GeoServerImportApiOptions? ImportOptions { get; init; }
}

/// <summary>
/// API model for GeoServer import options.
/// </summary>
public sealed record GeoServerImportApiOptions
{
    /// <summary>
    /// How to handle unsupported datastore types.
    /// </summary>
    public UnsupportedResourceBehavior? UnsupportedDataStoreBehavior { get; init; }

    /// <summary>
    /// How to handle unsupported layer types.
    /// </summary>
    public UnsupportedResourceBehavior? UnsupportedLayerBehavior { get; init; }

    /// <summary>
    /// How to handle SLD styles when ImportStyles is true but conversion is not available.
    /// </summary>
    public UnsupportedResourceBehavior? UnsupportedStyleBehavior { get; init; }

    /// <summary>
    /// Whether to continue import if individual resources fail.
    /// </summary>
    public bool? ContinueOnResourceFailure { get; init; }

    /// <summary>
    /// Custom workspace name mappings for import.
    /// Key: GeoServer workspace name, Value: Target Honua workspace name
    /// </summary>
    public IReadOnlyDictionary<string, string>? WorkspaceNameMappings { get; init; }

    /// <summary>
    /// Default workspace name to use for global resources.
    /// </summary>
    public string? DefaultWorkspaceName { get; init; }
}

/// <summary>
/// API response model for GeoServer import job information.
/// </summary>
public sealed record GeoServerImportJob
{
    /// <summary>
    /// Unique identifier for the import job.
    /// </summary>
    public required string JobId { get; init; }

    /// <summary>
    /// Current status of the import job.
    /// </summary>
    public required GeoServerImportJobStatus Status { get; init; }

    /// <summary>
    /// When the job was queued.
    /// </summary>
    public DateTimeOffset QueuedAt { get; init; }

    /// <summary>
    /// When the job started processing (null if not started yet).
    /// </summary>
    public DateTimeOffset? StartedAt { get; init; }

    /// <summary>
    /// When the job completed (null if still running).
    /// </summary>
    public DateTimeOffset? CompletedAt { get; init; }

    /// <summary>
    /// Source GeoServer URL.
    /// </summary>
    public string? GeoServerUrl { get; init; }

    /// <summary>
    /// Error message if the job failed.
    /// </summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// Detailed progress information (only available for individual job queries).
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public GeoServerImportProgress? Progress { get; init; }

    /// <summary>
    /// Duration of the job (completed jobs) or elapsed time (running jobs).
    /// </summary>
    [JsonIgnore]
    public TimeSpan Duration => (CompletedAt ?? DateTimeOffset.UtcNow) - (StartedAt ?? QueuedAt);

    /// <summary>
    /// Duration in a human-readable format.
    /// </summary>
    public string DurationDisplay => Duration.TotalSeconds < 60
        ? $"{Duration.TotalSeconds:F1}s"
        : Duration.TotalMinutes < 60
            ? $"{Duration.TotalMinutes:F1}m"
            : $"{Duration.TotalHours:F1}h";
}

/// <summary>
/// Status of a GeoServer import job.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<GeoServerImportJobStatus>))]
public enum GeoServerImportJobStatus
{
    /// <summary>
    /// Job is queued for processing.
    /// </summary>
    Queued,

    /// <summary>
    /// Job is currently being processed.
    /// </summary>
    Processing,

    /// <summary>
    /// Job completed successfully.
    /// </summary>
    Completed,

    /// <summary>
    /// Job failed with errors.
    /// </summary>
    Failed,

    /// <summary>
    /// Job was cancelled.
    /// </summary>
    Cancelled
}
