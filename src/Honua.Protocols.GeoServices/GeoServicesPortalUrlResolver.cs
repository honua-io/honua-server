// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Infrastructure.Helpers;

namespace Honua.Protocols.GeoServices;

/// <summary>
/// Resolves the same public portal identity for Sharing and federated server discovery.
/// The optional SOAP instance alias is not a separate owning portal.
/// </summary>
internal static class GeoServicesPortalUrlResolver
{
    internal static string GetBaseUrl(HttpContext context)
        => BaseUrlResolver.GetBaseUrl(context).TrimEnd('/');
}
