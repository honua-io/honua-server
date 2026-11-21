// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using Honua.Core.Configuration;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Exports;
using Honua.Core.Features.Styling.Abstractions;
using Honua.Infrastructure.Progress;
using Honua.Infrastructure.Services;
using Honua.Server.Features.Admin.Exports;
using Honua.Server.Features.Protocols.Tiles.PMTilesProxy;
using Microsoft.Extensions.Options;

namespace Honua.Server.Features.Admin.TileOperations;

// Export: one capture becomes a PMTiles and a GeoParquet artifact under an immutable
// version, and the publication's pointer moves to that version only after both validate.
internal sealed partial class TileOperationExecutionCore
{
    private sealed record ExportPublishContext
    {
        public required ExportVersionRun Run { get; init; }

        public required ExportLayerSource Source { get; init; }

        public required ExportFields Fields { get; init; }

        public required ExportStore Store { get; init; }

        public string? PointerETag { get; init; }

        public string? UnpublishedETag { get; init; }
    }

    private sealed record ExportSourceCheck
    {
        public required ExportStore Store { get; init; }

        public required ExportLayerSource Source { get; init; }

        public string? PointerETag { get; init; }

        public string? UnpublishedETag { get; init; }
    }

    private sealed record ExportArtifactFiles(Stream Archive, string ArchiveSha256, byte[] Parquet, string ParquetSha256);

    private async Task<TileOperationProgress> ExecuteExportAsync(
        TileOperationProgress progress,
        TileOperationStartRequest request,
        IReadOnlyDictionary<int, TileSource> tileSources,
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        try
        {
            return await PublishExportAsync(progress, request, tileSources, services, cancellationToken).ConfigureAwait(false);
        }
        catch (ExportException ex)
        {
            return ExportFailure(progress, ex.Message);
        }
        catch (GeoParquetLimitExceededException)
        {
            return ExportFailure(progress, "The layer exceeds the configured GeoParquet encoding limits.");
        }
        catch (ParquetRuntimeUnavailableException)
        {
            return ExportFailure(progress, "GeoParquet encoding is unavailable on this server.");
        }
    }

