// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Microsoft.AspNetCore.Routing;

namespace Honua.FileStorage;

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
