// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Server.Features.Collaboration.Sessions;

namespace Honua.Server.Features.Collaboration;

internal static class CollaborationEndpoints
{
    public static void MapCollaborationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapCollaborationSessionEndpoints();
    }
}