    private async Task<TileOperationProgress> PublishExportAsync(
        TileOperationProgress progress,
        TileOperationStartRequest request,
        IReadOnlyDictionary<int, TileSource> tileSources,
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        var graphProvider = services.GetRequiredService<IMetadataV2GraphProvider>();
        var context = await OpenExportAsync(request, graphProvider, services, cancellationToken).ConfigureAwait(false);
        if (tileSources[request.LayerId!.Value].StorageLayerId != context.Source.StorageLayerId)
        {
            throw new ExportException("The layer was rebound while the publish started; retry the publish.");
        }

        var previous = await context.Store.BeginOperationAsync(progress.JobId, context.Run, cancellationToken).ConfigureAwait(false);
        if (previous is not null)
        {
            return await RecoverActivatedAsync(context, previous, progress, cancellationToken).ConfigureAwait(false);
        }

        var reader = await context.Source.ResolveReaderAsync(graphProvider, services, cancellationToken).ConfigureAwait(false);
        var capture = await ExportFeatureCapture.ReadAsync(
            reader, context.Source.StorageLayerId, context.Fields, cancellationToken).ConfigureAwait(false);
        var limits = services.GetRequiredService<IOptions<LimitsOptions>>().Value;
        var extensions = ExportMetadata(context.Run);
        var parquet = capture.EncodeGeoParquet(context.Source.Resource, context.Fields, limits, extensions);
        var inputs = new PMTilesBuildInputs(extensions, [.. context.Fields.Hidden, .. capture.UndeclaredAttributes], UnbudgetedTileLimits());
        var build = await BuildPMTilesArchiveAsync(
            TileCacheExecutionSpecBuilder.ExportOperation, progress, request with { Bbox = capture.Bounds },
            request.LayerId.Value, tileSources, graphProvider, inputs, cancellationToken).ConfigureAwait(false);
        if (!build.HasArchive)
        {
            return build.Progress;
        }

        await using var archive = build.ArchiveStream;
        RequireCompleteArchive(build);
        var current = build.Progress with { CurrentPhase = "Uploading Export files" };
        await WriteProgressAsync(current, cancellationToken).ConfigureAwait(false);
        var artifacts = new ExportArtifactFiles(
            archive, await Sha256Async(archive, cancellationToken).ConfigureAwait(false),
            parquet, Convert.ToHexStringLower(SHA256.HashData(parquet)));
        try
        {
            var checks = await StoreAndCheckAsync(context, artifacts, current.JobId, graphProvider, cancellationToken).ConfigureAwait(false);
            var descriptor = Validated(context, capture, Describe(context, artifacts, build), checks);
            var layer = await LayerFactsAsync(context, services, cancellationToken).ConfigureAwait(false);
            var companions = new ExportCompanionInput(descriptor, layer);
            companions = companions with { Thumbnail = TryRenderThumbnail(context, capture, companions) };
            return await ActivateAsync(current, context, companions, build, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not ExportActivationUnknownException)
        {
            await DeleteUnusedVersionAsync(context, services, current.JobId).ConfigureAwait(false);
            throw;
        }
    }

    private static async Task<ExportPublishContext> OpenExportAsync(
        TileOperationStartRequest request,
        IMetadataV2GraphProvider graphProvider,
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(request.TileMatrixSetId, "WebMercatorQuad", StringComparison.OrdinalIgnoreCase))
        {
            throw new ExportException("Only TileMatrixSetId 'WebMercatorQuad' is currently supported.");
        }

        var storage = services.GetService<ICloudFileStorage>()
            ?? throw new ExportException("Cloud storage is not configured. Export requires cloud storage.");
        var options = services.GetRequiredService<IOptions<CloudStorageOptions>>().Value;
        var store = ExportStore.Create(storage, options, request.ExportId!);
        var pointerETag = await store.ReadPointerETagAsync(cancellationToken).ConfigureAwait(false);
        var unpublishedETag = await store.ReadUnpublishedETagAsync(cancellationToken).ConfigureAwait(false);

        var source = await ExportLayerSource.OpenAsync(
            graphProvider, services, request.ServiceId!, request.LayerId!.Value, cancellationToken).ConfigureAwait(false);
        await RequireSameSourceAsync(
            new ExportSourceCheck { Store = store, Source = source, PointerETag = pointerETag, UnpublishedETag = unpublishedETag },
            cancellationToken).ConfigureAwait(false);

        var fields = ExportFields.Resolve(source.Resource, request.IdentityField!);
        var capturedAt = DateTimeOffset.UtcNow;
        var captureId = $"capture-{Guid.NewGuid():N}";
        var run = new ExportVersionRun(
            request.ExportId!, ExportVersionFactory.NewVersion(capturedAt, captureId), captureId, capturedAt);
        return new ExportPublishContext { Run = run, Source = source, Fields = fields, Store = store, PointerETag = pointerETag, UnpublishedETag = unpublishedETag };
    }

    // An Export id names one Dataset; another Layer must not replace it, even after unpublish.
    private static async Task RequireSameSourceAsync(ExportSourceCheck check, CancellationToken cancellationToken)
    {
        var serviceId = check.Source.Published.Service.Metadata.Id;
        var layerId = check.Source.Resource.Metadata.Id;
        if (check.PointerETag is not null)
        {
            var pointer = await check.Store.ReadPointerAsync(cancellationToken).ConfigureAwait(false);
            var active = pointer is null ? null : await check.Store.ReadCollectionAsync(pointer, cancellationToken).ConfigureAwait(false);
            if (active is null || active.SourceLayer.Service != serviceId || active.SourceLayer.Layer != layerId)
            {
                throw new ExportException(
                    "This export id already exports a different layer; choose another export id.");
            }

            return;
        }

        if (check.UnpublishedETag is not null)
        {
            var marker = await check.Store.ReadUnpublishedAsync(cancellationToken).ConfigureAwait(false);
            if (marker is not null && !string.IsNullOrEmpty(marker.ServiceId)
                && (marker.ServiceId != serviceId || marker.LayerId != layerId))
            {
                throw new ExportException(
                    "This export id already exports a different layer; choose another export id.");
            }
        }

        var existing = await check.Store.FindAnyStoredVersionAsync(cancellationToken).ConfigureAwait(false);
        if (existing is not null && (existing.SourceLayer.Service != serviceId || existing.SourceLayer.Layer != layerId))
        {
            throw new ExportException(
                "This export id already exports a different layer; choose another export id.");
        }
    }

