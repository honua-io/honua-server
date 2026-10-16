// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Sockets;
using System.Text;
using FluentAssertions;
using Honua.CustomCode.Harness;
using Honua.Sdk.OgcFeatures;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Honua.CustomCode.Harness.Tests;

public sealed class ScopedSdkClientTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProductionFactory_TypedSdkRequest_SendsJobAndBearerHeaders(bool startupDenial)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var received = ReceiveAsync(listener, timeout.Token, startupDenial);
        using var provider = ScopedHonuaClientFactory.Create($"http://127.0.0.1:{port}", "capability", "job-typed-sdk");
        var sdk = provider.GetRequiredService<IHonuaOgcFeaturesClient>();
        await sdk.GetItemsAsync("features", cancellationToken: timeout.Token);
        var request = await received;
        request.Should().Contain("Authorization: Bearer capability");
        request.Should().Contain("X-Honua-Job-Id: job-typed-sdk");
        request.Should().Contain("/ogc/features/collections/features/items");
    }

    [Fact]
    public void ProductionFactory_MissingJobContext_IsRejected()
    {
        var create = () => CredentialSandbox.BuildScopedClient("https://example.test", "capability");
        create.Should().Throw<ArgumentException>().WithMessage("*jobId*");
    }

    private static async Task<string> ReceiveAsync(TcpListener listener, CancellationToken cancellationToken, bool startupDenial)
    {
        if (startupDenial)
        {
            using var denied = await listener.AcceptTcpClientAsync(cancellationToken);
            await using var deniedStream = denied.GetStream();
            using var deniedReader = new StreamReader(deniedStream, Encoding.ASCII, leaveOpen: true);
            while (await deniedReader.ReadLineAsync(cancellationToken) is { Length: > 0 })
            {
            }
            await deniedStream.WriteAsync("HTTP/1.1 401 Unauthorized\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"u8.ToArray(), cancellationToken);
        }
        using var connection = await listener.AcceptTcpClientAsync(cancellationToken);
        await using var stream = connection.GetStream();
        using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
        var request = new StringBuilder();
        while (await reader.ReadLineAsync(cancellationToken) is { Length: > 0 } line)
        {
            request.AppendLine(line);
        }

        const string payload = "{\"type\":\"FeatureCollection\",\"features\":[]}";
        var response = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: application/geo+json\r\nContent-Length: {payload.Length}\r\nConnection: close\r\n\r\n{payload}");
        await stream.WriteAsync(response, cancellationToken);
        return request.ToString();
    }
}
