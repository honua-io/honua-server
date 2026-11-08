// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Models;
using AccessDecision = Honua.Core.Features.Security.Domain.AccessDecision;

namespace Honua.Infrastructure.Validation;

internal static partial class LayerValidationHelpers
{
    public static async Task<MetadataV2ValidationResult> ValidatePublicationWithAccessV2Async(
        HttpContext context,
        string publicationId,
        AuthorizationOperation operation)
    {
        var snapshot = await GetV2SnapshotAsync(context, context.RequestAborted).ConfigureAwait(false);
        if (!snapshot.Index.PublicationsById.TryGetValue(publicationId, out var publication) || !snapshot.IsRoutable(publication))
        {
            return new MetadataV2ValidationResult(false, null, null, null,
                StandardErrorHelpers.CreateNotFound(context, $"Publication '{publicationId}' not found."));
        }

        var resource = snapshot.ResolveResource(publication)!;
        var service = ResolveVisibleProtocolService(context, snapshot, publication, null);
        if (service is null)
        {
            var error = context.User.Identity?.IsAuthenticated != true
                ? AccessPolicyHelpers.CreateAccessDeniedResult(context, AccessDecision.RequiresAuth())
                : StandardErrorHelpers.CreateNotFound(context, $"Publication '{publicationId}' not found.");
            return new MetadataV2ValidationResult(false, null, null, null, error);
        }

        var accessError = await AccessPolicyHelpers.RequireResourceAccessAsync(
            context, resource, operation, service, context.RequestAborted).ConfigureAwait(false);
        if (accessError is null)
        {
            ValidatedMetadataSnapshot.Remember(context, resource, snapshot);
        }

        return new MetadataV2ValidationResult(accessError is null, publication, resource, service, accessError, snapshot);
    }

    public static async Task<MetadataV2ValidationResult> ValidateStorageLayerWithAccessV2Async(
        HttpContext context,
        int storageLayerId,
        AuthorizationOperation operation)
    {
        var snapshot = await GetV2SnapshotAsync(context, context.RequestAborted).ConfigureAwait(false);
        var resource = snapshot.ResolveStorageResource(storageLayerId,
            publication => ResolveVisibleProtocolService(context, snapshot, publication, null) is not null);
        if (resource is null)
        {
            var error = context.User.Identity?.IsAuthenticated != true && snapshot.PublicationsForStorageLayer(storageLayerId).Any()
                ? AccessPolicyHelpers.CreateAccessDeniedResult(context, AccessDecision.RequiresAuth())
                : StandardErrorHelpers.CreateNotFound(context, $"Storage layer {storageLayerId} not found.");
            return new MetadataV2ValidationResult(false, null, null, null, error);
        }

        var publication = snapshot.PublicationsForStorageLayer(storageLayerId)
            .Where(candidate => candidate.ResourceId == resource.Metadata.Id &&
                ResolveVisibleProtocolService(context, snapshot, candidate, null) is not null)
            .OrderByDescending(static candidate => candidate.IsPrimary)
            .First();
        var service = snapshot.Index.ServicesById[publication.ServiceId];
        var accessError = await AccessPolicyHelpers.RequireResourceAccessAsync(
            context, resource, operation, service, context.RequestAborted).ConfigureAwait(false);
        if (accessError is null)
        {
            ValidatedMetadataSnapshot.Remember(context, resource, snapshot);
        }

        return new MetadataV2ValidationResult(accessError is null, publication, resource, service, accessError, snapshot);
    }

}