    private static ImmutableDictionary<string, string> ExportMetadata(ExportVersionRun run)
        => ImmutableDictionary<string, string>.Empty
            .Add(ExportFeatureCapture.CaptureIdMetadataKey, run.CaptureId)
            .Add(ExportFeatureCapture.PublicationIdMetadataKey, run.ExportId)
            .Add(ExportFeatureCapture.VersionMetadataKey, run.Version);

    // The per-tile budget may drop attributes or features to fit a size; a paired archive must hold
    // every captured feature with its attributes, so only maxTiles bounds this build.
    private TileLimits UnbudgetedTileLimits() => new()
    {
        MaxTilesPerRequest = _tileLimits.MaxTilesPerRequest,
        MaxZoomLevel = _tileLimits.MaxZoomLevel,
        MinZoomLevel = _tileLimits.MinZoomLevel,
        TileGenerationTimeout = _tileLimits.TileGenerationTimeout,
        MaxTileSize = long.MaxValue,
        MaxFeaturesPerTile = int.MaxValue
    };

    private static void RequireCompleteArchive(PMTilesBuildResult build)
    {
        if (build.Failed > 0)
        {
            throw new ExportException($"{build.Failed} tiles failed during generation; nothing was published.");
        }

        if (build.WasTruncated)
        {
            throw new ExportException("The tile pyramid exceeds maxTiles; raise maxTiles or lower maxZoom so every captured feature is tiled.");
        }
    }

    private static async Task<List<ExportValidationCheck>> StoreAndCheckAsync(
        ExportPublishContext context,
        ExportArtifactFiles artifacts,
        string jobId,
        IMetadataV2GraphProvider graphProvider,
        CancellationToken cancellationToken)
    {
        var run = context.Run;
        var pmtilesKey = context.Store.VersionKey(run.Version, ExportVersionFactory.PmtilesFileName(run));
        var parquetKey = context.Store.VersionKey(run.Version, ExportVersionFactory.GeoparquetFileName(run));
        var tags = ImmutableDictionary<string, string>.Empty
            .Add("jobId", jobId)
            .Add("layerId", context.Source.StorageLayerId.ToString(CultureInfo.InvariantCulture));

        // The paired operation tag keeps these archives out of the generic PMTiles range proxy, which
        // serves only "publish" objects and cannot apply the publication read policy.
        await context.Store.PutOnceAsync(pmtilesKey, artifacts.Archive, PmtilesExportArtifact.MediaTypeValue,
            PMTilesProxySourceResolver.StampPublishMetadata(
                tags.Add("operation", TileCacheExecutionSpecBuilder.ExportOperation), context.Source.Published),
            cancellationToken).ConfigureAwait(false);
        using (var parquet = new MemoryStream(artifacts.Parquet, writable: false))
        {
            await context.Store.PutOnceAsync(parquetKey, parquet, GeoparquetExportArtifact.MediaTypeValue,
                tags.Add("operation", TileCacheExecutionSpecBuilder.ExportOperation), cancellationToken).ConfigureAwait(false);
        }

        var unchanged = await context.Source.IsUnchangedAsync(graphProvider, cancellationToken).ConfigureAwait(false);
        return
        [
            await StoredCopyCheckAsync(context.Store, (pmtilesKey, artifacts.ArchiveSha256), "pmtiles-stored", cancellationToken).ConfigureAwait(false),
            await StoredCopyCheckAsync(context.Store, (parquetKey, artifacts.ParquetSha256), "geoparquet-stored", cancellationToken).ConfigureAwait(false),
            unchanged
                ? new ExportValidationCheck("source-unchanged", ExportValidation.Passed)
                : new ExportValidationCheck("source-unchanged", ExportValidation.Failed)
                {
                    Detail = "The layer or its metadata changed during capture; retry the publish."
                }
        ];
    }

