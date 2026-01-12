// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Microsoft.AspNetCore.Routing;

namespace Honua.Server.Features.FileStorage;

/// <summary>
/// Placeholder for FileStorage feature endpoints.
/// </summary>
internal static class FileStorageEndpoints
{
    public static IEndpointRouteBuilder MapFileStorageEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        return endpoints;
    }
}
