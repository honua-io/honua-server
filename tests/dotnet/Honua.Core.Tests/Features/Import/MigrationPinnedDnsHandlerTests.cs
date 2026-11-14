// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Net.Sockets;
using FluentAssertions;
using Honua.Core.Features.Migration.Services;
using Honua.TestKit.Attributes;

namespace Honua.Core.Tests.Features.Import;

public sealed class MigrationPinnedDnsHandlerTests
{
    private static readonly MigrationServiceUrlPolicy Policy = new(
        "http or https required",
        "https required",
        "no embedded credentials",
        "disallowed address",
        "connection failed");

    [UnitTest]
    public void CreateHandler_DisablesRedirectsAndAppliesTheClientConnectionLimit()
    {
        var client = new MigrationPinnedDnsClient(Policy, new AsyncLocal<bool>(), MaxConnectionsPerServer: 7);

        using var handler = MigrationPinnedDns.CreateHandler(client, hostAddressResolver: null);

        handler.AllowAutoRedirect.Should().BeFalse();
        handler.MaxConnectionsPerServer.Should().Be(7);
        handler.ConnectCallback.Should().NotBeNull();
    }

    [UnitTest]
    public async Task CreateHandler_ReadsTheUnsafeLocalSwitchForEachConnection()
    {
        var unsafeLocalUrlsAllowed = new AsyncLocal<bool>();
        var client = new MigrationPinnedDnsClient(Policy, unsafeLocalUrlsAllowed, MaxConnectionsPerServer: 1);
        using var handler = MigrationPinnedDns.CreateHandler(
            client, (_, _) => Task.FromResult(new[] { IPAddress.Loopback }));
        using var httpClient = new HttpClient(handler);
        var closedLoopbackPort = ReserveClosedLoopbackPort();
        var request = $"http://migration-source.test:{closedLoopbackPort}/";

        var refused = await Assert.ThrowsAsync<HttpRequestException>(() => httpClient.GetAsync(request));
        MessagesOf(refused).Should().Contain(Policy.DisallowedAddressMessage);

        unsafeLocalUrlsAllowed.Value = true;
        var attempted = await Assert.ThrowsAsync<HttpRequestException>(() => httpClient.GetAsync(request));
        MessagesOf(attempted).Should().Contain(Policy.ConnectionFailureMessage)
            .And.NotContain(Policy.DisallowedAddressMessage);
    }

    private static int ReserveClosedLoopbackPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static List<string> MessagesOf(Exception exception)
    {
        var messages = new List<string>();
        for (var current = exception; current is not null; current = current.InnerException)
        {
            messages.Add(current.Message);
        }

        return messages;
    }
}
