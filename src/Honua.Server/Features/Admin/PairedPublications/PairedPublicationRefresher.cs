// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Authorization;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Coordination;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Publishing.Descriptors;
using Honua.Server.Features.Admin.TileOperations;
using Microsoft.Extensions.Options;

namespace Honua.Server.Features.Admin.PairedPublications;

// Recipes live in the canonical graph; queue records and coalescing leases live in shared storage.
internal sealed partial class PairedPublicationRefresher(
    IServiceProvider services, IMetadataV2GraphProvider graphProvider, ICloudFileStorage storage,
    IOptions<CloudStorageOptions> options, ITileCacheJobService jobs, IChangeTracker changes,
    ILogger<PairedPublicationRefresher> logger, PairedPublicationRefreshLease coordination)
{
    internal async Task SweepAsync(CancellationToken cancellationToken)
    {
        if (coordination.Store is null)
        {
            return; // A node without shared coordination must not independently publish.
        }
        using var security = JobSecurityScope.BeginSystem();
        var snapshot = await graphProvider.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
        foreach (var publication in snapshot.Graph.Publications.Where(layer => layer.PairedPublication is not null && snapshot.IsRoutable(layer)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await RefreshAsync(publication, snapshot.Index.ServicesById[publication.ServiceId].EffectivePublicName, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                Log.RefreshFailed(logger, publication.Metadata.Id, exception);
            }
        }
    }

    private async Task RefreshAsync(MetadataV2Layer publication, string serviceName, CancellationToken cancellationToken)
    {
        var recipe = publication.PairedPublication!;
        if (publication.LayerIndex is not { } layerId)
        {
            throw new PairedPublicationException("Automatic publication requires a routable numeric layer.");
        }
        var source = await PairedPublicationSource.OpenAsync(
            graphProvider, services, serviceName, layerId, cancellationToken).ConfigureAwait(false);
        if (source.Published.Publication.Metadata.Id != publication.Metadata.Id
            || source.Published.Publication.PairedPublication != recipe)
        {
            throw new PairedPublicationException("Automatic publication cannot refresh a retired, rebound or changed recipe.");
        }
        var request = CreateRequest(source, recipe, layerId);
        var schema = services.GetService<ISchemaContext>()?.CurrentSchema;
        var claim = new LeaseClaim($"publication-refresh:{schema}:{recipe.PublicationId}", Guid.NewGuid().ToString("N"));
        if (!await coordination.Store!.TryAcquireAsync(claim, TimeSpan.FromSeconds(60), cancellationToken).ConfigureAwait(false))
        {
            return;
        }
        // Retain the short lease to coalesce independent tick deliveries, including completed fast jobs.
        if (await NeedsRefreshAsync(source, request, cancellationToken).ConfigureAwait(false))
        {
            await jobs.SubmitAsync(new TileOperationSubmission(request, TileCacheJobService.SchedulerIdentityName, schema),
                cancellationToken).ConfigureAwait(false);
        }
    }

    private static TileOperationStartRequest CreateRequest(
        PairedPublicationSource source, MetadataV2PairedPublication recipe, int layerId)
    {
        // Validate field visibility before queuing.
        _ = PairedPublicationFields.Resolve(source.Resource, recipe.IdentityField);
        var request = new TileOperationStartRequest
        {
            Operation = "paired-publish", PublicationId = recipe.PublicationId, IdentityField = recipe.IdentityField,
            ServiceId = source.Published.Service.EffectivePublicName, LayerId = layerId,
            MinZoom = recipe.MinZoom, MaxZoom = recipe.MaxZoom, MaxTiles = recipe.MaxTiles,
            PublicationRevision = source.MetadataRevision
        };
        if (PairedPublishRequestRules.Problem(request) is not null
            || recipe.MinZoom < 0 || recipe.MaxZoom < recipe.MinZoom || recipe.MaxZoom > 22
            || recipe.MaxTiles is < 1 or > 1000000)
        {
            throw new PairedPublicationException("Invalid automatic publication recipe.");
        }
        return request;
    }

    private async Task<bool> NeedsRefreshAsync(
        PairedPublicationSource source, TileOperationStartRequest request, CancellationToken cancellationToken)
    {
        var active = await jobs.ListAsync(TileOperationListScope.Active, cancellationToken).ConfigureAwait(false);
        if (active.Any(job => job.Operation == request.Operation && job.LayerId == request.LayerId
            && string.Equals(job.ServiceId, request.ServiceId, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }
        var store = PairedPublicationStore.Create(storage, options.Value, request.PublicationId!);
        var pointer = await store.ReadPointerAsync(cancellationToken).ConfigureAwait(false);
        var descriptor = pointer is null ? null : await store.ReadDescriptorAsync(pointer, cancellationToken).ConfigureAwait(false);
        var expectedSource = new PublicationSourceLayer(source.Published.Service.Metadata.Id, source.Resource.Metadata.Id);
        return !await new PairedPublicationRefreshDecision(changes).IsCurrentAsync(descriptor,
            new(expectedSource, source.MetadataRevision, source.StorageLayerId), cancellationToken).ConfigureAwait(false);
    }

    private static partial class Log
    {
        [LoggerMessage(EventId = 9255, Level = LogLevel.Warning,
            Message = "Paired publication refresh failed for {PublicationId}; retaining its last validated generation and retrying next tick.")]
        internal static partial void RefreshFailed(ILogger logger, string publicationId, Exception exception);
    }
}

internal sealed record PairedPublicationRefreshLease(ILeaseStore? Store);
