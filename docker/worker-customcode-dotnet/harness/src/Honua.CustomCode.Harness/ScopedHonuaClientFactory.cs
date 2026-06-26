// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net.Http.Headers;

namespace Honua.CustomCode.Harness;

/// <summary>
/// Builds the SCOPED Honua client the tool sees as <see cref="Sdk.GpContext.Client"/>.
/// </summary>
/// <remarks>
/// <para>
/// The production wiring is the .NET mirror of the Python harness's
/// <c>HonuaClient(base_url, StaticAuthProvider({"Authorization": f"Bearer {token}"}))</c>:
/// register the Honua SDK clients into a DI container with
/// <c>AddHonua(o =&gt; { o.BaseAddress = baseUrl; o.BearerTokenProvider = _ =&gt;
/// Task.FromResult&lt;string?&gt;(token); })</c> so every request carries the scoped,
/// job-bound bearer token and nothing else.
/// </para>
/// <para>
/// The real <c>AddHonua</c> wiring is compiled in behind the <c>HONUA_SDK</c> constant
/// (set in the Docker build, where the SDK package restores from the warm NuGet cache).
/// In the offline harness/test build the SDK package is absent, so this factory returns
/// a least-privilege fallback client: a bare <see cref="HttpClient"/> with the scoped
/// bearer header pre-set and the base address pinned. Either way the harness only ever
/// hands the tool a client that carries the scoped token — never the raw token string —
/// and the strip step removes the token env right after this runs.
/// </para>
/// </remarks>
public static class ScopedHonuaClientFactory
{
    /// <summary>Create the scoped client for <paramref name="baseUrl"/> + <paramref name="jobToken"/>.</summary>
    /// <param name="baseUrl">The Honua API base URL.</param>
    /// <param name="jobToken">The scoped, job-bound bearer token.</param>
    /// <returns>The scoped client (opaque to the harness; the tool casts it).</returns>
    public static object Create(string baseUrl, string jobToken)
    {
#if HONUA_SDK
        return CreateWithSdk(baseUrl, jobToken);
#else
        return CreateFallback(baseUrl, jobToken);
#endif
    }

#if HONUA_SDK
    private static object CreateWithSdk(string baseUrl, string jobToken)
    {
        // The image restores Honua.Sdk into the warm NuGet cache; AddHonua wires every
        // enabled client over an HttpClient whose Authorization header is set from the
        // scoped BearerTokenProvider before each request.
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        Honua.Sdk.Extensions.ServiceCollectionExtensions.AddHonua(services, o =>
        {
            o.BaseAddress = new Uri(baseUrl, UriKind.Absolute);
            o.BearerTokenProvider = _ => Task.FromResult<string?>(jobToken);
        });
        return services
            .Microsoft.Extensions.DependencyInjection.ServiceCollectionContainerBuilderExtensions
            .BuildServiceProvider();
    }
#endif

    private static object CreateFallback(string baseUrl, string jobToken)
    {
        var http = new HttpClient
        {
            BaseAddress = new Uri(baseUrl, UriKind.Absolute),
        };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", jobToken);
        return http;
    }
}
