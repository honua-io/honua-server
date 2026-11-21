// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Server.Features.Admin.Services;

internal static partial class ManagedImportConnection
{
    internal static async Task<Guid?> ResolveOwnerAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        var work = await PrepareAsync(services, cancellationToken).ConfigureAwait(false);
        if (work is null)
        {
            return null;
        }

        var builtin = await work.Registry.GetConnectionAsync(Id, cancellationToken).ConfigureAwait(false);
        if (builtin is not null && await StoresAsync(work, builtin).ConfigureAwait(false))
        {
            return Id;
        }

        foreach (var name in new[] { Name, FallbackName })
        {
            var named = await work.Registry.GetConnectionByNameAsync(name, cancellationToken).ConfigureAwait(false);
            if (named is not null && await StoresAsync(work, named).ConfigureAwait(false))
            {
                return named.ConnectionId;
            }
        }

        return null;
    }
}
