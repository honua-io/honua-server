// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Honua.Core.Configuration;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Publishing.Descriptors;
using Honua.Infrastructure.Progress;
using Honua.Infrastructure.Services;
using Honua.Server.Features.Admin.PairedPublications;
using Honua.Server.Features.Protocols.Tiles.PMTilesProxy;
using Microsoft.Extensions.Options;

namespace Honua.Server.Features.Admin.TileOperations;

// Paired publish: one capture becomes a PMTiles and a GeoParquet artifact under an immutable
// version, and the publication's pointer moves to that version only after both validate.
internal sealed partial class TileOperationExecutionCore
{
    private sealed record PairedPublishContext(
        PairedPublicationRun Run,
        PairedPublicationSource Source,
        PairedPublicationFields Fields,
        PairedPublicationStore Store,
        string? PointerETag);

    private sealed record PairedArtifacts(Stream Archive, string ArchiveSha256, byte[] Parquet, string ParquetSha256);

    private async Task<TileOperationProgress> ExecutePairedPublishAsync(
        TileOperationProgress progress,
        TileOperationStartRequest request,
        IReadOnlyDictionary<int, TileSource> tileSources,
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        try
        {
            return await PairedPublishAsync(progress, request, tileSources, services, cancellationToken).ConfigureAwait(false);
        }
        catch (PairedPublicationException ex)
        {
            return PairedFailure(progress, ex.Message);
        }
        catch (GeoParquetLimitExceededException)
        {
            return PairedFailure(progress, "The layer exceeds the configured GeoParquet encoding limits.");
        }
        catch (ParquetRuntimeUnavailableException)
        {
            return PairedFailure(progress, "GeoParquet encoding is unavailable on this server.");
        }
    }

    private async Task<TileOperationProgress> PairedPublishAsync(
        TileOperationProgress progress,
        TileOperationStartRequest request,
        IReadOnlyDictionary<int, TileSource> tileSources,
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        var graphProvider = services.GetRequiredService<IMetadataV2GraphProvider>();
        var context = await OpenPairedPublishAsync(request, graphProvider, services, cancellationToken).ConfigureAwait(false);
        if (tileSources[request.LayerId!.Value].StorageLayerId != context.Source.StorageLayerId)
        {
            throw new PairedPublicationException("The layer was rebound while the publish started; retry the publish.");
        }

        var previous = await context.Store.BeginOperationAsync(progress.JobId, context.Run, cancellationToken).ConfigureAwait(false);
        if (previous is not null)
        {
            return await RecoverActivatedAsync(context, previous, progress, cancellationToken).ConfigureAwait(false);
        }

        var reader = await context.Source.ResolveReaderAsync(graphProvider, services, cancellationToken).ConfigureAwait(false);
        var capture = await PairedPublicationCapture.ReadAsync(
            reader, context.Source.StorageLayerId, context.Fields, cancellationToken).ConfigureAwait(false);
        var limits = services.GetRequiredService<IOptions<LimitsOptions>>().Value;
        var extensions = PairedMetadata(context.Run);
        var parquet = capture.EncodeGeoParquet(context.Source.Resource, context.Fields, limits, extensions);
        var inputs = new PMTilesBuildInputs(extensions, [.. context.Fields.Hidden, .. capture.UndeclaredAttributes], UnbudgetedTileLimits());
        var build = await BuildPMTilesArchiveAsync(
            TileCacheExecutionSpecBuilder.PairedPublishOperation, progress, request with { Bbox = capture.Bounds },
            request.LayerId.Value, tileSources, graphProvider, inputs, cancellationToken).ConfigureAwait(false);
        if (!build.HasArchive)
        {
            return build.Progress;
        }

        await using var archive = build.ArchiveStream;
        RequireCompleteArchive(build);
        var current = build.Progress with { CurrentPhase = "Uploading paired publication" };
        await WriteProgressAsync(current, cancellationToken).ConfigureAwait(false);
        var artifacts = new PairedArtifacts(
            archive, await Sha256Async(archive, cancellationToken).ConfigureAwait(false),
            parquet, Convert.ToHexStringLower(SHA256.HashData(parquet)));
        try
        {
            var checks = await StoreAndCheckAsync(context, artifacts, current.JobId, graphProvider, cancellationToken).ConfigureAwait(false);
            var descriptor = Validated(context, capture, Describe(context, artifacts, build), checks);
            return await ActivateAsync(current, context, descriptor, build, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not PairedActivationUnknownException)
        {
            await DeleteUnusedVersionAsync(context, services, current.JobId).ConfigureAwait(false);
            throw;
        }
    }

    private static async Task<PairedPublishContext> OpenPairedPublishAsync(
        TileOperationStartRequest request,
        IMetadataV2GraphProvider graphProvider,
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(request.TileMatrixSetId, "WebMercatorQuad", StringComparison.OrdinalIgnoreCase))
        {
            throw new PairedPublicationException("Only TileMatrixSetId 'WebMercatorQuad' is currently supported.");
        }

        var storage = services.GetService<ICloudFileStorage>()
            ?? throw new PairedPublicationException("Cloud storage is not configured. Paired publish requires cloud storage.");
        var options = services.GetRequiredService<IOptions<CloudStorageOptions>>().Value;
        var store = PairedPublicationStore.Create(storage, options, request.PublicationId!);
        var pointerETag = await store.ReadPointerETagAsync(cancellationToken).ConfigureAwait(false);

        var source = await PairedPublicationSource.OpenAsync(
            graphProvider, services, request.ServiceId!, request.LayerId!.Value, cancellationToken).ConfigureAwait(false);
        if (pointerETag is not null)
        {
            await RequireSameSourceAsync(store, source, cancellationToken).ConfigureAwait(false);
        }

        var fields = PairedPublicationFields.Resolve(source.Resource, request.IdentityField!);
        var capturedAt = DateTimeOffset.UtcNow;
        var captureId = $"capture-{Guid.NewGuid():N}";
        var run = new PairedPublicationRun(
            request.PublicationId!, PairedPublicationDescriptorFactory.NewVersion(capturedAt, captureId), captureId, capturedAt);
        return new PairedPublishContext(run, source, fields, store, pointerETag);
    }

