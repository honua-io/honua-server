// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Core.Features.Assets;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Forms.Packages;

namespace Honua.Server.Features.Tasks;

internal sealed class WorkTaskAssetResolver(
    IAssetProfileStore profiles,
    IFormTargetMetadataResolver layerResolver,
    WorkTaskAssetReader reader)
{
    public async Task<WorkTaskAsset> ResolveAsync(WorkTaskAssetReference reference, ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        var profile = await profiles.GetAsync(reference.ProfileId, cancellationToken).ConfigureAwait(false);
        if (profile is null || !string.Equals(profile.ServiceId, reference.ServiceId, StringComparison.Ordinal))
        {
            return WorkTaskAsset.Fail("profileId", "The asset profile was not found on this service.");
        }

        if (profile.State != AssetProfileState.Active)
        {
            return WorkTaskAsset.Fail("profileId", "Tasks can only target assets of an active profile.");
        }

        var target = await layerResolver.ResolveAsync(
            new FormTargetDefinition { ServiceId = profile.ServiceId, LayerId = profile.LayerId }, cancellationToken).ConfigureAwait(false);
        if (target is not { Snapshot: { }, Service: { }, Publication: { }, Resource: { }, StorageLayerId: int })
        {
            return WorkTaskAsset.Fail("profileId", "The profiled layer is no longer published.");
        }

        return await reader.ResolveAsync((target, profile, reference.AssetId, reference.Operation), user, cancellationToken).ConfigureAwait(false);
    }

    public Task<bool> AuthorizeUpdateAsync(WorkTaskAsset asset, ClaimsPrincipal user, CancellationToken cancellationToken)
        => reader.AuthorizeUpdateAsync(asset, user, cancellationToken);

}

internal sealed record WorkTaskAssetReference(Guid ProfileId, string ServiceId, string AssetId)
{
    public AuthorizationOperation Operation { get; init; } = AuthorizationOperation.Query;
}

internal sealed record WorkTaskAsset
{
    public AssetProfile? Profile { get; init; }
    public FormTargetMetadataResolution? Target { get; init; }
    public Feature? Feature { get; init; }
    public string? CanonicalId { get; init; }
    public AssetFieldError? Error { get; init; }

    public static WorkTaskAsset Fail(string field, string message) => new() { Error = new AssetFieldError(field, message) };
}
