// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Primitives;

namespace Honua.Server.Features.CloudDemo;

internal static class CloudDemoCredentials
{
    public static bool TokenMatches(HttpRequest request, string headerName, string expectedToken)
    {
        if (request.Headers.TryGetValue(headerName, out var headerValues) &&
            ValuesContainToken(headerValues, expectedToken))
        {
            return true;
        }

        if (request.Headers.TryGetValue("Authorization", out var authorizationValues))
        {
            foreach (var authorization in authorizationValues)
            {
                const string bearerPrefix = "Bearer ";
                if (authorization is not null &&
                    authorization.StartsWith(bearerPrefix, StringComparison.OrdinalIgnoreCase) &&
                    FixedTimeEquals(authorization[bearerPrefix.Length..].Trim(), expectedToken))
                {
                    return true;
                }
            }
        }

        return false;
    }

    public static bool HasPresentedToken(HttpRequest request, string headerName)
    {
        if (request.Headers.TryGetValue(headerName, out var headerValues) &&
            !StringValues.IsNullOrEmpty(headerValues))
        {
            return true;
        }

        if (!request.Headers.TryGetValue("Authorization", out var authorizationValues))
        {
            return false;
        }

        foreach (var authorization in authorizationValues)
        {
            if (authorization is not null &&
                authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ValuesContainToken(StringValues values, string expectedToken)
    {
        foreach (var value in values)
        {
            if (FixedTimeEquals(value?.Trim(), expectedToken))
            {
                return true;
            }
        }

        return false;
    }

    private static bool FixedTimeEquals(string? providedToken, string expectedToken)
    {
        if (string.IsNullOrEmpty(providedToken) ||
            string.IsNullOrEmpty(expectedToken))
        {
            return false;
        }

        var providedBytes = Encoding.UTF8.GetBytes(providedToken);
        var expectedBytes = Encoding.UTF8.GetBytes(expectedToken);
        return providedBytes.Length == expectedBytes.Length &&
            CryptographicOperations.FixedTimeEquals(providedBytes, expectedBytes);
    }
}
