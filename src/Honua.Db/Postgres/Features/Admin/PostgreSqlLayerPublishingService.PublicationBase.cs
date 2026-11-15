// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Admin.Abstractions;

namespace Honua.Db.Postgres.Features.Admin;

internal sealed partial class PostgreSqlLayerPublishingService : ILayerPublicationBaseReader
{
    // Same source as the publication check: the persisted graph, or the bootstrap snapshot
    // a first publication saves against.
    public async Task<LayerPublicationBase> ReadPublicationBaseAsync(CancellationToken cancellationToken = default)
    {
        var (graph, etag) = await LoadCurrentOrEmptyGraphAsync(cancellationToken).ConfigureAwait(false);
        if (etag is null)
        {
            var bootstrap = await _metadataGraphStore.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
            return new LayerPublicationBase(bootstrap.Graph.Revision, bootstrap.Etag);
        }

        return new LayerPublicationBase(graph.Revision, etag);
    }
}
