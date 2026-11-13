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
            return new(false, null, null, null,
                StandardErrorHelpers.CreateNotFound(context, $"Publication '{publicationId}' not found."));
        }
        var resource = snapshot.ResolveResource(publication)!;
        var service = ResolveVisibleProtocolService(context, snapshot, publication, null);
        if (service is null)
        {
            return new(false, null, null, null,
                ChallengeTenantHiddenPublication(context, publication)
                    ?? StandardErrorHelpers.CreateNotFound(context, $"Publication '{publicationId}' not found."));
        }
        return await AuthorizeResolvedResourceAsync(
            new(context, operation, context.RequestAborted),
            new(true, publication, resource, service, null, snapshot)).ConfigureAwait(false);
    }

    public static async Task<MetadataV2ValidationResult> ValidateStorageLayerWithAccessV2Async(
        HttpContext context,
        int storageLayerId,
        AuthorizationOperation operation)
    {
        var snapshot = await GetV2SnapshotAsync(context, context.RequestAborted).ConfigureAwait(false);
        var target = snapshot.ResolveStorageResource(storageLayerId,
            publication => ResolveVisibleProtocolService(context, snapshot, publication, null) is not null);
        if (target.IsAmbiguous)
        {
            return new(false, null, null, null,
                StandardErrorHelpers.CreateConflict(context, target.ConflictDetail!));
        }
        if (target.Resource is not null)
        {
            var collections = await ResolveStorageCollectionsWithAccessV2Async(snapshot,
                new StorageCollectionAccess(new(context, operation, context.RequestAborted))
                {
                    StorageLayerId = storageLayerId,
                    ResourceId = target.Resource.Metadata.Id,
                }).ConfigureAwait(false);
            if (collections.TryGetValue(storageLayerId, out var candidate))
            {
                return candidate;
            }
        }
        return new(false, null, null, null,
            ChallengeTenantHiddenPublication(context, snapshot.PublicationsForStorageLayer(storageLayerId).FirstOrDefault())
                ?? StandardErrorHelpers.CreateNotFound(context, $"Storage layer {storageLayerId} not found."));
    }

    private static IResult? ChallengeTenantHiddenPublication(HttpContext context, MetadataV2Publication? publication)
        => context.User.Identity?.IsAuthenticated != true && publication is not null
            ? AccessPolicyHelpers.CreateAccessDeniedResult(context, AccessDecision.RequiresAuth())
            : null;

    private static async Task<MetadataV2ValidationResult> AuthorizeResolvedResourceAsync(
        ResourceAccessContext access,
        MetadataV2ValidationResult candidate)
    {
        var resource = candidate.Resource ?? throw new InvalidOperationException("A resolved resource is required.");
        var accessError = await AccessPolicyHelpers.RequireResourceAccessAsync(
            access.Context, resource, access.Operation, candidate.Service, access.CancellationToken).ConfigureAwait(false);
        if (accessError is null)
        {
            ValidatedMetadataSnapshot.Remember(access.Context, resource, candidate.Snapshot!);
        }
        return candidate with { IsValid = accessError is null, ErrorResult = accessError };
    }

    private readonly record struct ResourceAccessContext(
        HttpContext Context,
        AuthorizationOperation Operation,
        CancellationToken CancellationToken);

    private sealed record StorageCollectionAccess(ResourceAccessContext Access)
    {
        public string? RequiredProtocol { get; init; }
        public string? ResourceId { get; init; }
        public int? StorageLayerId { get; init; }
    }
}
