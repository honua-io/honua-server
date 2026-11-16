// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Admin.Domain;
using Honua.Core.Features.Metadata.Domain.V2;

namespace Honua.Db.Postgres.Features.Admin;

internal sealed partial class PostgreSqlLayerPublishingService
{
    private static MetadataV2PairedPublication? ValidatePairedPublication(
        MetadataV2PairedPublication? recipe, PairedPublicationContext context, PublishedLayerStorage storage)
    {
        var (resource, serviceId, graph) = context;
        if (recipe is null)
        {
            return null;
        }
        if (!storage.IsManagedStore)
        {
            throw new LayerPublishingException(LayerPublishingErrorKind.Validation,
                "Paired publication requires managed storage.");
        }
        if (MetadataV2PairedPublicationRules.Problem(recipe, resource) is { } problem)
        {
            throw new LayerPublishingException(LayerPublishingErrorKind.Validation,
                problem);
        }
        if (graph.Publications.Any(publication => (publication.ResourceId != resource.Metadata.Id || publication.ServiceId != serviceId)
            && publication.PairedPublication?.PublicationId == recipe.PublicationId))
        {
            throw new LayerPublishingException(LayerPublishingErrorKind.Validation,
                "The paired publication id is already assigned to another resource.");
        }
        return recipe;
    }
    private sealed record PairedPublicationContext(MetadataV2Object Resource, string ServiceId, MetadataV2Graph Graph);
}
