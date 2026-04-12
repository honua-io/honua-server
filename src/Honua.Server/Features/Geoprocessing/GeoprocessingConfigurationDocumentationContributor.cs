// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Configuration;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Server.Features.Infrastructure.Abstractions;
using Microsoft.Extensions.Options;
using ConfigurationSection = Honua.Core.Configuration.ConfigurationSection;

namespace Honua.Server.Features.Geoprocessing;

/// <summary>
/// Publishes geoprocessing workspace configuration metadata to the admin config endpoint.
/// </summary>
internal sealed class GeoprocessingConfigurationDocumentationContributor : IConfigurationDocumentationContributor
{
    private readonly IConfiguration _configuration;
    private readonly IOptions<WorkspaceOptions> _workspaceOptions;

    public GeoprocessingConfigurationDocumentationContributor(
        IConfiguration configuration,
        IOptions<WorkspaceOptions> workspaceOptions)
    {
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _workspaceOptions = workspaceOptions ?? throw new ArgumentNullException(nameof(workspaceOptions));
    }

    public IReadOnlyList<ConfigurationSection> GetSections()
    {
        var opts = _workspaceOptions.Value;

        return
        [
            new ConfigurationSection
            {
                Name = WorkspaceOptions.SectionName,
                Description = "Workspace lifecycle, retention, and cleanup settings for geoprocessing workflows",
                Properties =
                [
                    BuildPropertyWithCurrent(
                        $"{WorkspaceOptions.SectionName}:CleanupInterval",
                        "Geoprocessing__Workspace__CleanupInterval",
                        "duration",
                        "How frequently the cleanup service runs",
                        "00:15:00",
                        opts.CleanupInterval),
                    BuildPropertyWithCurrent(
                        $"{WorkspaceOptions.SectionName}:CleanupGracePeriod",
                        "Geoprocessing__Workspace__CleanupGracePeriod",
                        "duration",
                        "Grace period after expiration before workspace deletion",
                        "01:00:00",
                        opts.CleanupGracePeriod),
                    BuildPropertyWithCurrent(
                        $"{WorkspaceOptions.SectionName}:EnableAutomaticCleanup",
                        "Geoprocessing__Workspace__EnableAutomaticCleanup",
                        "boolean",
                        "Whether the automatic cleanup background service is enabled",
                        true,
                        opts.EnableAutomaticCleanup),
                    BuildPropertyWithCurrent(
                        $"{WorkspaceOptions.SectionName}:MaxCleanupBatchSize",
                        "Geoprocessing__Workspace__MaxCleanupBatchSize",
                        "integer",
                        "Maximum workspaces processed per cleanup sweep",
                        100,
                        opts.MaxCleanupBatchSize),
                    BuildPropertyWithCurrent(
                        $"{WorkspaceOptions.SectionName}:ScratchDefaultTtl",
                        "Geoprocessing__Workspace__ScratchDefaultTtl",
                        "duration",
                        "Default TTL for scratch workspaces",
                        RetentionPolicy.Defaults[WorkspaceKind.Scratch].DefaultTimeToLive,
                        opts.ScratchDefaultTtl ?? RetentionPolicy.Defaults[WorkspaceKind.Scratch].DefaultTimeToLive),
                    BuildPropertyWithCurrent(
                        $"{WorkspaceOptions.SectionName}:TempLayerDefaultTtl",
                        "Geoprocessing__Workspace__TempLayerDefaultTtl",
                        "duration",
                        "Default TTL for temp layer workspaces",
                        RetentionPolicy.Defaults[WorkspaceKind.TempLayer].DefaultTimeToLive,
                        opts.TempLayerDefaultTtl ?? RetentionPolicy.Defaults[WorkspaceKind.TempLayer].DefaultTimeToLive),
                    BuildPropertyWithCurrent(
                        $"{WorkspaceOptions.SectionName}:ResultCollectionDefaultTtl",
                        "Geoprocessing__Workspace__ResultCollectionDefaultTtl",
                        "duration",
                        "Default TTL for result collection workspaces",
                        RetentionPolicy.Defaults[WorkspaceKind.ResultCollection].DefaultTimeToLive,
                        opts.ResultCollectionDefaultTtl ?? RetentionPolicy.Defaults[WorkspaceKind.ResultCollection].DefaultTimeToLive),
                    BuildPropertyWithCurrent(
                        $"{WorkspaceOptions.SectionName}:MaxWorkspaceCount",
                        "Geoprocessing__Workspace__MaxWorkspaceCount",
                        "integer",
                        "Maximum workspace count per owner",
                        WorkspaceQuota.Default.MaxWorkspaceCount,
                        opts.MaxWorkspaceCount ?? WorkspaceQuota.Default.MaxWorkspaceCount),
                    BuildPropertyWithCurrent(
                        $"{WorkspaceOptions.SectionName}:MaxArtifactCount",
                        "Geoprocessing__Workspace__MaxArtifactCount",
                        "integer",
                        "Maximum artifact count per owner",
                        WorkspaceQuota.Default.MaxArtifactCount,
                        opts.MaxArtifactCount ?? WorkspaceQuota.Default.MaxArtifactCount),
                    BuildPropertyWithCurrent(
                        $"{WorkspaceOptions.SectionName}:MaxStorageBytes",
                        "Geoprocessing__Workspace__MaxStorageBytes",
                        "integer",
                        "Maximum storage bytes per owner",
                        WorkspaceQuota.Default.MaxStorageBytes,
                        opts.MaxStorageBytes ?? WorkspaceQuota.Default.MaxStorageBytes)
                ]
            }
        ];
    }

