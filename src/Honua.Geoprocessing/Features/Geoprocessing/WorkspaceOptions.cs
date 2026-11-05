// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Geoprocessing.Domain;
using Microsoft.Extensions.Options;

namespace Honua.Geoprocessing;

public sealed class WorkspaceOptions
{
    public const string SectionName = "Geoprocessing:Workspace";

    public TimeSpan CleanupInterval { get; set; } = TimeSpan.FromMinutes(15);

    // The grace period lets artifacts be promoted out of a workspace that has just expired.
    public TimeSpan CleanupGracePeriod { get; set; } = TimeSpan.FromHours(1);

    public bool EnableAutomaticCleanup { get; set; } = true;

    public int MaxCleanupBatchSize { get; set; } = 100;

    public TimeSpan? ScratchDefaultTtl { get; set; }

    public TimeSpan? TempLayerDefaultTtl { get; set; }

    public TimeSpan? ResultCollectionDefaultTtl { get; set; }

    public int? MaxWorkspaceCount { get; set; }

    public int? MaxArtifactCount { get; set; }

    public long? MaxStorageBytes { get; set; }
}

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
