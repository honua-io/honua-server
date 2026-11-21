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

internal readonly record struct ExportListPage(int? Limit = null, int? Offset = null);

internal sealed record ExportCatalogDependencies
{
    public required ICloudFileStorage Storage { get; init; }

    public required IOptions<CloudStorageOptions> Options { get; init; }

    public required ITileCacheJobService Jobs { get; init; }

    public PublicExportRevocationService? PublicRevocation { get; init; }
}

// Describes Exports from what storage and the job log hold: the pointer names the current Version,
// version folders hold earlier ones, and the newest export job says whether one is running or failed.
internal sealed partial class ExportCatalog(ExportCatalogDependencies deps)
{
    private readonly ICloudFileStorage storage = deps.Storage;
    private readonly IOptions<CloudStorageOptions> options = deps.Options;
    private readonly ITileCacheJobService jobs = deps.Jobs;
    private readonly PublicExportRevocationService? publicRevocation = deps.PublicRevocation;
    internal const string NotPublicReason = "The public data path is not enabled for Exports on this installation.";
    internal const int DefaultListLimit = 50;
    internal const int MaxListLimit = 200;
    internal const int MaxVersionsReturned = 100;

    private sealed record CatalogSnapshot(
        IReadOnlyList<CloudFile> Files,
        IReadOnlyList<TileOperationProgress> ExportJobs,
        string BaseUrl);

    private sealed record ExportState
    {
        public required ExportStore Store { get; init; }

        public ExportVersion? Current { get; init; }

        public required List<string> VersionIds { get; init; }

        public TileOperationProgress? Job { get; init; }
    }

    public async Task<bool> HasActiveJobAsync(string exportId, CancellationToken cancellationToken)
    {
        var exportJobs = await ExportJobsAsync(cancellationToken).ConfigureAwait(false);
        return exportJobs.Any(job => job.ExportId == exportId && job.Status is OperationStatus.Queued or OperationStatus.Processing);
    }

    public async Task<IReadOnlyList<ExportResponse>> ListAsync(
        string baseUrl, ExportListPage page, CancellationToken cancellationToken)
    {
        var pageLimit = Math.Clamp(page.Limit ?? DefaultListLimit, 1, MaxListLimit);
        var pageOffset = Math.Max(0, page.Offset ?? 0);
        var snapshot = new CatalogSnapshot(
            await ListFilesAsync(subFolder: null, cancellationToken).ConfigureAwait(false),
            await ExportJobsAsync(cancellationToken).ConfigureAwait(false),
            baseUrl);
        var ids = snapshot.Files.Select(file => Match(file).ExportId)
            .Concat(snapshot.ExportJobs.Select(job => job.ExportId))
            .OfType<string>().Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)
            .Skip(pageOffset).Take(pageLimit);
        var items = new List<ExportResponse>();
        foreach (var id in ids)
        {
            var export = await DescribeSummaryAsync(id, snapshot, cancellationToken).ConfigureAwait(false);
            items.AddRange(export is null ? [] : [export]);
        }

