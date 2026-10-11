// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Security.Claims;
using FluentAssertions;
using Honua.Core.Features.Operations.Domain;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.RateLimiting;
using Honua.Server.Features.Operations;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Honua.Server.Tests.Features.OperationsToolset;

[Trait("Tier", "Fast")]
public sealed class ApprovedReplayLoopbackTests
{
    [Theory]
    [InlineData("AdminSession", false, false)]
    [InlineData("ClientCertificate", false, false)]
    [InlineData("AdminSession", true, false)]
    [InlineData("AdminSession", false, true)]
    public async Task UrlImport_HeaderlessAuthenticatedCaller_UsesRevokedExactExecutionCredential(string scheme, bool failTransport, bool readOnly)
    {
        await using var services = new ServiceCollection().AddLogging().AddAuthorization(options =>
            options.AddPolicy(AuthenticationExtensions.AdminPolicy, policy => policy.RequireRole("admin")))
            .BuildServiceProvider();
        var keys = new InMemoryAdminApiKeyStore();
        var lineage = new OperationLineageAttestationStore(TimeProvider.System);
        using var handler = new ImportCredentialHandler(keys, lineage, failTransport, readOnly);
        using var client = new HttpClient(handler);
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(AdminConnectImportOperationExecutor.HttpClientName).Returns(client);
        var current = new DefaultHttpContext
        {
            RequestServices = services,
            User = new ClaimsPrincipal(new ClaimsIdentity([
                new Claim(ClaimTypes.NameIdentifier, "original-actor"),
                new Claim(ClaimTypes.Role, "admin")], scheme))
        };
        current.Request.Method = "POST";
        if (readOnly) ((ClaimsIdentity)current.User.Identity!).AddClaim(new Claim("scp", "honua.mcp.read"));
        current.Request.Path = "/api/v1/admin/import/upload-url";
        current.Request.Host = new HostString("localhost");
        current.Connection.LocalPort = 8080;
        current.Features.Set(new RateLimitAdmission());
        var executor = new AdminConnectImportOperationExecutor(
            AdminConnectImportOperationCatalog.Definitions.Single(item => item.OperationId == "admin.import.upload-url"),
            factory, new HttpContextAccessor { HttpContext = current }, keys, TimeProvider.System, lineage);

        var execute = () => executor.SubmitAsync(new OperationRequest
        {
            OperationId = executor.OperationId,
            Parameters = new Dictionary<string, string?> { ["sourceUrl"] = "https://example.com/a.geojson", ["tableName"] = "a" }
        }, new OperationPolicyContext
        {
            OperationInstanceId = "opinst-original", AuditId = "audit-original",
            PrincipalId = "original-actor", TenantId = "tenant-a"
        });

        if (failTransport)
            await execute.Should().ThrowAsync<HttpRequestException>();
        else
            (await execute()).Status.Should().Be(readOnly ? OperationHandleStatus.Failed : OperationHandleStatus.Completed);
        if (readOnly)
        {
            handler.CredentialId.Should().BeNull();
            (await keys.ListAsync(CancellationToken.None)).Should().BeEmpty();
            return;
        }
        handler.CredentialId.Should().NotBeNull();
        var issued = await keys.GetAsync(handler.CredentialId.GetValueOrDefault(), CancellationToken.None);
        Assert.NotNull(issued);
        issued.RevokedAt.Should().NotBeNull();
        issued.CreatedBy.Should().Be("original-actor");
    }

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
            // codeql[cs/local-not-disposed]: HttpClient disposes the response after the caller reads it
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") });
        }
    }

    private sealed class ImportCredentialHandler(IAdminApiKeyStore keys, OperationLineageAttestationStore lineage, bool failTransport, bool readOnly) : HttpMessageHandler
    {
        public Guid? CredentialId { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.Headers.Authorization.Should().BeNull();
            request.Headers.Contains("Cookie").Should().BeFalse();
            if (readOnly)
            {
                request.Headers.Contains("X-API-Key").Should().BeFalse();
                lineage.TryConsume(request.Headers.GetValues(OperationLineageHeaders.Attestation).Single(), out var denied).Should().BeTrue();
                denied.Admission.Should().BeNull();
                // codeql[cs/local-not-disposed]: the executor owns and disposes this response
                return new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("{}") };
            }
            var credential = await keys.ValidateAsync(request.Headers.GetValues("X-API-Key").Single(), cancellationToken);
            Assert.NotNull(credential);
            CredentialId = credential.Record.Id;
            credential.Record.Permissions.Should().Equal(
                "admin:operation:POST:/api/v1/admin/import/upload-url", "admin:operation:tenant:tenant-a");
            var token = request.Headers.GetValues(OperationLineageHeaders.Attestation).Single();
            lineage.TryConsume(token, out var attested).Should().BeTrue();
            attested.OperationInstanceId.Should().Be("opinst-original");
            attested.AuditId.Should().Be("audit-original");
            attested.Admission.Should().Be(new OperationLoopbackAdmission(
                credential.Record.Id, "POST", "/api/v1/admin/import/upload-url", "tenant-a"));
            if (failTransport) throw new HttpRequestException("deliberate transport failure");
            // codeql[cs/local-not-disposed]: the executor owns and disposes this response
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
        }
    }
}