    private static StoredPair Describe(ExportPublishContext context, ExportArtifactFiles artifacts, PMTilesBuildResult build)
    {
        var run = context.Run;
        return new StoredPair(
            new PmtilesExportArtifact
            {
                Href = ExportVersionFactory.PmtilesFileName(run),
                Bytes = artifacts.Archive.Length,
                Sha256 = artifacts.ArchiveSha256,
                CaptureId = run.CaptureId,
                Fields = context.Fields.TileFields,
                VectorLayer = "layer",
                Minzoom = build.MinZoom,
                Maxzoom = build.MaxZoom
            },
            new GeoparquetExportArtifact
            {
                Href = ExportVersionFactory.GeoparquetFileName(run),
                Bytes = artifacts.Parquet.Length,
                Sha256 = artifacts.ParquetSha256,
                CaptureId = run.CaptureId,
                Fields = context.Fields.ParquetFields
            });
    }

    private static ExportVersion Validated(
        ExportPublishContext context,
        ExportFeatureCapture capture,
        StoredPair artifacts,
        List<ExportValidationCheck> checks)
    {
        var draft = ExportVersionFactory.Create(context.Run, context.Source, capture, context.Fields,
            (artifacts, context.Fields.IdentityField, new ExportValidation(ExportValidation.Passed, DateTimeOffset.UtcNow, checks)));
        var violations = ExportVersionRules.Violations(draft);
        checks.Add(violations.Count == 0
            ? new ExportValidationCheck("descriptor-rules", ExportValidation.Passed)
            : new ExportValidationCheck("descriptor-rules", ExportValidation.Failed) { Detail = string.Join(", ", violations) });

        var failed = checks.Where(check => check.Status != ExportValidation.Passed).ToList();
        if (failed.Count > 0)
        {
            throw new ExportException("Validation failed: " + string.Join("; ",
                failed.Select(check => check.Detail is null ? check.Name : $"{check.Name} ({check.Detail})")));
        }

        return draft with { Validation = draft.Validation with { Checks = checks } };
    }

    // The companion files describe the layer's public metadata and stored style as they are at publish time.
    private async Task<ExportLayerFacts> LayerFactsAsync(
        ExportPublishContext context, IServiceProvider services, CancellationToken cancellationToken)
    {
        var service = context.Source.Published.Service.Metadata;
        var style = await TryReadStoredStyleAsync(context, services, cancellationToken).ConfigureAwait(false);
        var host = new[] { service.Publisher, service.Title, service.Name }.FirstOrDefault(name => !string.IsNullOrWhiteSpace(name))?.Trim()
            ?? service.Id;
        return new ExportLayerFacts(
            context.Source.Resource.Metadata.WithServiceGovernanceFallbacks(service), host, style);
    }

    // The companion files are a convenience: a style catalog outage falls back to default paints
    // rather than failing a publish whose artifacts are already stored.
    private async Task<string?> TryReadStoredStyleAsync(
        ExportPublishContext context, IServiceProvider services, CancellationToken cancellationToken)
    {
        try
        {
            return services.GetService<ILayerStyleCatalog>() is { } catalog
                ? (await catalog.GetLayerStyleAsync(context.Source.StorageLayerId, cancellationToken).ConfigureAwait(false))?.MapLibreStyleJson
                : null;
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested && ex is not OutOfMemoryException)
        {
            TileOperationLog.ExportStoredStyleSkipped(_logger, VersionLabel(context.Run), ex);
            return null;
        }
    }

    // A server image without the native renderer, or a style the renderer rejects, publishes without a thumbnail.
    private byte[]? TryRenderThumbnail(
        ExportPublishContext context, ExportFeatureCapture capture, ExportCompanionInput companions)
    {
        try
        {
            var style = ExportStyleAsset.PublicStoredStyle(
                companions.StoredMapLibreStyle, [.. companions.PublicFields.Select(field => field.Name)]);
            return ExportThumbnail.Render(
                new ExportThumbnailSource(capture.Features.Items, capture.Bounds, context.Source.Resource.ReadGeometryType()), style);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            TileOperationLog.ExportThumbnailSkipped(_logger, VersionLabel(context.Run), ex);
            return null;
        }
    }

    private static string VersionLabel(ExportVersionRun run) => $"{run.PublicationId}/{run.Version}";

