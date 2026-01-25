// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Admin.Services;

internal static class AdminApiUrlResolver
{
    public static string Resolve(string? configuredBaseUrl, string hostBaseAddress)
    {
        var hostBase = new Uri(hostBaseAddress, UriKind.Absolute);

        if (string.IsNullOrWhiteSpace(configuredBaseUrl))
        {
            return new Uri(hostBase, "/api/v1/admin/").ToString();
        }

        if (Uri.TryCreate(configuredBaseUrl, UriKind.Absolute, out var absolute))
        {
            return absolute.ToString();
        }

        return new Uri(hostBase, configuredBaseUrl).ToString();
    }
}
