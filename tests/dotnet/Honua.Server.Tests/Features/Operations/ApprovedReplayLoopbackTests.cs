// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using FluentAssertions;
using Honua.Core.Features.Operations.Domain;
using Honua.Infrastructure.Authentication;
using Honua.Server.Features.Operations;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using NSubstitute;

namespace Honua.Server.Tests.Features.OperationsToolset;

[Trait("Tier", "Fast")]
public sealed class ApprovedReplayLoopbackTests
{
    [Theory]
    [InlineData(false, "http")]
    [InlineData(true, "https")]
    public async Task ConnectReplay_StaysOnIssuingReplica(bool localTls, string expectedScheme)
    {
        using var handler = new CaptureHandler();
        using var client = new HttpClient(handler);
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(AdminConnectImportOperationExecutor.HttpClientName).Returns(client);
        var current = new DefaultHttpContext();
        current.Request.Scheme = "https";
        current.Request.Host = new HostString("load-balancer.example");
        current.Connection.LocalPort = 8080;
        if (localTls) current.Features.Set(Substitute.For<ITlsConnectionFeature>());
        var executor = new AdminConnectImportOperationExecutor(
            AdminConnectImportOperationCatalog.Definitions.Single(item => item.OperationId == "admin.connections.delete"),
            factory, new HttpContextAccessor { HttpContext = current }, new InMemoryAdminApiKeyStore(), TimeProvider.System,
            new OperationLineageAttestationStore(TimeProvider.System));
        await executor.SubmitAsync(new OperationRequest
        {
            OperationId = executor.OperationId,
            Parameters = new Dictionary<string, string?> { ["id"] = "connection-a" }
        }, new OperationPolicyContext { ApprovedProposalId = "proposal-a", TenantId = "tenant-a" });

        handler.Uri!.Host.Should().Be("127.0.0.1");
        handler.Uri.Port.Should().Be(8080);
        handler.Uri.Scheme.Should().Be(expectedScheme);
        handler.Uri.AbsolutePath.Should().Be("/api/v1/admin/connections/connection-a");
        handler.Host.Should().Be("load-balancer.example");
    }

    private sealed class CaptureHandler : HttpMessageHandler
    {
        public Uri? Uri { get; private set; }
        public string? Host { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Uri = request.RequestUri;
            Host = request.Headers.Host;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") });
        }
    }
}
