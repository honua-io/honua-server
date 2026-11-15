// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using Honua.Core.Features.Infrastructure.Validation;
using Honua.TestKit.Attributes;

namespace Honua.Core.Tests.Features.Infrastructure.Validation;

public sealed class PinnedDnsConnectorTests
{
    [UnitTest]
    public void CreateHandler_DefaultConfiguration_DisablesProxyRouting()
    {
        var connector = new PinnedDnsConnector(
            (_, _) => Task.FromResult(Array.Empty<IPAddress>()),
            new("Destination is unavailable.", "Connection failed."));

        using var handler = connector.CreateHandler(4);

        Assert.False(handler.UseProxy);
        Assert.False(handler.AllowAutoRedirect);
        Assert.Equal(4, handler.MaxConnectionsPerServer);
    }

    [UnitTest]
    public async Task CreateHandler_WithConfiguredProxy_ValidatesDestinationDirectly()
    {
        var resolvedHosts = new List<string>();
        var connector = new PinnedDnsConnector(
            (host, _) =>
            {
                resolvedHosts.Add(host);
                return Task.FromResult(new[] { IPAddress.Loopback });
            },
            new("Destination is unavailable.", "Connection failed."));
        using var handler = connector.CreateHandler(4);
        handler.Proxy = new UnexpectedProxy();
        using var client = new HttpClient(handler, disposeHandler: false);

        var exception = await Assert.ThrowsAsync<HttpRequestException>(
            () => client.GetAsync("https://destination.example/"));

        Assert.Equal(new[] { "destination.example" }, resolvedHosts);
        Assert.Contains("Destination is unavailable.", exception.ToString(), StringComparison.Ordinal);
    }

    private sealed class UnexpectedProxy : IWebProxy
    {
        public ICredentials? Credentials { get; set; }

        public Uri GetProxy(Uri destination)
            => throw new InvalidOperationException("Proxy routing was attempted.");

        public bool IsBypassed(Uri host)
            => throw new InvalidOperationException("Proxy routing was attempted.");
    }
}
