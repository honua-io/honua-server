// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Infrastructure.SharedCaching;
using Microsoft.AspNetCore.OutputCaching;

namespace Honua.Infrastructure.Caching;

// A replay skips the handler and therefore its access check. A response about a service, layer, collection or scene is
// stored only when the access pipeline granted it to an anonymous caller, so every replay stands on a grant made under
// the catalog revision that is part of its key.
internal sealed class RequireAnonymousGrantOutputCachePolicy : IOutputCachePolicy
{
    public ValueTask CacheRequestAsync(OutputCacheContext context, CancellationToken cancellationToken)
        => ValueTask.CompletedTask;

    public ValueTask ServeFromCacheAsync(OutputCacheContext context, CancellationToken cancellationToken)
        => ValueTask.CompletedTask;

    public ValueTask ServeResponseAsync(OutputCacheContext context, CancellationToken cancellationToken)
    {
        if (!MayStore(context.HttpContext))
        {
            context.AllowCacheStorage = false;
        }

        return ValueTask.CompletedTask;
    }

    internal static bool MayStore(HttpContext context)
    {
        if (ResponseAccessVerdict.Restricted(context))
        {
            return false;
        }

        return SharedCacheResponse.Subject(context) is null
               || ResponseAccessVerdict.GrantedToAnonymous(context)
               || SharedCacheResponse.DeclaredClass(context) is ResponseCacheClass.MetadataDocument;
    }
}
