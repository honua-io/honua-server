// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Microsoft.Extensions.Primitives;
using Microsoft.Net.Http.Headers;

namespace Honua.Infrastructure.Authentication;

internal static class RequestCredentialCatalog
{
    private static readonly string[] CredentialHeaders =
    [
        HeaderNames.Authorization, "X-API-Key", "X-Esri-Authorization", "X-Honua-Embed-Key", "X-Honua-Token",
    ];
    private static readonly string[] CredentialQueryParameters = ["token", "key"];

    internal static bool HasExplicitCredential(HttpRequest request)
        => HasCredential(request, static _ => true);

    internal static bool HasNonEmptyCredential(HttpRequest request)
        => HasCredential(request, static value => !StringValues.IsNullOrEmpty(value));

    private static bool HasCredential(HttpRequest request, Func<StringValues, bool> accept)
    {
        foreach (var header in CredentialHeaders)
        {
            if (request.Headers.TryGetValue(header, out var value)
                && accept(value)) return true;
        }
        foreach (var parameter in CredentialQueryParameters)
        {
            if (request.Query.TryGetValue(parameter, out var value)
                && accept(value)) return true;
        }
        return false;
    }
}
