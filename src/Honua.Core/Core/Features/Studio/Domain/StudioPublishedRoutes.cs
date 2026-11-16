// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Studio.Domain;

public static class StudioPublishedRoutes
{
    public const string BasePath = "/api/v1/studio/published";

    public static string BuildActiveUrl(string route)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(route);
        var key = route[0] == '/' ? route : "/" + route;
        return BasePath + string.Join('/', key.Split('/').Select(Uri.EscapeDataString));
    }

    public static string ToRouteKey(string? path) => "/" + (path ?? string.Empty);
}