    // A publication id names one dataset; another layer must not silently replace it for its readers.
    private static async Task RequireSameSourceAsync(
        PairedPublicationStore store,
        PairedPublicationSource source,
        CancellationToken cancellationToken)
    {
        var pointer = await store.ReadPointerAsync(cancellationToken).ConfigureAwait(false);
        var active = pointer is null ? null : await store.ReadDescriptorAsync(pointer, cancellationToken).ConfigureAwait(false);
        if (active is null
            || active.SourceLayer.Service != source.Published.Service.Metadata.Id
            || active.SourceLayer.Layer != source.Resource.Metadata.Id)
        {
            throw new PairedPublicationException(
                "This publication id already publishes a different layer; choose another publication id.");
        }
    }

    private static ImmutableDictionary<string, string> PairedMetadata(PairedPublicationRun run)
        => ImmutableDictionary<string, string>.Empty
            .Add(PairedPublicationCapture.CaptureIdMetadataKey, run.CaptureId)
            .Add(PairedPublicationCapture.PublicationIdMetadataKey, run.PublicationId)
            .Add(PairedPublicationCapture.VersionMetadataKey, run.Version);

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
            throw new PairedPublicationException($"{build.Failed} tiles failed during generation; nothing was published.");
        }

        if (build.WasTruncated)
        {
            throw new PairedPublicationException("The tile pyramid exceeds maxTiles; raise maxTiles or lower maxZoom so every captured feature is tiled.");
        }
    }

    private static async Task<List<PublicationValidationCheck>> StoreAndCheckAsync(
        PairedPublishContext context,
        PairedArtifacts artifacts,
        string jobId,
        IMetadataV2GraphProvider graphProvider,
        CancellationToken cancellationToken)
    {
        var run = context.Run;
        var pmtilesKey = context.Store.VersionKey(run.Version, PairedPublicationDescriptorFactory.PmtilesFileName(run));
        var parquetKey = context.Store.VersionKey(run.Version, PairedPublicationDescriptorFactory.GeoparquetFileName(run));
        var tags = ImmutableDictionary<string, string>.Empty
            .Add("jobId", jobId)
            .Add("layerId", context.Source.StorageLayerId.ToString(CultureInfo.InvariantCulture));

        // The paired operation tag keeps these archives out of the generic PMTiles range proxy, which
        // serves only "publish" objects and cannot apply the publication read policy.
        await context.Store.PutOnceAsync(pmtilesKey, artifacts.Archive, PmtilesPublicationArtifact.MediaTypeValue,
            PMTilesProxySourceResolver.StampPublishMetadata(
                tags.Add("operation", TileCacheExecutionSpecBuilder.PairedPublishOperation), context.Source.Published),
            cancellationToken).ConfigureAwait(false);
        using (var parquet = new MemoryStream(artifacts.Parquet, writable: false))
        {
            await context.Store.PutOnceAsync(parquetKey, parquet, GeoparquetPublicationArtifact.MediaTypeValue,
                tags.Add("operation", TileCacheExecutionSpecBuilder.PairedPublishOperation), cancellationToken).ConfigureAwait(false);
        }

        var unchanged = await context.Source.IsUnchangedAsync(graphProvider, cancellationToken).ConfigureAwait(false);
        return
        [
            await StoredCopyCheckAsync(context.Store, (pmtilesKey, artifacts.ArchiveSha256), "pmtiles-stored", cancellationToken).ConfigureAwait(false),
            await StoredCopyCheckAsync(context.Store, (parquetKey, artifacts.ParquetSha256), "geoparquet-stored", cancellationToken).ConfigureAwait(false),
            unchanged
                ? new PublicationValidationCheck("source-unchanged", PublicationValidation.Passed)
                : new PublicationValidationCheck("source-unchanged", PublicationValidation.Failed)
                {
                    Detail = "The layer or its metadata changed during capture; retry the publish."
                }
        ];
    }

    private static StoredPair Describe(PairedPublishContext context, PairedArtifacts artifacts, PMTilesBuildResult build)
    {
        var run = context.Run;
        return new StoredPair(
            new PmtilesPublicationArtifact
            {
                Href = PairedPublicationDescriptorFactory.PmtilesFileName(run),
                Bytes = artifacts.Archive.Length,
                Sha256 = artifacts.ArchiveSha256,
                CaptureId = run.CaptureId,
                Fields = context.Fields.TileFields,
                VectorLayer = "layer",
                Minzoom = build.MinZoom,
                Maxzoom = build.MaxZoom
            },
            new GeoparquetPublicationArtifact
            {
                Href = PairedPublicationDescriptorFactory.GeoparquetFileName(run),
                Bytes = artifacts.Parquet.Length,
                Sha256 = artifacts.ParquetSha256,
                CaptureId = run.CaptureId,
                Fields = context.Fields.ParquetFields
            });
    }

    private static PublicationDescriptor Validated(
        PairedPublishContext context,
        PairedPublicationCapture capture,
        StoredPair artifacts,
        List<PublicationValidationCheck> checks)
    {
        var draft = PairedPublicationDescriptorFactory.Create(context.Run, context.Source, capture, context.Fields,
            (artifacts, context.Fields.IdentityField, new PublicationValidation(PublicationValidation.Passed, DateTimeOffset.UtcNow, checks)));
        var violations = PublicationDescriptorRules.Violations(draft);
        checks.Add(violations.Count == 0
            ? new PublicationValidationCheck("descriptor-rules", PublicationValidation.Passed)
            : new PublicationValidationCheck("descriptor-rules", PublicationValidation.Failed) { Detail = string.Join(", ", violations) });

        var failed = checks.Where(check => check.Status != PublicationValidation.Passed).ToList();
        if (failed.Count > 0)
        {
            throw new PairedPublicationException("Validation failed: " + string.Join("; ",
                failed.Select(check => check.Detail is null ? check.Name : $"{check.Name} ({check.Detail})")));
        }

        return draft with { Validation = draft.Validation with { Checks = checks } };
    }

    private async Task<TileOperationProgress> ActivateAsync(
        TileOperationProgress current,
        PairedPublishContext context,
        PublicationDescriptor validated,
        PMTilesBuildResult build,
        CancellationToken cancellationToken)
    {
        var run = context.Run;
        var descriptor = validated with { PublishedAt = DateTimeOffset.UtcNow };
        var descriptorKey = context.Store.VersionKey(run.Version, PairedPublicationDescriptorFactory.DescriptorFileName);
        await context.Store.PutJsonOnceAsync(descriptorKey, descriptor,
            PublicationDescriptorJsonContext.Default.PublicationDescriptor, cancellationToken).ConfigureAwait(false);
        using (var item = new MemoryStream(Encoding.UTF8.GetBytes(PairedPublicationDescriptorFactory.StacItem(descriptor).ToJsonString())))
        {
            await context.Store.PutOnceAsync(context.Store.VersionKey(run.Version, PairedPublicationDescriptorFactory.StacItemFileName),
                item, "application/geo+json", ImmutableDictionary<string, string>.Empty, cancellationToken).ConfigureAwait(false);
        }

        await SwitchPointerAsync(context.Store, context.Run, context.PointerETag).ConfigureAwait(false);
        TileOperationLog.PairedPublishActivated(_logger, current.JobId, run.PublicationId, run.Version);
        return current with
        {
            ArchiveSizeBytes = build.ArchiveSize,
            PublishedPublication = new PublishedPublication(run.PublicationId, run.Version, run.CaptureId, descriptorKey, context.Store.PointerKey),
            Status = OperationStatus.Completed,
            CompletedAt = DateTimeOffset.UtcNow,
            ErrorMessage = null,
            CurrentPhase = "Paired publish completed"
        };
    }

    private static async Task<PublicationValidationCheck> StoredCopyCheckAsync(
        PairedPublicationStore store,
        (string Key, string Sha256) expected,
        string name,
        CancellationToken cancellationToken)
    {
        var stored = await store.ReadBackAsync(expected.Key, cancellationToken).ConfigureAwait(false);
        return stored is { } copy && copy.Sha256 == expected.Sha256
            ? new PublicationValidationCheck(name, PublicationValidation.Passed)
            : new PublicationValidationCheck(name, PublicationValidation.Failed) { Detail = "The stored copy does not match the generated bytes." };
    }

    private static async Task<string> Sha256Async(Stream stream, CancellationToken cancellationToken)
    {
        stream.Position = 0;
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        stream.Position = 0;
        return Convert.ToHexStringLower(hash);
    }

    private async Task DeleteUnusedVersionAsync(PairedPublishContext context, IServiceProvider services, string jobId)
    {
        var run = context.Run;
        if (await context.Store.PointerStateAsync(run.Version).ConfigureAwait(false) != PointerState.NamesOther)
        {
            return;
        }

        var storage = services.GetRequiredService<ICloudFileStorage>();
        foreach (var file in new[]
                 {
                     PairedPublicationDescriptorFactory.PmtilesFileName(run),
                     PairedPublicationDescriptorFactory.GeoparquetFileName(run),
                     PairedPublicationDescriptorFactory.DescriptorFileName,
                     PairedPublicationDescriptorFactory.StacItemFileName
                 })
        {
            await TryDeletePublishArtifactAsync(storage, context.Store.VersionKey(run.Version, file), jobId, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private static TileOperationProgress PairedFailure(TileOperationProgress progress, string message)
        => progress with
        {
            Status = OperationStatus.Failed,
            CompletedAt = DateTimeOffset.UtcNow,
            ErrorMessage = message,
            CurrentPhase = "Failed"
        };
}
