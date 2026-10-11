// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Honua.Ai.Protocols.Mcp;
using Honua.Ai.Protocols.Mcp.Models;
using Honua.Ai.Protocols.Mcp.Resources;
using Honua.Core.Features.Capabilities;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Services;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.Mcp;

[Protocol(TestProtocols.Mcp)]
public sealed class McpTemporaryFileResourceTests
{
    // A valid one-pixel PNG; the resource adapter must preserve its exact bytes.
    private static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jRZkAAAAASUVORK5CYII=");

    [Theory]
    [Trait("Category", "Unit")]
    [Trait("Tier", "Fast")]
    [InlineData(null)]
    [InlineData("https://maps.example.test/artifacts/")]
    [Endpoint("POST /mcp resources/read")]
    public async Task Read_UsesCanonicalStoreAndPreservesOwnerExpiryAndExactBytes(string? baseUrl)
    {
        var directory = Path.Join(Path.GetTempPath(), "honua-mcp-resource-" + Guid.NewGuid().ToString("N"));
        try
        {
            var options = Options.Create(new TemporaryFileOptions { StorageDirectory = directory, BaseUrl = baseUrl });
            using var store = new FileSystemTemporaryFileService(options, NullLogger<FileSystemTemporaryFileService>.Instance);
            using var services = new ServiceCollection().AddSingleton<ITemporaryFileService>(store).BuildServiceProvider();
            var resource = new TemporaryFileResource(options, NullLogger<TemporaryFileResource>.Instance);
            var surface = new McpDataAccessSurface([], [resource], NullLogger<McpDataAccessSurface>.Instance);
            McpRegistryCompositionValidator.FindDrift(surface, new CapabilityRegistry()).Should().BeEmpty();
            var owner = Context(services, "owner");
            var uri = await store.StoreTemporaryFileAsync(Png, "image/png", principal: owner.User);
            resource.DescribeTemplates().Single().UriTemplate.Should().Be(TemporaryFileResource.Template);

            var response = await Read(surface, owner, uri);
            response.Error.Should().BeNull();
            var content = response.Result!.Value.GetProperty("contents")[0];
            content.GetProperty("uri").GetString().Should().Be(uri);
            content.GetProperty("mimeType").GetString().Should().Be("image/png");
            Convert.FromBase64String(content.GetProperty("blob").GetString()!).Should().Equal(Png);
            content.TryGetProperty("text", out _).Should().BeFalse();
            response.Result.Value.TryGetProperty("nextCursor", out _).Should().BeFalse();

            var alias = TemporaryFileResource.UriPrefix + uri[(uri.LastIndexOf('/') + 1)..];
            var aliasRead = await Read(surface, owner, alias);
            aliasRead.Error.Should().BeNull();
            aliasRead.Result!.Value.GetProperty("contents")[0].GetProperty("blob").GetString()
                .Should().Be(content.GetProperty("blob").GetString());
            var aliasDenied = await Read(surface, Context(services, "other-owner"), alias);
            aliasDenied.Error.Should().NotBeNull();
            aliasDenied.Result.Should().BeNull();

            var denied = await Read(surface, Context(services, "other-owner"), uri);
            denied.Error.Should().NotBeNull();
            denied.Result.Should().BeNull();
            var anonymous = await Read(surface, new DefaultHttpContext { RequestServices = services }, uri);
            anonymous.Error.Should().NotBeNull();
            anonymous.Result.Should().BeNull();

            // Expire the canonical metadata deterministically, without sleeping.
            var metadataPath = Directory.GetFiles(directory, "*.meta").Single();
            var metadata = JsonNode.Parse(await File.ReadAllTextAsync(metadataPath))!;
            metadata["ExpiresAt"] = DateTimeOffset.UtcNow.AddMinutes(-1).ToString("O");
            await File.WriteAllTextAsync(metadataPath, metadata.ToJsonString());
            var expired = await Read(surface, owner, uri);
            expired.Error.Should().NotBeNull();
            expired.Result.Should().BeNull();
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Theory]
    [Trait("Category", "Unit")]
    [Trait("Tier", "Fast")]
    [InlineData("honua://temporary-files/../private.png")]
    [InlineData("honua://temporary-files/%2e%2e%2fprivate.png")]
    [InlineData("/temp/../private.png")]
    [InlineData("/temp/%2e%2e%2fprivate.png")]
    [InlineData("/temp/folder/private.png")]
    [InlineData("/temp/abc.png?token=secret")]
    [InlineData("/temp/abc.png#fragment")]
    [InlineData("/temp/abc\\private.png")]
    [InlineData("https://unrelated.example/temp/abc.png")]
    [InlineData("file:///temp/abc.png")]
    [InlineData("/temp/")]
    [InlineData("/temp/.png")]
    public void CanHandle_RejectsNonCanonicalUris(string uri)
    {
        var resource = new TemporaryFileResource(Options.Create(new TemporaryFileOptions()), NullLogger<TemporaryFileResource>.Instance);
        resource.CanHandle(uri).Should().BeFalse();
    }

    [UnitTest]
    public void CanHandle_AcceptsOpaqueSharedStoreTokenOnlyUnderConfiguredPrefix()
    {
        const string token = "AAbbcCDDEEFF00112233445566778899_a-bCDefGHiJ";
        var resource = new TemporaryFileResource(Options.Create(new TemporaryFileOptions
        {
            BaseUrl = "https://maps.example.test/temp"
        }), NullLogger<TemporaryFileResource>.Instance);
        resource.CanHandle("https://maps.example.test/temp/" + token + ".png").Should().BeTrue();
        resource.CanHandle("https://evil.example/temp/" + token + ".png").Should().BeFalse();
    }

    [Theory]
    [Trait("Category", "Unit")]
    [Trait("Tier", "Fast")]
    [InlineData(750_000, true)]
    [InlineData(750_001, false)]
    [Endpoint("POST /mcp resources/read")]
    public async Task Read_HardBinaryLimitIsCheckedBeforeBase64Allocation(int byteCount, bool accepted)
    {
        const string uri = "/temp/0123456789abcdef0123456789abcdef.png";
        var store = Substitute.For<ITemporaryFileService>();
        store.GetTemporaryFileAsync(Arg.Any<string>(), Arg.Any<ClaimsPrincipal?>(), Arg.Any<CancellationToken>())
            .Returns((new byte[byteCount], "image/png"));
        using var services = new ServiceCollection().AddSingleton(store).BuildServiceProvider();
        var resource = new TemporaryFileResource(Options.Create(new TemporaryFileOptions()), NullLogger<TemporaryFileResource>.Instance);
        var surface = new McpDataAccessSurface([], [resource], NullLogger<McpDataAccessSurface>.Instance);
        var response = await surface.DispatchAsync(Context(services, "owner"), new McpJsonRpcRequest
        {
            JsonRpc = "2.0",
            Id = McpTestFactory.ParseJson("1"),
            Method = "resources/read",
            Params = McpTestFactory.ParseJson($$"""{"uri":"{{uri}}","maxChars":1000000}""")
        }, CancellationToken.None);
        response.Should().NotBeNull();
        if (accepted)
        {
            response!.Error.Should().BeNull();
            response.Result!.Value.GetProperty("contents")[0].GetProperty("blob").GetString()!.Length.Should().Be(1_000_000);
        }
        else
        {
            response!.Result.Should().BeNull();
            response.Error!.Message.Should().Contain("smaller image");
        }
    }

    [UnitTest]
    [Endpoint("POST /mcp resources/read")]
    public async Task Read_UnresolvedBearerTenantCannotReachArtifactStore()
    {
        var store = Substitute.For<ITemporaryFileService>();
        using var services = new ServiceCollection().AddSingleton(store).BuildServiceProvider();
        var resource = new TemporaryFileResource(Options.Create(new TemporaryFileOptions()), NullLogger<TemporaryFileResource>.Instance);
        var surface = new McpDataAccessSurface([], [resource], NullLogger<McpDataAccessSurface>.Instance);
        var context = Context(services, "owner");
        context.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, "owner")], OidcAuthenticationExtensions.JwtBearerScheme));
        var response = await Read(surface, context, "/temp/0123456789abcdef0123456789abcdef.png");
        response.Result.Should().BeNull();
        response.Error.Should().NotBeNull();
        await store.DidNotReceive().GetTemporaryFileAsync(Arg.Any<string>(), Arg.Any<ClaimsPrincipal?>(), Arg.Any<CancellationToken>());
    }

    private static DefaultHttpContext Context(IServiceProvider services, string owner) => new()
    {
        RequestServices = services,
        User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, owner)], "test"))
    };

    private static async Task<McpJsonRpcResponse> Read(McpDataAccessSurface surface, HttpContext context, string uri) =>
        await surface.DispatchAsync(context, new McpJsonRpcRequest
        {
            JsonRpc = "2.0",
            Id = McpTestFactory.ParseJson("1"),
            Method = "resources/read",
            Params = McpTestFactory.ParseJson("{\"uri\":" + JsonSerializer.Serialize(uri) + "}")
        }, CancellationToken.None) ?? throw new InvalidOperationException("Resource requests must produce a response.");
}
