// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Admin.Domain;
using Honua.Core.Features.Metadata.Domain.V2;

namespace Honua.Db.Postgres.Features.Admin;

internal sealed partial class PostgreSqlLayerPublishingService
{
    private static void ValidatePublicationRevision(MetadataV2Graph graph, string? etag, LayerPublishRequest request)
    {
        if (request.ExpectedMetadataRevision is { } expected &&
            (graph.Revision != expected || string.IsNullOrEmpty(request.ExpectedMetadataEtag) ||
             !string.Equals(etag, request.ExpectedMetadataEtag, StringComparison.Ordinal)))
        {
            throw new LayerPublishingException(
                LayerPublishingErrorKind.Conflict,
                "Publication metadata changed after preview. Create a new preview before publishing.");
        }
    }
}
