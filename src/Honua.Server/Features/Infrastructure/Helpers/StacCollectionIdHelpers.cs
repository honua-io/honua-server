// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.Metadata.Domain.V2;

namespace Honua.Server.Features.Infrastructure.Helpers;

internal static class StacCollectionIdHelpers
{
    internal static int? ParseNumericCollectionId(string collectionId)
        => int.TryParse(
            collectionId.Trim(),
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out var parsed)
                ? parsed
                : null;

    internal static bool IsMetadataStacServiceEnabled(MetadataV2Service service)
        => service.ServiceType == MetadataV2ServiceType.StacApi &&
           ServiceProtocols.IsProtocolEnabled(service, ServiceProtocols.Stac);

    internal static bool MatchesMetadataStacCollectionId(
        MetadataV2Publication publication,
        string collectionId,
        int? numericId)
    {
        if (string.Equals(publication.ServiceLocalId, collectionId, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(publication.Path, collectionId, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(publication.Metadata.Name, collectionId, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(publication.Metadata.Id, collectionId, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return numericId.HasValue && publication.LayerIndex == numericId.Value;
    }
}
