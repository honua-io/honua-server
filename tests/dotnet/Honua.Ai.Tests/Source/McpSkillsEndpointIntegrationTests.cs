// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.Protocols.Mcp;

[Collection("Database")]
[Protocol(TestProtocols.Mcp)]
[Operation(Operations.GetMetadata)]
public sealed class McpSkillsEndpointIntegrationTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new();
    public Task InitializeAsync() => _fixture.InitializeAsync();
    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Endpoint("POST /mcp")]
    [InterfaceOperation(TestProtocols.Mcp, "skills/list")]
    public async Task DiscoveryAndManifest_ReadEveryFileWithMatchingBytesAndFrontmatter()
    {
        var discovery = await SendAsync("server/discover");
        Assert.True(discovery.GetProperty("result").GetProperty("capabilities")
            .GetProperty("extensions").TryGetProperty("io.modelcontextprotocol/skills", out _));
        var listing = (await SendAsync("skills/list")).GetProperty("result");
        Assert.Equal("complete", listing.GetProperty("resultType").GetString());
        Assert.Equal("private", listing.GetProperty("cacheScope").GetString());
        Assert.Equal(0, listing.GetProperty("ttlMs").GetInt32());
        var entries = listing.GetProperty("skills").EnumerateArray().ToArray();
        Assert.Equal(5, entries.Length);
        foreach (var entry in entries)
        {
            var uri = entry.GetProperty("uri").GetString()!;
            var fetched = (await SendAsync("skills/get", uri)).GetProperty("result").GetProperty("skill");
            Assert.Equal(entry.GetRawText(), fetched.GetRawText());
            var directory = System.IO.Path.Combine(FindRoot().FullName, "skills",
                entry.GetProperty("frontmatter").GetProperty("name").GetString()!);
            var expectedUris = Directory.GetFiles(directory, "*", SearchOption.AllDirectories)
                .Select(path => "skill://" + System.IO.Path.GetRelativePath(
                    System.IO.Path.Combine(FindRoot().FullName, "skills"), path).Replace('\\', '/'))
                .Order(StringComparer.Ordinal).ToArray();
            Assert.Equal(expectedUris, entry.GetProperty("resources").EnumerateArray()
                .Select(resource => resource.GetProperty("uri").GetString()!).Order(StringComparer.Ordinal).ToArray());
            foreach (var resource in entry.GetProperty("resources").EnumerateArray())
            {
                var resourceUri = resource.GetProperty("uri").GetString()!;
                var read = (await SendAsync("resources/read", resourceUri)).GetProperty("result");
                var content = Assert.Single(read.GetProperty("contents").EnumerateArray());
                var bytes = content.TryGetProperty("text", out var text)
                    ? Encoding.UTF8.GetBytes(text.GetString()!)
                    : Convert.FromBase64String(content.GetProperty("blob").GetString()!);
                Assert.Equal(resource.GetProperty("size").GetInt64(), bytes.LongLength);
                Assert.Equal(resource.GetProperty("digest").GetString(),
                    "sha256:" + Convert.ToHexStringLower(SHA256.HashData(bytes)));
                var source = System.IO.Path.Combine(FindRoot().FullName, "skills", resourceUri["skill://".Length..]);
                Assert.Equal(File.ReadAllBytes(source), bytes);
                if (resourceUri == uri)
                {
                    var markdown = Encoding.UTF8.GetString(bytes);
                    using var frontmatter = JsonDocument.Parse(markdown.Split("---", 3)[1]);
                    Assert.True(JsonElement.DeepEquals(frontmatter.RootElement, entry.GetProperty("frontmatter")));
                }
            }
        }
    }

    [IntegrationTheory]
    [InlineData("skill://missing/SKILL.md")]
    [InlineData("skill://honua-install/../AGENTS.md")]
    [InlineData("skill://honua-install/%2e%2e/AGENTS.md")]
    [Endpoint("POST /mcp")]
    [InterfaceOperation(TestProtocols.Mcp, "skills/get")]
    public async Task Get_UnknownOrNoncanonicalUri_ReturnsInvalidParams(string uri)
    {
        var result = await SendAsync("skills/get", uri, HttpStatusCode.BadRequest);
        Assert.Equal(-32602, result.GetProperty("error").GetProperty("code").GetInt32());
    }

    private async Task<JsonElement> SendAsync(string method, string? uri = null,
        HttpStatusCode expectedStatus = HttpStatusCode.OK)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        request.Headers.Add("MCP-Protocol-Version", "2026-07-28");
        request.Headers.Add("Mcp-Method", method);
        if (uri is not null) request.Headers.Add("Mcp-Name", uri);
        var parameters = "\"_meta\":{\"io.modelcontextprotocol/protocolVersion\":\"2026-07-28\",\"io.modelcontextprotocol/clientCapabilities\":{}}";
        if (uri is not null) parameters += ",\"uri\":" + JsonSerializer.Serialize(uri);
        request.Content = new StringContent("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":" +
            JsonSerializer.Serialize(method) + ",\"params\":{" + parameters + "}}", Encoding.UTF8, "application/json");
        using var response = await _fixture.Client.SendAsync(request);
        Assert.Equal(expectedStatus, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }

    private static DirectoryInfo FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(System.IO.Path.Combine(directory.FullName, "Honua.sln")))
            directory = directory.Parent;
        return directory ?? throw new InvalidOperationException("Repository root not found.");
    }
}
