// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Metadata.Domain.V2;

namespace Honua.Infrastructure.Helpers;

internal static class GeoServicesServiceUrls
{
    public static string CanonicalPath(MetadataV2Graph graph, MetadataV2Service service)
        => $"/rest/services/{EscapeName(MetadataV2ServicePaths.QualifiedName(graph, service))}";

    public static string Canonical(HttpContext context, MetadataV2Graph graph, MetadataV2Service service, string serviceType)
        => $"{BaseUrlResolver.GetBaseUrl(context)}{CanonicalPath(graph, service)}/{serviceType}";

    public static string Requested(HttpContext context, string fallbackServiceName, string serviceType)
        => BaseUrlResolver.GetBaseUrl(context) + RequestedPath(context, fallbackServiceName, serviceType);

    public static string RequestedLocation(HttpContext context, string fallbackServiceName, string serviceType)
        => (BaseUrlResolver.TryGetConfiguredBaseUrl(context, out var configuredBase) ? configuredBase : context.Request.PathBase.Value)
            + RequestedPath(context, fallbackServiceName, serviceType);

    private static string RequestedPath(HttpContext context, string fallbackServiceName, string serviceType)
    {
        var path = GeoServicesServicePath.FromRequest(context);
        return $"{path?.Root ?? "/rest/services"}/{EscapeName(path?.QualifiedName ?? fallbackServiceName)}/{serviceType}";
    }

    public static string EscapeName(string qualifiedName)
        => string.Join('/', qualifiedName.Split('/').Select(Uri.EscapeDataString));
}
