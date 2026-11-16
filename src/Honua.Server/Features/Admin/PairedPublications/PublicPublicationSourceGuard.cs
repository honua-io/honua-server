// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Globalization;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Server.Features.Protocols.Tiles.PMTilesProxy;

namespace Honua.Server.Features.Admin.PairedPublications;

internal sealed record PublicPublicationSourceIdentity(PMTilesPublishedSource Source, long MetadataRevision, int LayerIndex);

internal sealed class PublicPublicationSourceGuard(IMetadataV2GraphStore store, PublicPublicationEligibility eligibility)
{
    public async Task<bool> IsCurrentAndEligibleAsync(PublicPublicationSourceIdentity expected, CancellationToken cancellationToken)
    {
        // Read the store, bypassing cached graph providers, while the caller holds the journal fence.
        var snapshot = await store.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
        if (snapshot.Revision != expected.MetadataRevision)
        {
            return false;
        }
        var metadata = PMTilesProxySourceResolver.StampPublishMetadata(
            ImmutableDictionary<string, string>.Empty.Add(PMTilesProxySourceResolver.LayerIdMetadataKey,
                expected.LayerIndex.ToString(CultureInfo.InvariantCulture)), expected.Source);
        var current = PMTilesProxySourceResolver.Resolve(snapshot, metadata);
        return PMTilesProxySourceResolver.SameSource(expected.Source, current)
            && await eligibility.IsEligibleAsync(snapshot, current!, cancellationToken).ConfigureAwait(false);
    }
}
