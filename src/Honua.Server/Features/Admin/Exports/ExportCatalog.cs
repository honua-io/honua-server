// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.RegularExpressions;
using Honua.Core.Features.Exports;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Infrastructure.Progress;
using Honua.Server.Features.Admin.TileOperations;
using Microsoft.Extensions.Options;

namespace Honua.Server.Features.Admin.Exports;

// Describes Exports from what storage and the job log hold: the pointer names the current Version,
// version folders hold earlier ones, and the newest export job says whether one is running or failed.
internal sealed partial class ExportCatalog(
    ICloudFileStorage storage,
    IOptions<CloudStorageOptions> options,
    ITileCacheJobService jobs)
{
    internal const string NotPublicReason = "The public data path is not enabled for Exports on this installation.";

    public async Task<IReadOnlyList<ExportResponse>> ListAsync(string baseUrl, CancellationToken cancellationToken)
    {
        var files = await ListFilesAsync(cancellationToken).ConfigureAwait(false);
        var exportJobs = await ExportJobsAsync(cancellationToken).ConfigureAwait(false);
        var ids = files.Select(file => Match(file).ExportId)
            .Concat(exportJobs.Select(job => job.ExportId))
            .OfType<string>().Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);
        var items = new List<ExportResponse>();
        foreach (var id in ids)
        {
            var export = await DescribeAsync(id, files, exportJobs, baseUrl, allVersions: false, cancellationToken).ConfigureAwait(false);
            items.AddRange(export is null ? [] : [export]);
        }

        return items;
    }

    public async Task<ExportResponse?> GetAsync(string exportId, string baseUrl, CancellationToken cancellationToken)
    {
        var files = await ListFilesAsync(cancellationToken).ConfigureAwait(false);
        var exportJobs = await ExportJobsAsync(cancellationToken).ConfigureAwait(false);
        return await DescribeAsync(exportId, files, exportJobs, baseUrl, allVersions: true, cancellationToken).ConfigureAwait(false);
    }

    // Unpublishing removes the pointer, so the read routes stop serving; Versions stay for audit.
    public async Task<bool> UnpublishAsync(string exportId, CancellationToken cancellationToken)
    {
        var store = ExportStore.Create(storage, options.Value, exportId);
        return await storage.DeleteAsync(store.PointerKey, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ExportResponse?> DescribeAsync(
        string exportId,
        IReadOnlyList<CloudFile> files,
        IReadOnlyList<TileOperationProgress> exportJobs,
        string baseUrl,
        bool allVersions,
        CancellationToken cancellationToken)
    {
        var store = ExportStore.Create(storage, options.Value, exportId);
        var pointer = await store.ReadPointerAsync(cancellationToken).ConfigureAwait(false);
        var current = pointer?.ExportId == exportId ? await store.ReadCollectionAsync(pointer, cancellationToken).ConfigureAwait(false) : null;
        var versionIds = files.Select(Match).Where(match => match.ExportId == exportId && match.Version is not null)
            .Select(match => match.Version!).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        var job = exportJobs.Where(candidate => candidate.ExportId == exportId).MaxBy(candidate => candidate.StartedAt);
        if (current is null && versionIds.Count == 0 && job is null)
        {
            return null;
        }

        var versions = await VersionsAsync(store, current, allVersions ? versionIds : [], baseUrl, cancellationToken).ConfigureAwait(false);
        return new ExportResponse
        {
            Id = exportId,
            Title = current?.Title ?? exportId,
            Status = Status(current, versionIds, job),
            Source = Source(current, job),
            CurrentVersion = current?.Version,
            UpdatedAt = current?.PublishedAt,
            CollectionUrl = current is null ? null : FileUrl(baseUrl, exportId, current.Version, ExportCollectionDocument.FileName),
            NotPublicReason = NotPublicReason,
            JobId = job?.JobId,
            Message = job?.Status == OperationStatus.Failed ? job.ErrorMessage : null,
            Versions = versions
        };
    }

    private static async Task<IReadOnlyList<ExportVersionResponse>> VersionsAsync(
        ExportStore store, ExportVersion? current, IReadOnlyList<string> versionIds, string baseUrl, CancellationToken cancellationToken)
    {
        var described = new List<ExportVersionResponse>();
        foreach (var versionId in versionIds.Where(id => id != current?.Version))
        {
            var version = await store.ReadVersionAsync(versionId, cancellationToken).ConfigureAwait(false);
            described.AddRange(version is null ? [] : [Version(version, baseUrl)]);
        }

        described.AddRange(current is null ? [] : [Version(current, baseUrl)]);
        return described;
    }

    internal static ExportVersionResponse Version(ExportVersion version, string baseUrl)
        => new(version.Version, version.PublishedAt, version.Capture.FeatureCount,
        [
            new ExportArtifactResponse("collection", FileUrl(baseUrl, version.ExportId, version.Version, ExportCollectionDocument.FileName), null),
            new ExportArtifactResponse("geoparquet", FileUrl(baseUrl, version.ExportId, version.Version, version.Artifacts.Geoparquet.Href), version.Artifacts.Geoparquet.Bytes),
            new ExportArtifactResponse("pmtiles", FileUrl(baseUrl, version.ExportId, version.Version, version.Artifacts.Pmtiles.Href), version.Artifacts.Pmtiles.Bytes)
        ]);

    private static string Status(ExportVersion? current, List<string> versionIds, TileOperationProgress? job)
    {
        if (job?.Status is OperationStatus.Queued or OperationStatus.Processing)
        {
            return ExportResponse.Running;
        }

        if (current is not null)
        {
            return ExportResponse.Ready;
        }

        return versionIds.Count > 0 ? ExportResponse.Unpublished : ExportResponse.Failed;
    }

    private static ExportSourceRequest Source(ExportVersion? current, TileOperationProgress? job) => new()
    {
        Kind = "layer",
        ServiceId = job?.ServiceId ?? current?.SourceLayer.Service,
        LayerId = job?.LayerId,
        Name = current?.SourceLayer.Title ?? current?.SourceLayer.Layer
    };

    private static string FileUrl(string baseUrl, string exportId, string version, string fileName)
        => $"{baseUrl}/api/v1/exports/{Uri.EscapeDataString(exportId)}/{Uri.EscapeDataString(version)}/{Uri.EscapeDataString(fileName)}";

    private async Task<IReadOnlyList<TileOperationProgress>> ExportJobsAsync(CancellationToken cancellationToken)
    {
        var all = await jobs.ListAsync(TileOperationListScope.All, cancellationToken).ConfigureAwait(false);
        return [.. all.Where(job => job.Operation == TileCacheExecutionSpecBuilder.ExportOperation && job.ExportId is not null)];
    }

    // Listing prefixes the provider's own key prefix, so it takes the folder below it.
    private Task<IReadOnlyList<CloudFile>> ListFilesAsync(CancellationToken cancellationToken)
    {
        var publishPrefix = options.Value.PMTilesPublish?.KeyPrefix?.Trim().Trim('/');
        var folder = string.IsNullOrEmpty(publishPrefix) ? "exports" : $"{publishPrefix}/exports";
        return storage.ListFilesAsync(folder, int.MaxValue, includeMetadata: false, cancellationToken);
    }

    private static (string? ExportId, string? Version) Match(CloudFile file)
    {
        var match = ExportFilePattern().Match(file.FileId);
        match = match.Success ? match : ExportFilePattern().Match(file.StoragePath);
        if (!match.Success)
        {
            return (null, null);
        }

        return (match.Groups["id"].Value, match.Groups["version"].Success ? match.Groups["version"].Value : null);
    }

    [GeneratedRegex(@"(?:^|/)exports/(?<id>[a-z0-9][a-z0-9-]{2,63})/(?:current\.json|(?<version>[A-Za-z0-9][A-Za-z0-9-]{0,95})/collection\.json)$", RegexOptions.CultureInvariant)]
    private static partial Regex ExportFilePattern();
}