    public IReadOnlyList<EnvironmentVariableInfo> GetEnvironmentVariables()
    {
        return
        [
            new() { Name = "Geoprocessing__Workspace__CleanupInterval", ConfigPath = "Geoprocessing.Workspace", Description = "Cleanup service run interval", Default = "00:15:00", Example = "00:30:00" },
            new() { Name = "Geoprocessing__Workspace__CleanupGracePeriod", ConfigPath = "Geoprocessing.Workspace", Description = "Grace period after expiration before deletion", Default = "01:00:00", Example = "02:00:00" },
            new() { Name = "Geoprocessing__Workspace__EnableAutomaticCleanup", ConfigPath = "Geoprocessing.Workspace", Description = "Enable automatic workspace cleanup", Default = "true", Example = "false" },
            new() { Name = "Geoprocessing__Workspace__MaxCleanupBatchSize", ConfigPath = "Geoprocessing.Workspace", Description = "Max workspaces per cleanup sweep", Default = "100", Example = "50" },
            new() { Name = "Geoprocessing__Workspace__ScratchDefaultTtl", ConfigPath = "Geoprocessing.Workspace", Description = "Default TTL for scratch workspaces", Required = false, Example = "01:00:00" },
            new() { Name = "Geoprocessing__Workspace__TempLayerDefaultTtl", ConfigPath = "Geoprocessing.Workspace", Description = "Default TTL for temp layer workspaces", Required = false, Example = "06:00:00" },
            new() { Name = "Geoprocessing__Workspace__ResultCollectionDefaultTtl", ConfigPath = "Geoprocessing.Workspace", Description = "Default TTL for result collection workspaces", Required = false, Example = "1.00:00:00" },
            new() { Name = "Geoprocessing__Workspace__MaxWorkspaceCount", ConfigPath = "Geoprocessing.Workspace", Description = "Max workspace count per owner", Required = false, Example = "50" },
            new() { Name = "Geoprocessing__Workspace__MaxArtifactCount", ConfigPath = "Geoprocessing.Workspace", Description = "Max artifact count per owner", Required = false, Example = "500" },
            new() { Name = "Geoprocessing__Workspace__MaxStorageBytes", ConfigPath = "Geoprocessing.Workspace", Description = "Max storage bytes per owner", Required = false, Example = "1073741824" }
        ];
    }

    private ConfigurationProperty BuildProperty(
        string path,
        string envVar,
        string type,
        string description,
        object? defaultValue,
        bool isRequired = false,
        bool isSensitive = false,
        string? validation = null)
    {
        var currentValue = GetCurrentValue(path, isSensitive);
        var source = DetermineSource(path);

        return new ConfigurationProperty
        {
            Name = path.Split(':').Last(),
            Path = path,
            EnvironmentVariable = envVar,
            Type = type,
            Description = description,
            DefaultValue = defaultValue,
            CurrentValue = currentValue,
            IsRequired = isRequired,
            IsSensitive = isSensitive,
            Validation = validation,
            Source = source
        };
    }

    private ConfigurationProperty BuildPropertyWithCurrent(
        string path,
        string envVar,
        string type,
        string description,
        object? defaultValue,
        object? currentValue,
        string? validation = null,
        bool isSensitive = false)
    {
        var source = DetermineSource(path);
        var displayValue = isSensitive && currentValue != null ? "***" : currentValue;

        return new ConfigurationProperty
        {
            Name = path.Split(':').Last(),
            Path = path,
            EnvironmentVariable = envVar,
            Type = type,
            Description = description,
            DefaultValue = defaultValue,
            CurrentValue = displayValue,
            IsRequired = false,
            IsSensitive = isSensitive,
            Validation = validation,
            Source = source
        };
    }

    private string? GetCurrentValue(string path, bool isSensitive)
    {
        var value = _configuration[path];
        if (value == null)
        {
            return null;
        }

        if (isSensitive)
        {
            return "***";
        }

        return value;
    }

    private string DetermineSource(string path)
    {
        var envVarName = path.Replace(":", "__", StringComparison.Ordinal);
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable(envVarName)))
        {
            return "Environment";
        }

        if (_configuration[path] != null)
        {
            return "appsettings.json";
        }

        return "Default";
    }
}
