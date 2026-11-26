// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using System.Text.RegularExpressions;
using Honua.Core.Features.Exports;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Infrastructure.Progress;
using Honua.Server.Features.Admin.TileOperations;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Honua.Server.Features.Admin.Exports;

internal readonly record struct ExportListPage(int? Limit = null, int? Offset = null);

internal sealed record ExportCatalogDependencies
{
    public required ICloudFileStorage Storage { get; init; }

    public required IOptions<CloudStorageOptions> Options { get; init; }

    public required ITileCacheJobService Jobs { get; init; }

    public PublicExportRevocationService? PublicRevocation { get; init; }

    // Durable promoted-export records; a revocation removes the record, so it decides whether the
    // public copy a job once promoted is still served.
    public IPublicExportStateStore? PublicState { get; init; }

    public ILogger<ExportCatalog>? Logger { get; init; }
}

// Describes Exports from what storage and the job log hold: the pointer names the current Version,
// version folders hold earlier ones, and the newest export job says whether one is running or failed.
internal sealed partial class ExportCatalog(ExportCatalogDependencies deps)
{
    private readonly ICloudFileStorage storage = deps.Storage;
    private readonly IOptions<CloudStorageOptions> options = deps.Options;
    private readonly ITileCacheJobService jobs = deps.Jobs;
    private readonly PublicExportRevocationService? publicRevocation = deps.PublicRevocation;
    private readonly IPublicExportStateStore? publicState = deps.PublicState;
    private readonly ILogger<ExportCatalog> logger = deps.Logger ?? NullLogger<ExportCatalog>.Instance;
    internal const string NotPublicReason = "The public data path is not enabled for Exports on this installation.";
    internal const string PublicCopyWithdrawnReason =
        "The public copy of this Export was withdrawn because its Layer is no longer public or the Export was unpublished.";
    internal const int DefaultListLimit = 50;
    internal const int MaxListLimit = 200;
    internal const int MaxVersionsReturned = 100;

    private sealed record CatalogSnapshot(
        IReadOnlyList<CloudFile> Files,
        IReadOnlyList<TileOperationProgress> ExportJobs,
        string BaseUrl);

    private sealed record ExportState
    {
        public required string ExportId { get; init; }

        public required ExportStore Store { get; init; }

        public ExportVersion? Current { get; init; }

        public required List<string> VersionIds { get; init; }

        public TileOperationProgress? Job { get; init; }

        public PublicExportDeliveryStatus? PublicDelivery { get; init; }
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
        var (ids, exportsWithVersionsOrJobs) = IndexExports(snapshot);
        var items = new List<ExportResponse>();
        foreach (var id in ids)
        {
            if (pageOffset > 0)
            {
                pageOffset -= await CountsTowardOffsetAsync(id, exportsWithVersionsOrJobs, cancellationToken)
                    .ConfigureAwait(false) ? 1 : 0;
                continue;
            }

            var export = await DescribeSummaryAsync(id, snapshot, cancellationToken).ConfigureAwait(false);
            if (export is null)
            {
                continue;
            }

            items.Add(export);
            if (items.Count == pageLimit)
            {
                break;
            }
        }

        return items;
    }

    private async ValueTask<bool> CountsTowardOffsetAsync(
        string id, HashSet<string> exportsWithVersionsOrJobs, CancellationToken cancellationToken)
    {
        // Versions and jobs are enough to count a row. Only pointer-only entries need a storage read.
        return exportsWithVersionsOrJobs.Contains(id) ||
            await ReadCurrentAsync(ExportStore.Create(storage, options.Value, id), id, cancellationToken)
                .ConfigureAwait(false) is not null;
    }

