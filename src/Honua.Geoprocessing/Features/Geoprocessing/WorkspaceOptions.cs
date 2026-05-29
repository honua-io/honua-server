// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Geoprocessing.Domain;
using Microsoft.Extensions.Options;

namespace Honua.Server.Features.Geoprocessing;

/// <summary>
/// Configuration options for workspace lifecycle management.
/// </summary>
public sealed class WorkspaceOptions
{
    /// <summary>
    /// Configuration section name.
    /// </summary>
    public const string SectionName = "Geoprocessing:Workspace";

    /// <summary>
    /// How frequently the cleanup service runs. Default: 15 minutes.
    /// </summary>
    public TimeSpan CleanupInterval { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Grace period after expiration before a workspace is deleted. Default: 1 hour.
    /// Allows promotion of artifacts from recently-expired workspaces.
    /// </summary>
    public TimeSpan CleanupGracePeriod { get; init; } = TimeSpan.FromHours(1);

    /// <summary>
    /// Whether the automatic cleanup background service is enabled.
    /// </summary>
    public bool EnableAutomaticCleanup { get; init; } = true;

    /// <summary>
    /// Maximum number of workspaces to process per cleanup sweep to bound memory usage.
    /// </summary>
    public int MaxCleanupBatchSize { get; init; } = 100;

    /// <summary>
    /// Default time-to-live override for scratch workspaces. Null uses the built-in default.
    /// </summary>
    public TimeSpan? ScratchDefaultTtl { get; init; }

    /// <summary>
    /// Default time-to-live override for temp layer workspaces. Null uses the built-in default.
    /// </summary>
    public TimeSpan? TempLayerDefaultTtl { get; init; }

    /// <summary>
    /// Default time-to-live override for result collection workspaces. Null uses the built-in default.
    /// </summary>
    public TimeSpan? ResultCollectionDefaultTtl { get; init; }

    /// <summary>
    /// Maximum workspace count per owner. Null uses the built-in default.
    /// </summary>
    public int? MaxWorkspaceCount { get; init; }

    /// <summary>
    /// Maximum artifact count per owner. Null uses the built-in default.
    /// </summary>
    public int? MaxArtifactCount { get; init; }

    /// <summary>
    /// Maximum storage bytes per owner. Null uses the built-in default.
    /// </summary>
    public long? MaxStorageBytes { get; init; }
}

/// <summary>
/// Validates <see cref="WorkspaceOptions"/> at startup to surface configuration errors early.
/// </summary>
internal sealed class WorkspaceOptionsValidator : IValidateOptions<WorkspaceOptions>
{
    public ValidateOptionsResult Validate(string? name, WorkspaceOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>();

        if (options.CleanupInterval <= TimeSpan.Zero)
        {
            failures.Add($"{WorkspaceOptions.SectionName}:CleanupInterval must be a positive duration.");
        }

        if (options.CleanupGracePeriod < TimeSpan.Zero)
        {
            failures.Add($"{WorkspaceOptions.SectionName}:CleanupGracePeriod must not be negative.");
        }

        if (options.MaxCleanupBatchSize <= 0)
        {
            failures.Add($"{WorkspaceOptions.SectionName}:MaxCleanupBatchSize must be a positive integer.");
        }

        ValidateTtlOverride(failures, "ScratchDefaultTtl",
            options.ScratchDefaultTtl, WorkspaceKind.Scratch);

        ValidateTtlOverride(failures, "TempLayerDefaultTtl",
            options.TempLayerDefaultTtl, WorkspaceKind.TempLayer);

        ValidateTtlOverride(failures, "ResultCollectionDefaultTtl",
            options.ResultCollectionDefaultTtl, WorkspaceKind.ResultCollection);

        if (options.MaxWorkspaceCount.HasValue && options.MaxWorkspaceCount.Value <= 0)
        {
            failures.Add($"{WorkspaceOptions.SectionName}:MaxWorkspaceCount must be a positive integer when specified.");
        }

        if (options.MaxArtifactCount.HasValue && options.MaxArtifactCount.Value <= 0)
        {
            failures.Add($"{WorkspaceOptions.SectionName}:MaxArtifactCount must be a positive integer when specified.");
        }

        if (options.MaxStorageBytes.HasValue && options.MaxStorageBytes.Value <= 0)
        {
            failures.Add($"{WorkspaceOptions.SectionName}:MaxStorageBytes must be a positive value when specified.");
        }

        return failures.Count > 0
            ? ValidateOptionsResult.Fail(failures)
            : ValidateOptionsResult.Success;
    }

    private static void ValidateTtlOverride(
        List<string> failures,
        string propertyName,
        TimeSpan? value,
        WorkspaceKind kind)
    {
        if (!value.HasValue)
            return;

        if (value.Value <= TimeSpan.Zero)
        {
            failures.Add($"{WorkspaceOptions.SectionName}:{propertyName} must be a positive duration when specified.");
            return;
        }

        if (RetentionPolicy.Defaults.TryGetValue(kind, out var policy)
            && policy.MaxTimeToLive.HasValue
            && value.Value > policy.MaxTimeToLive.Value)
        {
            failures.Add(
                $"{WorkspaceOptions.SectionName}:{propertyName} ({value.Value}) exceeds the maximum allowed TTL of {policy.MaxTimeToLive.Value} for {kind} workspaces.");
        }
    }
}
