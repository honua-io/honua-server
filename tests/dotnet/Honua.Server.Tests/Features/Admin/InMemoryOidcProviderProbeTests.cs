// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text;
using Honua.Core.Features.Identity.Domain;
using Honua.Server.Features.Admin;
using Honua.Server.Features.Admin.Models;
using Honua.Server.Features.Admin.Services;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Server.Tests.Features.Admin;

/// <summary>
/// The in-memory provider store probes discovery and publishes enabled providers into
/// the authentication catalog. A missing probe is not reported as success.
/// </summary>
[SecurityTest]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.Security)]
public sealed class InMemoryOidcProviderProbeTests
{
    [UnitTest]
    public async Task TestProvider_WithoutFactory_IsNotReachable()
    {
        var store = new InMemoryOidcProviderStore();
        var created = await store.CreateProviderAsync(Provider(), CancellationToken.None);

        var result = await store.TestProviderAsync(created.ProviderId, CancellationToken.None);

        Assert.False(result.IsReachable);
    }

    [UnitTest]
    public async Task TestProvider_ProbesDiscovery_AndPublishesIntoTheCatalog()
    {
        var handler = new CountingHandler(HttpStatusCode.OK);
        var registry = new OidcRuntimeProviderRegistry();
        try
        {
            var store = new InMemoryOidcProviderStore(new StubFactory(handler), registry);
            var created = await store.CreateProviderAsync(Provider(), CancellationToken.None);

            var reachable = await store.TestProviderAsync(created.ProviderId, CancellationToken.None);
            var providers = OidcProviderCatalog.GetProviders(new OidcAuthenticationOptions(), registry);

            Assert.True(reachable.IsReachable, reachable.Message);
            Assert.Equal(1, handler.Calls);
            Assert.Contains("/.well-known/openid-configuration", handler.LastUri!.AbsolutePath, StringComparison.Ordinal);
            Assert.Contains(providers, provider => provider.ClientId == "client-1" && provider.Enabled);

            handler.Status = HttpStatusCode.InternalServerError;
            var failed = await store.TestProviderAsync(created.ProviderId, CancellationToken.None);
            Assert.False(failed.IsReachable);
            Assert.Equal(2, handler.Calls);
        }
        finally
        {
            OidcProviderCatalog.Attach(null);
        }
    }

    [UnitTest]
    public async Task Update_RejectsAnInvalidAuthority()
    {
        var store = new InMemoryOidcProviderStore();
        var created = await store.CreateProviderAsync(Provider(), CancellationToken.None);
        var result = await OidcProviderEndpoints.HandleUpdateProvider(
            created.ProviderId,
            new UpdateOidcProviderRequest { Authority = "not a url" },
            store,
            NullLogger<OidcProviderEndpoints.OidcProviderEndpointsLog>.Instance,
            new DefaultHttpContext());

        var status = Assert.IsAssignableFrom<IStatusCodeHttpResult>(result.Result);
        Assert.Equal(StatusCodes.Status400BadRequest, status.StatusCode);
        var stored = await store.GetProviderAsync(created.ProviderId, CancellationToken.None);
        Assert.Equal("https://example.com", stored!.Authority);
    }

    private static OidcProviderConfiguration Provider() => new()
    {
        Name = "Example",
        ProviderType = "Generic",
        Authority = "https://example.com",
        ClientId = "client-1",
        Enabled = true,
    };

    private sealed class StubFactory(CountingHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class CountingHandler(HttpStatusCode status) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        public Uri? LastUri { get; private set; }

        public HttpStatusCode Status { get; set; } = status;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            LastUri = request.RequestUri;
            var response = new HttpResponseMessage(Status)
            {
                Content = new StringContent("{\"issuer\":\"https://example.com\"}", Encoding.UTF8, "application/json"),
            };
            return Task.FromResult(response);
        }
    }
}