    private static (IEnumerable<string> Ids, HashSet<string> ExportsWithVersionsOrJobs) IndexExports(CatalogSnapshot snapshot)
    {
        var files = snapshot.Files.Select(Match).ToArray();
        var exportsWithVersionsOrJobs = files.Where(file => file.Version is not null)
            .Select(file => file.ExportId)
            .Concat(snapshot.ExportJobs.Select(job => job.ExportId))
            .OfType<string>().ToHashSet(StringComparer.Ordinal);
        var ids = files.Select(file => file.ExportId).OfType<string>()
            .Union(exportsWithVersionsOrJobs, StringComparer.Ordinal).Order(StringComparer.Ordinal);
        return (ids, exportsWithVersionsOrJobs);
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
        var (serviceId, layerId) = await ResolveSourceIdentityAsync(store, exportId, cancellationToken).ConfigureAwait(false);
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

    private async Task<(string ServiceId, string LayerId)> ResolveSourceIdentityAsync(
        ExportStore store, string exportId, CancellationToken cancellationToken)
    {
        var pointer = await TryReadPointerAsync(store, exportId, cancellationToken).ConfigureAwait(false);
        var version = pointer is not null
            ? await TryReadCollectionAsync(store, pointer, cancellationToken).ConfigureAwait(false)
            : null;
        version ??= await TryFindAnyStoredVersionAsync(store, exportId, cancellationToken).ConfigureAwait(false);
        if (version is not null)
        {
            return (version.SourceLayer.Service, version.SourceLayer.Layer);
        }

        var existing = await TryReadUnpublishedAsync(store, exportId, cancellationToken).ConfigureAwait(false);
        return existing is not null ? (existing.ServiceId, existing.LayerId) : (string.Empty, string.Empty);
    }

    private async Task<ExportPointer?> TryReadPointerAsync(
        ExportStore store, string exportId, CancellationToken cancellationToken)
    {
        try
        {
            return await store.ReadPointerAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            LogUnreadableDocument(logger, exportId, ExportStore.PointerFileName, ex);
            return null;
        }
    }

    private async Task<ExportVersion?> TryReadCollectionAsync(
        ExportStore store, ExportPointer pointer, CancellationToken cancellationToken)
    {
        try
        {
            return await store.ReadCollectionAsync(pointer, cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            LogUnreadableDocument(logger, pointer.ExportId, $"{pointer.Version}/{ExportCollectionDocument.FileName}", ex);
            return null;
        }
    }

    private async Task<ExportVersion?> TryFindAnyStoredVersionAsync(
        ExportStore store, string exportId, CancellationToken cancellationToken)
    {
        try
        {
            return await store.FindAnyStoredVersionAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            LogUnreadableDocument(logger, exportId, ExportCollectionDocument.FileName, ex);
            return null;
        }
    }

    private async Task<ExportUnpublishedMarker?> TryReadUnpublishedAsync(
        ExportStore store, string exportId, CancellationToken cancellationToken)
    {
        try
        {
            return await store.ReadUnpublishedAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            LogUnreadableDocument(logger, exportId, ExportStore.UnpublishedFileName, ex);
            return null;
        }
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
        var current = await ReadCurrentAsync(store, exportId, cancellationToken).ConfigureAwait(false);
        var versionIds = snapshot.Files.Select(Match).Where(match => match.ExportId == exportId && match.Version is not null)
            .Select(match => match.Version!).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)
            .TakeLast(MaxVersionsReturned).ToList();
        var job = snapshot.ExportJobs.Where(candidate => candidate.ExportId == exportId).MaxBy(candidate => candidate.StartedAt);
        if (current is null && versionIds.Count == 0 && job is null)
        {
            return null;
        }

        var publicDelivery = await CurrentPublicDeliveryAsync(current, job, cancellationToken).ConfigureAwait(false);
        return new ExportState { ExportId = exportId, Store = store, Current = current, VersionIds = versionIds, Job = job, PublicDelivery = publicDelivery };
    }

    private async Task<ExportVersion?> ReadCurrentAsync(
        ExportStore store, string exportId, CancellationToken cancellationToken)
    {
        var pointer = await TryReadPointerAsync(store, exportId, cancellationToken).ConfigureAwait(false);
        return pointer?.ExportId == exportId
            ? await TryReadCollectionAsync(store, pointer, cancellationToken).ConfigureAwait(false)
            : null;
    }

    // The job records the public delivery at promotion time; a later revocation deletes the promoted
    // record but leaves the job and pointer, so the job alone would keep advertising a deleted copy.
    private async Task<PublicExportDeliveryStatus?> CurrentPublicDeliveryAsync(
        ExportVersion? current, TileOperationProgress? job, CancellationToken cancellationToken)
    {
        var delivery = current is not null && job?.PublishedExportVersion?.Version == current.Version
            ? job.PublishedExportVersion.Public
            : null;
        if (publicState is null || delivery?.Status is not (PublicExportDeliveryStatus.Promoted or PublicExportDeliveryStatus.PromotedUnreadable))
        {
            return delivery;
        }

        // A delivery is only read from a job that published the current Version, so both are set here.
        var promoted = await publicState.GetPromotedAsync(job!.ExportId!, cancellationToken).ConfigureAwait(false);
        return promoted is not null && promoted.Version == current!.Version
            ? delivery
            : new PublicExportDeliveryStatus(PublicExportDeliveryStatus.StaysSignedIn) { Reason = PublicCopyWithdrawnReason };
    }

    private static ExportResponse BuildResponse(
        string exportId, ExportState state, (string BaseUrl, IReadOnlyList<ExportVersionResponse> Versions) detail)
    {
        var publicDelivery = state.PublicDelivery;
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

    private async Task<IReadOnlyList<ExportVersionResponse>> LoadVersionsAsync(
        ExportState state, string baseUrl, CancellationToken cancellationToken)
    {
        var described = new List<ExportVersionResponse>();
        foreach (var versionId in state.VersionIds.Where(id => id != state.Current?.Version))
        {
            var version = await TryReadVersionAsync(state, versionId, cancellationToken).ConfigureAwait(false);
            described.AddRange(version is null ? [] : [Version(version, baseUrl)]);
        }

        described.AddRange(state.Current is null ? [] : [Version(state.Current, baseUrl)]);
        return described;
    }

    private async Task<ExportVersion?> TryReadVersionAsync(
        ExportState state, string versionId, CancellationToken cancellationToken)
    {
        try
        {
            return await state.Store.ReadVersionAsync(versionId, cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            LogUnreadableDocument(logger, state.ExportId, $"{versionId}/{ExportCollectionDocument.FileName}", ex);
            return null;
        }
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

    [LoggerMessage(
        EventId = 10345,
        Level = LogLevel.Warning,
        Message = "Export '{ExportId}' has an unreadable {DocumentName}; treating it as absent.")]
    private static partial void LogUnreadableDocument(
        ILogger logger,
        string exportId,
        string documentName,
        Exception exception);
}
