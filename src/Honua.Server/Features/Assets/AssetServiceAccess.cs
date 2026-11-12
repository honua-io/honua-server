// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Models;

namespace Honua.Server.Features.Assets;

// Asset and task commands are service data edits: the shared data-editor check decides, including layer-scoped write keys.
// Services are addressed by name or ID in any case; records and command receipts always store the service ID.
internal static class AssetServiceAccess
{
    public static async Task<AssetServiceGrant> RequireEditorAsync(HttpContext context, string? serviceId)
    {
        if (string.IsNullOrWhiteSpace(serviceId))
        {
            return AssetServiceGrant.Deny(AssetCommandHttp.Rejected(context, "serviceId", "serviceId is required."));
        }

        var graph = context.RequestServices.GetRequiredService<IMetadataV2GraphProvider>();
        var snapshot = await graph.GetCurrentAsync(context.RequestAborted).ConfigureAwait(false);
        if (snapshot.FindServiceByNameOrId(serviceId) is not { } service)
        {
            return AssetServiceGrant.Deny(
                ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status404NotFound, "The service was not found."));
        }

        var denied = await ServiceDataEditorAuthorization.RequireServiceDataEditorAsync(context, service, context.RequestAborted).ConfigureAwait(false);
        return denied is null ? new AssetServiceGrant(null, service) : AssetServiceGrant.Deny(denied);
    }

    public static bool Matches(MetadataV2Service service, string? serviceReference)
        => string.Equals(serviceReference, service.Metadata.Id, StringComparison.Ordinal) ||
           string.Equals(serviceReference, service.Metadata.Name, StringComparison.OrdinalIgnoreCase);
}

internal sealed record AssetServiceGrant(IResult? Denied, MetadataV2Service? Service)
{
    public string ServiceId => Service!.Metadata.Id;

    public static AssetServiceGrant Deny(IResult denied) => new(denied, null);
}
