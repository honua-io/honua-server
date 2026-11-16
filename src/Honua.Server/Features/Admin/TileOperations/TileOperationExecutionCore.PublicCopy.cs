// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Metadata.Abstractions;
using Honua.Server.Features.Admin.PairedPublications;

namespace Honua.Server.Features.Admin.TileOperations;

internal sealed partial class TileOperationExecutionCore
{
    private static async Task CopyPublicGenerationAsync(
        PairedPublishContext context, IServiceProvider services, CancellationToken cancellationToken)
    {
        var copier = services.GetService<PublicPublicationCopier>();
        if (copier is null)
        {
            return;
        }

        var target = services.GetRequiredService<PublicPublicationCleanupTarget>();
        var guard = services.GetRequiredService<PublicPublicationSourceGuard>();
        var provider = services.GetRequiredService<IMetadataV2GraphProvider>();
        var source = context.Source;
        if (source.Published.Publication.LayerIndex is not int layerIndex)
        {
            return;
        }
        var run = context.Run;
        var identity = new PublicPublicationSourceIdentity(source.Published, source.MetadataRevision,
            layerIndex);
        if (!await guard.IsCurrentAndEligibleAsync(identity, cancellationToken).ConfigureAwait(false))
        {
            return;
        }
        var copy = new PublicPublicationCopySource
        {
            Reservation = new PublicPublicationReservation
            {
                Key = new(run.PublicationId, run.Version),
                ServiceId = source.Published.Service.EffectivePublicName,
                LayerIndex = layerIndex,
                MetadataRevision = source.MetadataRevision,
                DestinationBucket = target.Bucket,
                DestinationBaseUrl = target.BaseUrl,
                CdnDistributionId = target.DistributionId
            },
            IsCurrentAndEligibleAsync = async token =>
                await guard.IsCurrentAndEligibleAsync(identity, token).ConfigureAwait(false)
                && await source.IsUnchangedAsync(provider, token).ConfigureAwait(false),
            OpenArtifactAsync = (name, token) => context.Store.OpenArtifactAsync(run.Version, name, token)
        };
        await copier.CopyAsync(copy, cancellationToken).ConfigureAwait(false);
    }
}
