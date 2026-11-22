// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.AuditLog.Abstractions;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Infrastructure.Middleware;

namespace Honua.Server.Features.Admin;

internal sealed class HttpContextMetadataActorProvider(IHttpContextAccessor httpContextAccessor) : IMetadataActorProvider
{
    public string? GetCurrentActor()
    {
        var context = httpContextAccessor.HttpContext;
        if (context is null || context.User.Identity?.IsAuthenticated != true)
        {
            return null;
        }

        var (actor, actorType) = AuditContextResolver.ResolveCanonicalActor(context);
        return actorType == AuditActorType.Anonymous || string.IsNullOrWhiteSpace(actor)
            ? null
            : actor;
    }
}
