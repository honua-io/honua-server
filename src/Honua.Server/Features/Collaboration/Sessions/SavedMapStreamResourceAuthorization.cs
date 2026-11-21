// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Infrastructure.Authentication;

namespace Honua.Server.Features.Collaboration.Sessions;

// A collaboration stream stays open only while its caller may still join the map, so a revoked map grant ends it.
internal sealed class SavedMapStreamResourceAuthorization : ILiveStreamResourceAuthorization
{
    public static readonly SavedMapStreamResourceAuthorization Instance = new();

    private SavedMapStreamResourceAuthorization()
    {
    }

    public async Task<bool> IsAuthorizedAsync(HttpContext check, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(check);
        if (check.Request.RouteValues["mapId"] is not string mapId || string.IsNullOrWhiteSpace(mapId))
        {
            return false;
        }

        var authorizer = check.RequestServices.GetRequiredService<ISavedMapCollaborationAuthorizer>();
        var result = await authorizer.ReauthorizeAsync(check, mapId).ConfigureAwait(false);
        return result.Authorized;
    }
}