    private async Task<TileOperationProgress> ActivateAsync(
        TileOperationProgress current,
        ExportPublishContext context,
        ExportCompanionInput validated,
        PMTilesBuildResult build,
        CancellationToken cancellationToken)
    {
        var run = context.Run;
        var companions = validated with { Descriptor = validated.Descriptor with { PublishedAt = DateTimeOffset.UtcNow } };
        var collectionKey = context.Store.VersionKey(run.Version, ExportCollectionDocument.FileName);
        await PutCompanionFilesAsync(context, companions, cancellationToken).ConfigureAwait(false);
        await context.Store.PutJsonOnceAsync(collectionKey, ExportVersionFactory.Collection(companions),
            ExportVersionJsonContext.Default.JsonObject, cancellationToken).ConfigureAwait(false);

        var currentUnpublishedETag = await context.Store.ReadUnpublishedETagAsync(cancellationToken).ConfigureAwait(false);
        if (currentUnpublishedETag is not null
            && !string.Equals(currentUnpublishedETag, context.UnpublishedETag, StringComparison.Ordinal))
        {
            throw new ExportException("This Export was unpublished while the job was running; its version was not activated.");
        }

        await SwitchPointerAsync(context.Store, context.Run, context.PointerETag).ConfigureAwait(false);
        if (context.UnpublishedETag is not null)
        {
            await context.Store.ClearUnpublishedAsync(CancellationToken.None).ConfigureAwait(false);
        }

        TileOperationLog.ExportActivated(_logger, current.JobId, run.ExportId, run.Version);
        return current with
        {
            ArchiveSizeBytes = build.ArchiveSize,
            PublishedExportVersion = new PublishedExportVersion(run.ExportId, run.Version, run.CaptureId, collectionKey, context.Store.PointerKey),
            Status = OperationStatus.Completed,
            CompletedAt = DateTimeOffset.UtcNow,
            ErrorMessage = null,
            CurrentPhase = "Export completed"
        };
    }

    private static async Task PutCompanionFilesAsync(
        ExportPublishContext context, ExportCompanionInput companions, CancellationToken cancellationToken)
    {
        foreach (var file in ExportCompanionFiles.Render(companions))
        {
            await PutTextOnceAsync(context, file, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task PutTextOnceAsync(ExportPublishContext context, ExportCompanionFile file, CancellationToken cancellationToken)
    {
        using var body = new MemoryStream(file.Content, writable: false);
        await context.Store.PutOnceAsync(context.Store.VersionKey(context.Run.Version, file.FileName), body, file.MediaType,
            ImmutableDictionary<string, string>.Empty, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<ExportValidationCheck> StoredCopyCheckAsync(
        ExportStore store,
        (string Key, string Sha256) expected,
        string name,
        CancellationToken cancellationToken)
    {
        var stored = await store.ReadBackAsync(expected.Key, cancellationToken).ConfigureAwait(false);
        return stored is { } copy && copy.Sha256 == expected.Sha256
            ? new ExportValidationCheck(name, ExportValidation.Passed)
            : new ExportValidationCheck(name, ExportValidation.Failed) { Detail = "The stored copy does not match the generated bytes." };
    }

    private static async Task<string> Sha256Async(Stream stream, CancellationToken cancellationToken)
    {
        stream.Position = 0;
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        stream.Position = 0;
        return Convert.ToHexStringLower(hash);
    }

    private async Task DeleteUnusedVersionAsync(ExportPublishContext context, IServiceProvider services, string jobId)
    {
        var run = context.Run;
        if (await context.Store.PointerStateAsync(run.Version).ConfigureAwait(false) != PointerState.NamesOther)
        {
            return;
        }

        var storage = services.GetRequiredService<ICloudFileStorage>();
        foreach (var file in new[]
                 {
                     ExportVersionFactory.PmtilesFileName(run),
                     ExportVersionFactory.GeoparquetFileName(run),
                     ExportCollectionDocument.FileName
                 }.Concat(ExportCompanionFiles.FileNames))
        {
            await TryDeletePublishArtifactAsync(storage, context.Store.VersionKey(run.Version, file), jobId, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private static TileOperationProgress ExportFailure(TileOperationProgress progress, string message)
        => progress with
        {
            Status = OperationStatus.Failed,
            CompletedAt = DateTimeOffset.UtcNow,
            ErrorMessage = message,
            CurrentPhase = "Failed"
        };
}
