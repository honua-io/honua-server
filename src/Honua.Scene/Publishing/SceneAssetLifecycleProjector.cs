// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Scene.Domain;
using Honua.Scene;

namespace Honua.Infrastructure.Scene;

internal static class SceneAssetLifecycleProjector
{
    public static SceneAssetLifecycleSnapshot Project(
        SceneDatasetRecord? record,
        SceneJobLifecycleSummary? latestJob,
        string baseUrl)
    {
        if (record is null && latestJob is null)
        {
            throw new ArgumentException("Either a registered scene record or a scene job summary is required.");
        }

        var sceneId = record?.Id ?? latestJob!.SceneId!;
        var (diskBytes, diskFiles) = record is not null
            ? MeasurePublishedDirectory(record.AssetRoot)
            : (0L, 0);

        var byteSize = diskBytes > 0 ? diskBytes : (latestJob?.ByteSize ?? 0L);
        var fileCount = diskFiles > 0 ? diskFiles : (latestJob?.FileCount ?? 0);
        var sourceKind = latestJob?.SourceKind ?? InferSourceKind(record?.DatasetType ?? SceneDatasetType.HostedTiles);
        var lifecycleStatus = ResolveLifecycleStatus(record, latestJob);
        var warnings = BuildWarnings(record, latestJob);

        var normalizedBase = baseUrl.TrimEnd('/');
        var tilesetUrl = record is not null || string.Equals(latestJob?.Status, SceneJobStatusNames.Succeeded, StringComparison.Ordinal)
            ? SceneUrls.AbsoluteTilesetUrl(normalizedBase, sceneId)
            : null;
        var i3sUrl = record is not null || string.Equals(latestJob?.Status, SceneJobStatusNames.Succeeded, StringComparison.Ordinal)
            ? string.Concat(normalizedBase, "/rest/services/", Uri.EscapeDataString(sceneId), "/SceneServer")
            : null;

        return new SceneAssetLifecycleSnapshot(
            SceneId: sceneId,
            DatasetId: record?.DatasetId,
            Name: record?.Name ?? latestJob?.DisplayName ?? sceneId,
            Description: record?.Description,
            SourceKind: sourceKind,
            DatasetType: DatasetTypeToWire(record?.DatasetType ?? SceneDatasetType.HostedTiles),
            LifecycleStatus: lifecycleStatus,
            JobStatus: latestJob?.Status,
            LatestJobId: latestJob?.JobId,
            PublishedRevision: record?.Revision,
            TilesetUrl: tilesetUrl,
            I3sUrl: i3sUrl,
            ByteSize: byteSize,
            FileCount: fileCount,
            Warnings: warnings,
            IsPublic: record?.IsPublic ?? false,
            RequiresAuth: record?.RequiresAuth ?? true,
            Crs: record?.Crs,
            Extent: record?.Extent,
            CreatedAt: record?.CreatedAt ?? latestJob?.CreatedAt ?? DateTimeOffset.UtcNow,
            UpdatedAt: record?.UpdatedAt ?? latestJob?.CompletedAt);
    }

    private static string ResolveLifecycleStatus(SceneDatasetRecord? record, SceneJobLifecycleSummary? latestJob)
    {
        if (latestJob is not null
            && latestJob.Status is SceneJobStatusNames.Queued or SceneJobStatusNames.Running)
        {
            return latestJob.Status;
        }

        if (record is not null)
        {
            return record.Status switch
            {
                SceneDatasetStatus.Active => "active",
                SceneDatasetStatus.Inactive => "inactive",
                SceneDatasetStatus.ValidationFailed => "validation_failed",
                _ => "active",
            };
        }

        return latestJob?.Status ?? "unknown";
    }

    private static string InferSourceKind(SceneDatasetType datasetType) => datasetType switch
    {
        SceneDatasetType.Building => "citygml",
        SceneDatasetType.PointCloud => "pointcloud",
        SceneDatasetType.Terrain => "terrain",
        _ => "registered",
    };

    private static string DatasetTypeToWire(SceneDatasetType type) => type switch
    {
        SceneDatasetType.HostedTiles => "hosted_tiles",
        SceneDatasetType.Terrain => "terrain",
        SceneDatasetType.Building => "building",
        SceneDatasetType.PointCloud => "point_cloud",
        _ => "hosted_tiles",
    };

    private static string[] BuildWarnings(SceneDatasetRecord? record, SceneJobLifecycleSummary? latestJob)
    {
        var list = new List<string>();
        if (!string.IsNullOrWhiteSpace(record?.ValidationMessage))
        {
            list.Add(record.ValidationMessage);
        }

        if (latestJob?.Warnings is { Count: > 0 } jobWarnings)
        {
            foreach (var warning in jobWarnings)
            {
                if (!string.IsNullOrWhiteSpace(warning) && !list.Contains(warning, StringComparer.Ordinal))
                {
                    list.Add(warning);
                }
            }
        }

        return list.ToArray();
    }

    private static (long Bytes, int Files) MeasurePublishedDirectory(string? assetRoot)
    {
        if (string.IsNullOrWhiteSpace(assetRoot) || !Directory.Exists(assetRoot))
        {
            return (0L, 0);
        }

        try
        {
            long totalBytes = 0;
            var fileCount = 0;
            foreach (var file in Directory.EnumerateFiles(assetRoot, "*", SearchOption.AllDirectories))
            {
                var info = new FileInfo(file);
                if (info.Exists)
                {
                    totalBytes += info.Length;
                    fileCount++;
                }

                if (fileCount >= 50_000)
                {
                    break;
                }
            }

            return (totalBytes, fileCount);
        }
        // Directory measurement is observational metadata for the lifecycle view; IO races return zero.
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (0L, 0);
        }
    }
}

internal static class SceneJobStatusNames
{
    public const string Queued = "queued";
    public const string Running = "running";
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";
}

internal sealed record SceneJobLifecycleSummary(
    string JobId,
    string SourceKind,
    string Status,
    string? SceneId,
    string? DisplayName,
    long? ByteSize,
    int? FileCount,
    IReadOnlyList<string> Warnings,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt);

internal sealed record SceneAssetLifecycleSnapshot(
    string SceneId,
    Guid? DatasetId,
    string Name,
    string? Description,
    string SourceKind,
    string DatasetType,
    string LifecycleStatus,
    string? JobStatus,
    string? LatestJobId,
    int? PublishedRevision,
    string? TilesetUrl,
    string? I3sUrl,
    long ByteSize,
    int FileCount,
    string[] Warnings,
    bool IsPublic,
    bool RequiresAuth,
    string? Crs,
    SceneExtent? Extent,
    DateTimeOffset CreatedAt,
    DateTimeOffset? UpdatedAt);
