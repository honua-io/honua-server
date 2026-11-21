// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Honua.Infrastructure.Authentication;

// One place builds the API-key and Basic challenges a 401 offers, so the authentication handler and
// every access-denial response list the same schemes. Basic is offered only on a transport where the
// handler accepts Basic credentials.
internal static class AuthenticationChallenge
{
    internal const string Realm = "Honua Admin";
    internal const string ApiKeyHeader = "X-API-Key";
    internal const string ApiKeyChallenge = $"ApiKey realm=\"{Realm}\", header=\"{ApiKeyHeader}\"";
    internal const string BasicChallenge = $"Basic realm=\"{Realm}\", charset=\"UTF-8\"";

    internal static void AppendCredentialChallenges(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var options = context.RequestServices?.GetService<IOptions<ApiKeyAuthenticationOptions>>()?.Value;
        AppendCredentialChallenges(context, options);
    }

    internal static void AppendCredentialChallenges(HttpContext context, ApiKeyAuthenticationOptions? options)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.Response.Headers.Append("WWW-Authenticate", ApiKeyChallenge);
        if (OffersBasic(context.Request, options))
        {
            context.Response.Headers.Append("WWW-Authenticate", BasicChallenge);
        }
    }

    // Request.IsHttps reflects direct TLS and trusted forwarded HTTPS once ForwardedHeaders has run,
    // the same transport test the handler applies before it accepts Basic credentials.
    internal static bool OffersBasic(HttpRequest request, ApiKeyAuthenticationOptions? options) =>
        options is { EnableBasicAuthCompatibility: true }
        && (!options.RequireHttpsForBasicAuth || request.IsHttps);
}
