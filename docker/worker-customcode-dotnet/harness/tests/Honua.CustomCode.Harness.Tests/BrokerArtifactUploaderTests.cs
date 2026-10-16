// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Sockets;
using FluentAssertions;
using Honua.CustomCode.Sdk;
using Xunit;

namespace Honua.CustomCode.Harness.Tests;

public sealed class BrokerArtifactUploaderTests
{
    [Fact]
    [Trait("Tier", "Fast")]
    public async Task RealHttpTransport_SendsExactBytesAndAttemptCapability()
    {
        using var socket = new TcpListener(IPAddress.Loopback, 0);
        socket.Start();
        var port = ((IPEndPoint)socket.LocalEndpoint).Port;
        socket.Stop();
        using var listener = new HttpListener();
        var origin = $"http://127.0.0.1:{port}";
        listener.Prefixes.Add(origin + "/");
        listener.Start();
        var directory = Directory.CreateTempSubdirectory("gp-broker-");
        try
        {
            var path = Path.Combine(directory.FullName, "result.json");
            var bytes = "{\"value\":7}"u8.ToArray();
            await File.WriteAllBytesAsync(path, bytes);
            var receiver = Task.Run(async () =>
            {
                var context = await listener.GetContextAsync().WaitAsync(TimeSpan.FromSeconds(20));
                context.Request.RawUrl.Should().Be("/api/geoprocessing/jobs/gp-test/custom-artifacts/result.json?attempt=2");
                context.Request.Headers["X-Api-Key"].Should().Be("job-only-capability");
                using var received = new MemoryStream();
                await context.Request.InputStream.CopyToAsync(received);
                received.ToArray().Should().Equal(bytes);
                context.Response.StatusCode = 200;
                context.Response.Close();
            });
            var uploader = ArtifactUploader.CreateBroker(origin, "job-only-capability", "gp-test", "2");
            var result = await Task.Run(() => uploader.Upload([new Artifact("result.json", path, bytes.Length)]));
            await receiver;
            result.Single().Uri.Should().Be(origin + "/api/geoprocessing/jobs/gp-test/custom-artifacts/result.json?attempt=2");
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }
}
