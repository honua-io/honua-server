// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

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

public sealed record GeoServerDiscoveryApiRequest
{
    public string? GeoServerRestUrl { get; init; }

    public string? Username { get; init; }

    public string? Password { get; init; }

    public int? TimeoutSeconds { get; init; }

    public bool? IncludeCompatibilityAnalysis { get; init; }

    public bool? IncludeStyleContent { get; init; }
}

public sealed record GeoServerImportApiRequest
{
    public string? GeoServerRestUrl { get; init; }

    public string? Username { get; init; }

    public string? Password { get; init; }

    public string? PasswordSecretReference { get; init; }

    public string? TargetHonuaUrl { get; init; }

    public string? HonuaApiKey { get; init; }

    public string? HonuaApiKeySecretReference { get; init; }

    public string[]? WorkspaceNames { get; init; }

    public string[]? DataStoreNames { get; init; }

    public string[]? LayerNames { get; init; }

    public bool? ImportStyles { get; init; }

    public bool? OverwriteExisting { get; init; }

    public bool? DryRun { get; init; }

    public bool? ApplyMode { get; init; }

    public int? TargetSrid { get; init; }

    public int? RequestTimeoutSeconds { get; init; }

    public int? MaxRetries { get; init; }

    public bool? AutoPublishLayers { get; init; }

    public int? BatchSize { get; init; }

    public GeoServerImportApiOptions? ImportOptions { get; init; }
}

public sealed record GeoServerImportApiOptions
{
    public UnsupportedResourceBehavior? UnsupportedDataStoreBehavior { get; init; }

    public UnsupportedResourceBehavior? UnsupportedLayerBehavior { get; init; }

    public UnsupportedResourceBehavior? UnsupportedStyleBehavior { get; init; }

    public bool? ContinueOnResourceFailure { get; init; }

    public IReadOnlyDictionary<string, string>? WorkspaceNameMappings { get; init; }

    public string? DefaultWorkspaceName { get; init; }
}

public sealed record GeoServerImportJob
{
    public required string JobId { get; init; }

    public required GeoServerImportJobStatus Status { get; init; }

    public DateTimeOffset QueuedAt { get; init; }

    public DateTimeOffset? StartedAt { get; init; }

    public DateTimeOffset? CompletedAt { get; init; }

    public string? GeoServerUrl { get; init; }

    public string? ErrorMessage { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public GeoServerImportProgress? Progress { get; init; }

    [JsonIgnore]
    public TimeSpan Duration => (CompletedAt ?? DateTimeOffset.UtcNow) - (StartedAt ?? QueuedAt);

    public string DurationDisplay => Duration.TotalSeconds < 60
        ? $"{Duration.TotalSeconds:F1}s"
        : Duration.TotalMinutes < 60
            ? $"{Duration.TotalMinutes:F1}m"
            : $"{Duration.TotalHours:F1}h";
}

[JsonConverter(typeof(JsonStringEnumConverter<GeoServerImportJobStatus>))]
public enum GeoServerImportJobStatus
{
    Queued,

    Processing,

    Completed,

    Failed,

    Cancelled
}
