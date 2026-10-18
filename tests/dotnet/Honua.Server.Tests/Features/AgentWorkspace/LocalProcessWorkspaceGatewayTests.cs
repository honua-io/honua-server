// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text;
using Honua.Core.Features.AgentWorkspace.Domain;
using Honua.Server.Features.AgentWorkspace;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Configuration;

namespace Honua.Server.Tests.Features.AgentWorkspace;

public sealed class LocalProcessWorkspaceGatewayTests
{
    private static readonly LocalProcessWorkspaceAccess Access = new("agent", "human", "workspace", "deployment", "tenant", true);

    [UnitTest]
    public async Task GetIdentity_UsesTrustedAssertionsWithoutHumanCredentials()
    {
        using var handler = new Handler(request =>
        {
            Assert.Equal("http://127.0.0.1:7194/project/identity", request.RequestUri!.AbsoluteUri);
            Assert.Equal("agent", request.Headers.GetValues("X-Honua-Actor-Id").Single());
            Assert.Equal("human", request.Headers.GetValues("X-Honua-Workspace-Owner-Id").Single());
            Assert.Equal("deployment", request.Headers.GetValues("X-Honua-Environment-Id").Single());
            Assert.Equal("true", request.Headers.GetValues("X-Honua-Workspace-Delegated").Single());
            Assert.Equal("tenant", request.Headers.GetValues("X-Honua-Tenant-Id").Single());
            Assert.Null(request.Headers.Authorization);
            Assert.False(request.Headers.Contains("Cookie"));
            return Json("""{"projectId":"p","revision":1,"sourceDigest":"hash","image":"sha256:image","binding":null}""");
        });
        using var client = new HttpClient(handler);
        var gateway = Create(client);
        var identity = await gateway.GetIdentityAsync(Access, CancellationToken.None);
        Assert.Equal("p", identity.ProjectId);
        Assert.Equal(1, handler.Calls);
    }

    [UnitTest]
    public async Task Bind_RejectsDelegationEvenWhenAgentIsWorkspaceOwner()
    {
        using var handler = new Handler(_ => throw new InvalidOperationException("Must not send."));
        using var client = new HttpClient(handler);
        await Assert.ThrowsAsync<LocalProcessWorkspaceException>(() =>
            Create(client).BindAsync(Access with { ActorId = "human" }, "project", CancellationToken.None));
        Assert.Equal(0, handler.Calls);
    }

    [UnitTheory]
    [InlineData("../secrets")]
    [InlineData("%2e%2e/secrets")]
    [InlineData("https://foreign/source.py")]
    [InlineData("a\\b")]
    [InlineData("/absolute.py")]
    [InlineData("a//b")]
    public async Task ReadSource_RejectsPathsBeforeSending(string path)
    {
        using var handler = new Handler(_ => throw new InvalidOperationException("Must not send."));
        using var client = new HttpClient(handler);
        await Assert.ThrowsAsync<ArgumentException>(() => Create(client).ReadSourceAsync(Access, path, CancellationToken.None));
        Assert.Equal(0, handler.Calls);
    }

    [UnitTest]
    public async Task Run_ReportsLostResponseWithoutReexecuting()
    {
        using var handler = new Handler(_ => throw new HttpRequestException("private transport details"));
        using var client = new HttpClient(handler);
        var failure = await Assert.ThrowsAsync<LocalProcessWorkspaceException>(() => Create(client).RunAsync(Access,
            new("p", new string('a', 32), new string('b', 64), "command"), CancellationToken.None));
        Assert.DoesNotContain("private transport", failure.Message, StringComparison.Ordinal);
        Assert.Equal(1, handler.Calls);
        Assert.Equal(503, failure.StatusCode);
    }

    [UnitTheory]
    [InlineData("")]
    [InlineData("command_with_underscores")]
    [InlineData("../command")]
    [InlineData("\u00e9")]
    public async Task RunAndReconciliation_RejectInvalidCommandIdsBeforeSending(string commandId)
    {
        using var handler = new Handler(_ => throw new InvalidOperationException("Must not send."));
        using var client = new HttpClient(handler);
        var gateway = Create(client);
        await Assert.ThrowsAsync<ArgumentException>(() => gateway.RunAsync(Access,
            new("p", new string('a', 32), new string('b', 64), commandId), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => gateway.GetCommandAsync(Access, commandId, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => gateway.GetCommandAsync(Access, new string('a', 81), CancellationToken.None));
        Assert.Equal(0, handler.Calls);
    }

    [UnitTest]
    public async Task ReadArtifact_RejectsChangedSuccessfulOutput()
    {
        using var handler = new Handler(request => request.RequestUri!.AbsolutePath.EndsWith("/artifacts/result.geojson", StringComparison.Ordinal)
            ? new(HttpStatusCode.OK) { Content = new ByteArrayContent(Encoding.UTF8.GetBytes("bad")) }
            : Json($$"""{"projectId":"p","snapshotId":"s","snapshotDigest":"hash","image":"image","runId":"{{new string('a', 32)}}","status":"succeeded","artifacts":[{"path":"result.geojson","bytes":3,"sha256":"{{new string('b', 64)}}"}]}"""));
        using var client = new HttpClient(handler);
        var failure = await Assert.ThrowsAsync<LocalProcessWorkspaceException>(() =>
            Create(client).ReadArtifactAsync(Access, new string('a', 32), "result.geojson", CancellationToken.None));
        Assert.Equal(502, failure.StatusCode);
        Assert.Equal(2, handler.Calls);
    }

    [UnitTheory]
    [InlineData("http://public.example/")]
    [InlineData("http://127.0.0.1:7194/path")]
    [InlineData("http://127.0.0.1:7194/?key=value")]
    [InlineData("http://user:password@127.0.0.1:7194/")]
    [InlineData("http://169.254.169.254/")]
    public void Configuration_RejectsUnpinnedOrPublicEndpoints(string url)
    {
        using var client = new HttpClient();
        Assert.Throws<ArgumentException>(() => Create(client, url));
    }

    private static LocalProcessWorkspaceGateway Create(HttpClient client, string url = "http://127.0.0.1:7194") => new(client,
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["LocalProcesses:WorkspaceUrl"] = url,
            ["HONUA_GP_WORKSPACE_KEY"] = new string('k', 40),
        }).Build());

    private static HttpResponseMessage Json(string text) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(text, Encoding.UTF8, "application/json"),
    };

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> handle) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(handle(request));
        }
    }
}
