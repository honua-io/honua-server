// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Validation.Abstractions;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Helpers;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Protocols.GeoServices.MapServer;

internal static partial class MapServerEndpoints
{
    private sealed record MapServerPublicationSelection(
        MetadataV2Service Service, MetadataV2GraphSnapshot Snapshot, MapServerMetadataLayerDescriptor[] Layers);

    private static async Task<(MapServerPublicationSelection? Selection, IResult? Error)> ResolveMapServerPublicationSelectionAsync(
        HttpContext context, string serviceId, CancellationToken cancellationToken)
    {
        var validator = context.RequestServices.GetRequiredService<IResourceValidator>();
        var result = await ServiceResourceValidationHelpers.ValidateServiceV2Async(
            validator, serviceId, ServiceProtocols.MapServer, context,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!result.IsValid)
        {
            return (null, result.ErrorResult!);
        }
        var provider = context.RequestServices.GetRequiredService<IMetadataV2GraphProvider>();
        var snapshot = await provider.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
        var service = result.Service!;
        return (new(service, snapshot, ResolveMapServerMetadataLayers(snapshot, service, context)), null);
    }

    private static async Task<(ResourceAccessSet Access, IResult? Error)> AuthorizeMapServerExportAsync(
        HttpContext context, MapServerPublicationSelection selection, CancellationToken cancellationToken)
    {
        var resources = selection.Layers.Select(static layer => layer.Resource).ToArray();
        var access = await AccessPolicyHelpers.EvaluateResourceAccessSetAsync(
            context, resources, selection.Service, AuthorizationOperation.Export, cancellationToken).ConfigureAwait(false);
        return (access, access.RequireAny(resources));
    }
}
