// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Metadata.Domain.V2;

namespace Honua.Infrastructure.Helpers;

internal static class GeoServicesServiceUrls
{
    public static string CanonicalPath(MetadataV2Graph graph, MetadataV2Service service)
        => $"/rest/services/{EscapeName(MetadataV2ServicePaths.QualifiedName(graph, service))}";

    public static string CanonicalLocation(HttpContext context, MetadataV2Graph graph, MetadataV2Service service, string serviceType)
        => $"{LocationBase(context)}{CanonicalPath(graph, service)}/{serviceType}";

    public static string Requested(HttpContext context, string fallbackServiceName, string serviceType)
        => BaseUrlResolver.GetBaseUrl(context) + RequestedPath(context, fallbackServiceName, serviceType);

    public static string RequestedLocation(HttpContext context, string fallbackServiceName, string serviceType)
        => LocationBase(context) + RequestedPath(context, fallbackServiceName, serviceType);

    private static string? LocationBase(HttpContext context)
        => BaseUrlResolver.TryGetConfiguredBaseUrl(context, out var configuredBase) ? configuredBase : context.Request.PathBase.Value;

    public static string RequestedProtocol(HttpContext context, string protocol)
    {
        if (GeoServicesServicePath.FromRequest(context) is { } servicePath &&
            context.GetEndpoint()?.Metadata.GetMetadata<GeoServicesServiceRoutes.ServiceRoute>() is { } route)
        {
            return Requested(context, servicePath.PublicName, route.ServiceType) + '/' + protocol;
        }
        var path = context.Request.Path.Value ?? string.Empty;
        var protocolOffset = path.LastIndexOf('/' + protocol, StringComparison.OrdinalIgnoreCase);
        var endpointPath = protocolOffset < 0 ? path.TrimEnd('/') : path[..(protocolOffset + protocol.Length + 1)];
        return BaseUrlResolver.GetBaseUrl(context) + EscapeName(endpointPath);
    }

    private static string RequestedPath(HttpContext context, string fallbackServiceName, string serviceType)
    {
        var path = GeoServicesServicePath.FromRequest(context);
        return $"{path?.Root ?? "/rest/services"}/{EscapeName(path?.QualifiedName ?? fallbackServiceName)}/{serviceType}";
    }

    public static string EscapeName(string qualifiedName)
        => string.Join('/', qualifiedName.Split('/').Select(Uri.EscapeDataString));
}
