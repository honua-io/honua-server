// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.Migration.Services;
using Honua.Core.Features.FileImport.Abstractions;
using Honua.Core.Features.FileImport.Domain;
using Honua.Core.Features.FileImport.Services;
namespace Honua.Core.Features.Migration.Domain;

public sealed record GeoServerDiscoveryRequest
{
    public required string GeoServerRestUrl { get; init; }

    public string? Username { get; init; }

    public string? Password { get; init; }

    public int TimeoutSeconds { get; init; } = 120;

    public bool IncludeCompatibilityAnalysis { get; init; } = true;

    public bool IncludeStyleContent { get; init; }

    public bool AllowUnsafeLocalUrls { get; init; }
}

public sealed record GeoServerImportRequest
{
    public string? JobId { get; init; }

    public required string GeoServerRestUrl { get; init; }

    public string? Username { get; init; }

    public string? Password { get; init; }

    public string? PasswordSecretReference { get; init; }

    public required string TargetHonuaUrl { get; init; }

    public string? HonuaApiKey { get; init; }

    public string? HonuaApiKeySecretReference { get; init; }

    public string[]? WorkspaceNames { get; init; }

    public string[]? DataStoreNames { get; init; }

    public string[]? LayerNames { get; init; }

    public bool ImportStyles { get; init; }

    public bool OverwriteExisting { get; init; }

    public bool DryRun { get; init; }

    public bool ApplyMode { get; init; }

    public int? TargetSrid { get; init; }

    public int RequestTimeoutSeconds { get; init; } = 120;

    public int MaxRetries { get; init; } = 3;

    public bool AutoPublishLayers { get; init; } = true;

    public int BatchSize { get; init; } = 10;

    public GeoServerImportOptions? ImportOptions { get; init; }

    public bool AllowUnsafeLocalUrls { get; init; }
}

public sealed record GeoServerImportOptions
{
    public UnsupportedResourceBehavior UnsupportedDataStoreBehavior { get; init; } = UnsupportedResourceBehavior.Skip;

    public UnsupportedResourceBehavior UnsupportedLayerBehavior { get; init; } = UnsupportedResourceBehavior.Skip;

    public UnsupportedResourceBehavior UnsupportedStyleBehavior { get; init; } = UnsupportedResourceBehavior.LogWarning;

    public bool ContinueOnResourceFailure { get; init; } = true;

    public IReadOnlyDictionary<string, string>? WorkspaceNameMappings { get; init; }

    public string DefaultWorkspaceName { get; init; } = "geoserver-import";
}

public enum UnsupportedResourceBehavior
{
    Skip,

    LogWarning,

    FailImport
}

public sealed record GeoServerImportResult
{
    public required bool Success { get; init; }

    public int WorkspacesImported { get; init; }

    public int DataStoresImported { get; init; }

    public int LayersImported { get; init; }

    public int StylesImported { get; init; }

    public int FailedResources { get; init; }

    public int ResourcesPlanned { get; init; }

    public int ResourcesApplied { get; init; }

    public int ResourcesAlreadyApplied { get; init; }

    public int ResourcesManualReview { get; init; }

    public int ResourcesUnsupported { get; init; }

    public required string SourceGeoServerUrl { get; init; }

    public required string TargetHonuaUrl { get; init; }

    public string? SourceGeoServerVersion { get; init; }

    public string? ErrorMessage { get; init; }

    public TimeSpan Duration { get; init; }

    public IReadOnlyList<string> Warnings { get; init; } = [];

    public IReadOnlyList<GeoServerImportedResource> ImportedResources { get; init; } = [];

    public IReadOnlyList<GeoServerFailedResource> FailedResourceDetails { get; init; } = [];

    public MigrationApplyPlanArtifact? ApplyPlan { get; init; }

    public MigrationApplyExecutionArtifact? ApplyExecution { get; init; }

    public MigrationRunMetricsArtifact? RunMetrics { get; init; }

    public bool WasDryRun { get; init; }

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

public sealed record GeoServerImportedResource
{
    public required string ResourceType { get; init; }

    public required string Name { get; init; }

    public string? WorkspaceName { get; init; }

    public int? HonuaResourceId { get; init; }

    public string? Notes { get; init; }
}

public sealed record GeoServerFailedResource
{
    public required string ResourceType { get; init; }

    public required string Name { get; init; }

    public string? WorkspaceName { get; init; }

    public required string ErrorMessage { get; init; }

    public bool IsExpectedFailure { get; init; }
}
