// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Security.Domain;

namespace Honua.Geoprocessing;

// This allow-all authorizer is not registered in DI, so a missing production authorizer fails at startup instead of allowing every layer.
internal sealed class NullLayerAccessAuthorizer : ILayerAccessAuthorizer
{
    public static NullLayerAccessAuthorizer Instance { get; } = new();

    private NullLayerAccessAuthorizer()
    {
    }

    public Task<AccessDecision> AuthorizeLayerAsync(
        ClaimsPrincipal principal,
        int layerId,
        AuthorizationOperation operation,
        CancellationToken cancellationToken = default)
        => Task.FromResult(AccessDecision.Allowed());

    public Task<AccessDecision> AuthorizePublicationAsync(
        ClaimsPrincipal principal,
        Honua.Core.Features.Metadata.Domain.V2.MetadataV2Publication publication,
        Honua.Core.Features.Metadata.Domain.V2.MetadataV2Resource resource,
        Honua.Core.Features.Metadata.Domain.V2.MetadataV2Service? service,
        AuthorizationOperation operation,
        CancellationToken cancellationToken = default)
        => Task.FromResult(AccessDecision.Allowed());
}
