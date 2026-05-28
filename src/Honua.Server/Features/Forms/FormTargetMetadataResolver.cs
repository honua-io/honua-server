// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.Forms.Packages;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;

namespace Honua.Server.Features.Forms;

internal sealed class FormTargetMetadataResolver(IMetadataV2GraphProvider metadataGraphProvider) : IFormTargetMetadataResolver
{
    public async Task<FormTargetMetadataResolution> ResolveAsync(
        FormTargetDefinition? target,
        CancellationToken cancellationToken = default)
    {
        if (target is null || string.IsNullOrWhiteSpace(target.ServiceId))
        {
            return default;
        }

        var snapshot = await metadataGraphProvider.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
        var service = ResolveService(snapshot, target.ServiceId);
        if (service is null)
        {
            return default;
        }

        var publication = snapshot.FindPublicationByLayerIndex(service.Metadata.Id, target.LayerId)
            ?? snapshot.FindPublicationOnService(
                service.Metadata.Id,
                target.LayerId.ToString(CultureInfo.InvariantCulture));

        var resource = publication is null
            ? null
            : snapshot.ResolveResource(publication);
        var storageLayerId = publication is null
            ? null
            : snapshot.ResolveStorageLayerId(publication);

        return new FormTargetMetadataResolution(service, publication, resource, storageLayerId);
    }

    private static MetadataV2Service? ResolveService(MetadataV2GraphSnapshot snapshot, string serviceId)
    {
        if (snapshot.Index.ServicesByName.TryGetValue(serviceId, out var byName))
        {
            return byName;
        }

        if (snapshot.Index.ServicesById.TryGetValue(serviceId, out var byId))
        {
            return byId;
        }

        return null;
    }
}