        return items;
    }

    public async Task<ExportResponse?> GetAsync(string exportId, string baseUrl, CancellationToken cancellationToken)
    {
        var snapshot = new CatalogSnapshot(
            await ListFilesAsync(exportId, cancellationToken).ConfigureAwait(false),
            await ExportJobsAsync(cancellationToken).ConfigureAwait(false),
            baseUrl);
        return await DescribeDetailAsync(exportId, snapshot, cancellationToken).ConfigureAwait(false);
    }

    // Unpublishing writes a durable fence before removing the pointer so an in-flight job cannot
    // republish and a different Layer cannot later claim the same Export id; Versions stay for audit.
    public async Task<bool> UnpublishAsync(string exportId, CancellationToken cancellationToken)
    {
        var store = ExportStore.Create(storage, options.Value, exportId);
        var (serviceId, layerId) = await ResolveSourceIdentityAsync(store, cancellationToken).ConfigureAwait(false);
        await store.WriteUnpublishedAsync(
            new ExportUnpublishedMarker
            {
                ExportId = exportId,
                ServiceId = serviceId,
                LayerId = layerId,
                UnpublishedAt = DateTimeOffset.UtcNow
            },
            cancellationToken).ConfigureAwait(false);
        if (publicRevocation is not null)
        {
            await publicRevocation.RevokeUnpublishedAsync(exportId, changeId: null, cancellationToken).ConfigureAwait(false);
        }

        return await storage.DeleteAsync(store.PointerKey, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<(string ServiceId, string LayerId)> ResolveSourceIdentityAsync(
        ExportStore store, CancellationToken cancellationToken)
    {
        var pointer = await store.ReadPointerAsync(cancellationToken).ConfigureAwait(false);
        var version = pointer is not null
            ? await store.ReadCollectionAsync(pointer, cancellationToken).ConfigureAwait(false)
            : await store.FindAnyStoredVersionAsync(cancellationToken).ConfigureAwait(false);
        if (version is not null)
        {
            return (version.SourceLayer.Service, version.SourceLayer.Layer);
        }

        var existing = await store.ReadUnpublishedAsync(cancellationToken).ConfigureAwait(false);
        return existing is not null ? (existing.ServiceId, existing.LayerId) : (string.Empty, string.Empty);
    }

    private async Task<ExportResponse?> DescribeSummaryAsync(
        string exportId, CatalogSnapshot snapshot, CancellationToken cancellationToken)
    {
        var state = await LoadStateAsync(exportId, snapshot, cancellationToken).ConfigureAwait(false);
        if (state is null)
        {
            return null;
        }

        var versions = state.Current is null ? [] : new[] { Version(state.Current, snapshot.BaseUrl) };
        return BuildResponse(exportId, state, (snapshot.BaseUrl, versions));
    }

    private async Task<ExportResponse?> DescribeDetailAsync(
        string exportId, CatalogSnapshot snapshot, CancellationToken cancellationToken)
    {
        var state = await LoadStateAsync(exportId, snapshot, cancellationToken).ConfigureAwait(false);
        if (state is null)
        {
            return null;
        }

        var versions = await LoadVersionsAsync(state, snapshot.BaseUrl, cancellationToken).ConfigureAwait(false);
        return BuildResponse(exportId, state, (snapshot.BaseUrl, versions));
    }

    private async Task<ExportState?> LoadStateAsync(
        string exportId, CatalogSnapshot snapshot, CancellationToken cancellationToken)
    {
        var store = ExportStore.Create(storage, options.Value, exportId);
        var pointer = await store.ReadPointerAsync(cancellationToken).ConfigureAwait(false);
        var current = pointer?.ExportId == exportId ? await store.ReadCollectionAsync(pointer, cancellationToken).ConfigureAwait(false) : null;
        var versionIds = snapshot.Files.Select(Match).Where(match => match.ExportId == exportId && match.Version is not null)
            .Select(match => match.Version!).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)
            .TakeLast(MaxVersionsReturned).ToList();
        var job = snapshot.ExportJobs.Where(candidate => candidate.ExportId == exportId).MaxBy(candidate => candidate.StartedAt);
        return current is null && versionIds.Count == 0 && job is null
            ? null
            : new ExportState { Store = store, Current = current, VersionIds = versionIds, Job = job };
    }

    private static ExportResponse BuildResponse(
        string exportId, ExportState state, (string BaseUrl, IReadOnlyList<ExportVersionResponse> Versions) detail)
    {
        var publicDelivery = state.Current is not null && state.Job?.PublishedExportVersion?.Version == state.Current.Version
            ? state.Job.PublishedExportVersion.Public
            : null;
        var isPublic = publicDelivery?.Status == PublicExportDeliveryStatus.Promoted && !string.IsNullOrWhiteSpace(publicDelivery.CollectionUrl);
        return new()
        {
            Id = exportId,
            Title = state.Current?.Title ?? exportId,
            Status = Status(state.Current, state.VersionIds, state.Job),
            Source = Source(state.Current, state.Job),
            CurrentVersion = state.Current?.Version,
            UpdatedAt = state.Current?.PublishedAt,
            CollectionUrl = state.Current is null
                ? null
                : FileUrl(detail.BaseUrl, exportId, VersionFilePath(state.Current.Version, ExportCollectionDocument.FileName)),
            PublicUrl = isPublic ? publicDelivery!.CollectionUrl : null,
            NotPublicReason = isPublic ? null : publicDelivery?.Reason ?? NotPublicReason,
            JobId = state.Job?.JobId,
            Message = state.Job?.Status == OperationStatus.Failed ? state.Job.ErrorMessage : null,
            Versions = detail.Versions
        };
    }

    private static async Task<IReadOnlyList<ExportVersionResponse>> LoadVersionsAsync(
        ExportState state, string baseUrl, CancellationToken cancellationToken)
    {
        var described = new List<ExportVersionResponse>();
        foreach (var versionId in state.VersionIds.Where(id => id != state.Current?.Version))
        {
            var version = await state.Store.ReadVersionAsync(versionId, cancellationToken).ConfigureAwait(false);
            described.AddRange(version is null ? [] : [Version(version, baseUrl)]);
        }

        described.AddRange(state.Current is null ? [] : [Version(state.Current, baseUrl)]);
        return described;
    }

    internal static ExportVersionResponse Version(ExportVersion version, string baseUrl)
        => new()
        {
            Version = version.Version,
            CreatedAt = version.PublishedAt,
            FeatureCount = version.Capture.FeatureCount,
            Artifacts =
            [
                new ExportArtifactResponse("collection", FileUrl(baseUrl, version.ExportId, VersionFilePath(version.Version, ExportCollectionDocument.FileName)), null),
                new ExportArtifactResponse("geoparquet", FileUrl(baseUrl, version.ExportId, VersionFilePath(version.Version, version.Artifacts.Geoparquet.Href)), version.Artifacts.Geoparquet.Bytes),
                new ExportArtifactResponse("pmtiles", FileUrl(baseUrl, version.ExportId, VersionFilePath(version.Version, version.Artifacts.Pmtiles.Href)), version.Artifacts.Pmtiles.Bytes)
            ]
        };

    private static string VersionFilePath(string version, string fileName)
        => $"{Uri.EscapeDataString(version)}/{Uri.EscapeDataString(fileName)}";

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

    private static string FileUrl(string baseUrl, string exportId, string versionFilePath)
        => $"{baseUrl}/api/v1/exports/{Uri.EscapeDataString(exportId)}/{versionFilePath}";

    private async Task<IReadOnlyList<TileOperationProgress>> ExportJobsAsync(CancellationToken cancellationToken)
    {
        var all = await jobs.ListAsync(TileOperationListScope.All, cancellationToken).ConfigureAwait(false);
        return [.. all.Where(job => job.Operation == TileCacheExecutionSpecBuilder.ExportOperation && job.ExportId is not null)];
    }

    // Listing prefixes the provider's own key prefix, so it takes the folder below it.
    private Task<IReadOnlyList<CloudFile>> ListFilesAsync(string? subFolder, CancellationToken cancellationToken)
        => storage.ListFilesAsync(ExportStore.ListFolder(options.Value, subFolder), int.MaxValue, includeMetadata: false, cancellationToken);

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
