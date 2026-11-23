// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Ai.Protocols.Mcp.Tools;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Features.Protocols.Mcp;

[Collection("Database")]
[Protocol(TestProtocols.Mcp)]
[Operation(Operations.Security)]
public sealed partial class McpHttpRequestBoundsTests(McpHttpRequestBoundsTests.BoundsHost host)
    : IClassFixture<McpHttpRequestBoundsTests.BoundsHost>
{
    private const int BodyLimit = 512;
    private const string ListRequest = """{"jsonrpc":"2.0","id":1,"method":"tools/list","params":{}}""";
    private readonly HttpClient _client = host.Fixture.Client;

    public sealed class BoundsHost : IAsyncLifetime
    {
        internal DiscoveryCounter Counter { get; } = new();
        internal WebAppFixture Fixture { get; }

        public BoundsHost()
        {
            Fixture = new WebAppFixture().UseSeed("tests/seed/server.yaml")
                .ConfigureWebHost(builder =>
                {
                    builder.UseEnvironment("Test");
                    builder.UseSetting("HONUA_DEV_AUTH", "false");
                    builder.UseSetting("HONUA_ADMIN_PASSWORD", WebAppFixture.SharedAdminPassword);
                    builder.UseSetting("Mcp:MaxRequestBodyBytes", BodyLimit.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    builder.UseSetting("Mcp:MaxBatchSize", "3");
                    builder.UseSetting("Mcp:MaxAnonymousBatchSize", "2");
                    builder.ConfigureServices(services => services.AddSingleton<IMcpToolSource>(Counter));
                });
        }

        public Task InitializeAsync() => Fixture.InitializeAsync();
        public Task DisposeAsync() => Fixture.DisposeAsync();
    }

    [IntegrationTheory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [Endpoint("POST /mcp")]
    [InterfaceOperation(TestProtocols.Mcp, "tools/list")]
    public async Task Body_AtByteLimit_DispatchesDiscovery(bool modern, bool unknownLength)
    {
        using var request = BodyRequest(BodyLimit, modern, unknownLength);
        var before = host.Counter.Calls;

        using var response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        document.RootElement.GetProperty("id").GetInt32().Should().Be(1);
        document.RootElement.GetProperty("result").GetProperty("tools").ValueKind.Should().Be(JsonValueKind.Array);
        host.Counter.Calls.Should().BeGreaterThan(before, "the request reached the live discovery surface");
    }

    [IntegrationTheory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [Endpoint("POST /mcp")]
    [InterfaceOperation(TestProtocols.Mcp, "tools/list")]
    public async Task Body_OneByteOverLimit_Returns413BeforeDiscovery(bool modern, bool unknownLength)
    {
        using var request = BodyRequest(BodyLimit + 1, modern, unknownLength);
        var before = host.Counter.Calls;

        using var response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
        var body = await response.Content.ReadAsStringAsync();
        if (modern)
        {
            response.Content.Headers.ContentType!.MediaType.Should().Be("text/event-stream");
            body = body.Split('\n')
                .Single(line => line.StartsWith("data: ", StringComparison.Ordinal))["data: ".Length..];
        }

        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        root.GetProperty("id").ValueKind.Should().Be(JsonValueKind.Null);
        root.GetProperty("error").GetProperty("code").GetInt32().Should().Be(-32600);
        root.TryGetProperty("result", out _).Should().BeFalse();
        host.Counter.Calls.Should().Be(before, "oversized bodies must be rejected before dispatch");
    }

    private static HttpRequestMessage BodyRequest(int bytes, bool modern, bool unknownLength)
    {
        var metadata = modern
            ? "\"_meta\":{\"io.modelcontextprotocol/protocolVersion\":\"2026-07-28\",\"io.modelcontextprotocol/clientCapabilities\":{}},"
            : string.Empty;
        var prefix = "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/list\",\"params\":{" + metadata + "\"pad\":\"é";
        const string suffix = "\"}}";
        var padding = bytes - Encoding.UTF8.GetByteCount(prefix + suffix);
        var body = Encoding.UTF8.GetBytes(prefix + new string('x', padding) + suffix);
        body.Length.Should().Be(bytes);
        var content = unknownLength ? (HttpContent)new UnknownLengthContent(body) : new ByteArrayContent(body);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        var request = new HttpRequestMessage(HttpMethod.Post, "/mcp?view=default") { Content = content };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (modern)
        {
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
            request.Headers.Add("MCP-Protocol-Version", "2026-07-28");
            request.Headers.Add("Mcp-Method", "tools/list");
        }

        return request;
    }

    internal sealed class DiscoveryCounter : IMcpToolSource
    {
        private int _calls;
        internal int Calls => Volatile.Read(ref _calls);

        public ValueTask<IReadOnlyList<IMcpTool>> GetToolsAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            return ValueTask.FromResult<IReadOnlyList<IMcpTool>>([]);
        }
    }

    private sealed class UnknownLengthContent(byte[] body) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
            => stream.WriteAsync(body).AsTask();

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }
}
