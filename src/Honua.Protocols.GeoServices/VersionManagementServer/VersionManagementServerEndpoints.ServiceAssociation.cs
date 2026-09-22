// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Models;
using Honua.Protocols.GeoServices.FeatureServer;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Protocols.GeoServices.VersionManagementServer;

public static partial class VersionManagementServerEndpoints
{
    private static readonly object CanonicalVersionServiceKey = new();

    private static MetadataV2Service RequireValidatedVersionService(HttpContext context)
        => context.Items[CanonicalVersionServiceKey] as MetadataV2Service
            ?? throw new InvalidOperationException("Version service authorization must precede branch lookup.");

    private static string RequireCanonicalVersionService(HttpContext context)
        => RequireValidatedVersionService(context).Metadata.Id;

    // Honua extension: this is never advertised as an Esri native lifecycle operation.
    private static async Task<IResult> HandleAdoptService(
        string serviceId, string versionGuid, HttpContext context,
        [FromServices] IVersionManager versionManager, CancellationToken cancellationToken)
    {
        var (gate, _) = await AuthorizeAndReadAsync(serviceId, context, versionManager, cancellationToken).ConfigureAwait(false);
        if (gate is not null)
        {
            return gate;
        }
        if (versionManager is not IVersionServiceAssociationManager associationManager)
        {
            return StandardErrorHelpers.CreateNotImplemented(context, "Legacy branch adoption is unavailable for this provider.");
        }
        if (!Guid.TryParse(versionGuid, out var versionId))
        {
            return StandardErrorHelpers.CreateBadRequest(context, "versionGuid is not a valid GUID.");
        }
        // Intentionally unscoped only for this explicit adoption operation. All normal
        // service lookups use VersionServiceScope; owners/admins cannot bypass it by GUID.
        var descriptor = await versionManager.GetVersionAsync(versionId, cancellationToken).ConfigureAwait(false);
        var isAdmin = ServiceDataEditorAuthorization.IsAdminPrincipal(context);
        if (descriptor is not { } version || !VersionAccessPolicy.IsVersionVisible(version, context.User.Identity?.Name, isAdmin))
        {
            return StandardErrorHelpers.CreateNotFound(context, $"Version '{versionGuid}' was not found.");
        }
        if (!VersionAccessPolicy.CanManageVersion(version, context.User.Identity?.Name, isAdmin))
        {
            return StandardErrorHelpers.CreateForbidden(context, AccessPolicyHelpers.AccessForbiddenMessage);
        }
        var service = RequireValidatedVersionService(context);
        var snapshot = await context.RequestServices.GetRequiredService<IMetadataV2GraphProvider>()
            .GetCurrentAsync(cancellationToken).ConfigureAwait(false);
        var (publications, resourceError) = await AccessPolicyHelpers.FilterAccessibleResourcesAsync(
            context, FeatureServerEndpoints.GetRoutableFeaturePublicationsV2(service, snapshot),
            static pair => pair.Resource, service, AuthorizationOperation.Update, cancellationToken).ConfigureAwait(false);
        if (resourceError is not null)
        {
            return resourceError;
        }
        var layers = new HashSet<int>();
        foreach (var (publication, resource) in publications)
        {
            var editorError = await ServiceDataEditorAuthorization.RequireResourceDataEditorAsync(
                context, resource, service, cancellationToken).ConfigureAwait(false);
            if (editorError is null && await FeatureServerEndpoints.IsPublicationBranchVersioningAvailableAsync(
                context, service, resource, publication, snapshot, cancellationToken).ConfigureAwait(false)
                && snapshot.ResolveStorageLayerId(publication) is { } layerId)
            {
                layers.Add(layerId);
            }
        }
        if (layers.Count == 0)
        {
            return StandardErrorHelpers.CreateForbidden(context, AccessPolicyHelpers.AccessForbiddenMessage);
        }
        VersionServiceAssociationResult result;
        try
        {
            result = await associationManager.AssociateLegacyVersionAsync(versionId, service.Metadata.Id,
                version.Owner, layers.ToArray(), cancellationToken).ConfigureAwait(false);
        }
        catch (VersionLockedException)
        {
            return StandardErrorHelpers.CreateConflict(context, "Version maintenance is in progress; retry adoption after it completes.");
        }
        return result switch
        {
            VersionServiceAssociationResult.Associated => Moment(true),
            VersionServiceAssociationResult.Missing => StandardErrorHelpers.CreateNotFound(context, $"Version '{versionGuid}' was not found."),
            _ => StandardErrorHelpers.CreateConflict(context,
                "The version cannot be adopted: its association, owner, state, or affected resources do not permit this operation.")
        };
    }
}
