// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.AuditLog.Abstractions;
using Honua.Core.Features.MultiTenancy.Abstractions;
using Honua.Core.Features.Operations.Domain;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Middleware;
using Honua.Infrastructure.MultiTenancy;
using Honua.Server.Features.Operations;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Honua.Server.Tests.Features.Operations;

public sealed class AdminOperationsGaContractTests
{
    [UnitTest]
    public async Task AdminMutation_AuditDetails_PreserveEffectiveTenant()
    {
        var tenant = new RequestTenantContext();
        tenant.Set("tenant-a", TenantContextSource.Claim);
        AuditEvent? recorded = null;
        var audit = Substitute.For<IAuditLog>();
        audit.RecordAsync(Arg.Do<AuditEvent>(value => recorded = value), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>("audit-test"));
        using var services = new ServiceCollection()
            .AddSingleton<ITenantContext>(tenant).AddSingleton(audit).BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services };
        context.Request.Method = "PUT";
        context.Request.Path = "/api/v1/admin/metadata/layers/1/filter";
        context.User = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, "operator"),
            new Claim("tenant_id", "tenant-a"),
            new Claim(ClaimTypes.Role, "admin")
        ], "test"));
        context.SetEndpoint(new RouteEndpoint(_ => Task.CompletedTask,
            RoutePatternFactory.Parse("/api/v1/admin/metadata/layers/{layerId}/filter"),
            0, EndpointMetadataCollection.Empty, "Set layer filter"));
        var resolver = Substitute.For<IAuditActionResolver>();
        resolver.Resolve(Arg.Any<string>(), Arg.Any<string?>()).Returns(new AuditActionDescriptor
        {
            EventType = AuditEventType.AdminAction, Action = "admin.config.update", ResourceType = "admin"
        });
        var middleware = new AuditLogMiddleware(_ => Task.CompletedTask, resolver);

        await middleware.InvokeAsync(context);

        recorded.Should().NotBeNull();
        recorded!.Details.Should().Contain("tenant-a", "the shared audit table must identify the effective target tenant");
    }

    [UnitTest]
    public void AdminApprovalPlan_ReviewerProjection_IdentifiesTargetAndChange()
    {
        var definition = AdminApiOperationCatalog.Definitions.Single(d => d.OperationId == "admin.layer.filter.set");
        var descriptor = AdminApiOperationCatalog.Descriptors.Single(d => d.OperationId == definition.OperationId);
        var mapper = new AdminApiOperationApprovalRequestMapper(definition);
        var proposal = mapper.Map(descriptor, new OperationRequest
        {
            OperationId = definition.OperationId,
            Parameters = new Dictionary<string, string?>
            {
                ["layerId"] = "123",
                ["permanentFilter"] = """{"expression":"status = 'open'","language":"arcgis-sql"}"""
            }
        }, new OperationPolicyContext { PrincipalId = "requester", TenantId = "tenant-a" },
            new PolicyDecision { Kind = PolicyDecisionKind.RequireApproval });

        // ProposalEndpoints.ToDetail exposes these fields, not ExecutionPayload.
        var reviewerText = string.Join("\n", new[] { proposal.Plan!.Summary }
            .Concat(proposal.Plan.Diff).Concat(proposal.Plan.DryRun));
        reviewerText.Should().Contain("123", "the reviewer must be shown the target resource");
        reviewerText.Should().Contain("status = 'open'", "the reviewer must be shown the proposed change");
    }

    [UnitTest]
    public async Task ApprovedReplay_TenantHeader_PreservesApprovedTenant()
    {
        var tenant = new RequestTenantContext();
        using var services = new ServiceCollection().AddSingleton<ITenantContext>(tenant).BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services };
        context.Request.Path = "/api/v1/admin/metadata/layers/1/fields";
        context.Request.Headers[TenantContextOptions.TenantHeaderName] = "approved-tenant";
        // These are the claims issued by ApiKeyAuthenticationHandler for the
        // exact-method/path credential minted by the approved replay executor.
        context.User = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.Name, "approved-operation:proposal-test"),
            new Claim(ClaimTypes.Role, "approved-operation"),
            new Claim("auth_type", "api_key"),
            new Claim("api_key_id", "11111111-1111-1111-1111-111111111111"),
            new Claim("permission", AdminApiKeyPermission.CreateApprovedOperationGrant("PUT", context.Request.Path))
        ], "ApiKey"));
        var nextCalled = false;
        var middleware = new TenantContextMiddleware(_ => { nextCalled = true; return Task.CompletedTask; },
            Options.Create(new TenantContextOptions()), NullLogger<TenantContextMiddleware>.Instance);

        await middleware.InvokeAsync(context);

        nextCalled.Should().BeTrue();
        tenant.TenantId.Should().Be("approved-tenant", "replay must use the tenant sealed into the approved payload");
    }

    [UnitTest]
    public async Task ReleasePackageList_Pagination_RemainsInQuery()
    {
        using var capture = new CaptureHandler();
        using var client = new HttpClient(capture);
        var executor = CreateOperate("admin.metadata.release-packages.list", client);
        await executor.SubmitAsync(new OperationRequest
        {
            OperationId = executor.OperationId,
            Parameters = new Dictionary<string, string?> { ["limit"] = "1", ["offset"] = "2" }
        }, new OperationPolicyContext());

        capture.Uri!.AbsolutePath.Should().Be("/api/v1/admin/metadata/release-packages");
        capture.Uri.Query.Should().Be("?limit=1&offset=2");
    }

    [UnitTest]
    public async Task SetLayerEnabled_ServiceFilter_RemainsInQuery()
    {
        using var capture = new CaptureHandler();
        using var client = new HttpClient(capture);
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(AdminApiOperationExecutor.HttpClientName).Returns(client);
        var executor = new AdminApiOperationExecutor(
            AdminApiOperationCatalog.Definitions.Single(d => d.OperationId == "admin.layer.set-enabled"),
            factory, Context(), new InMemoryAdminApiKeyStore(TimeProvider.System), TimeProvider.System,
            new OperationLineageAttestationStore(TimeProvider.System));
        await executor.SubmitAsync(new OperationRequest
        {
            OperationId = executor.OperationId,
            ConnectionId = "11111111-1111-1111-1111-111111111111",
            Parameters = new Dictionary<string, string?>
            {
                ["layerId"] = "1", ["serviceName"] = "roads", ["enabled"] = "true"
            }
        }, new OperationPolicyContext());

        capture.Uri!.Query.Should().Be("?serviceName=roads");
        capture.Uri.AbsolutePath.Should().EndWith("/layers/1/enabled");
    }

    [UnitTest]
    public async Task ReleasePackageCreate_NumericText_RemainsAString()
    {
        using var capture = new CaptureHandler();
        using var client = new HttpClient(capture);
        var executor = CreateOperate("admin.metadata.release-packages.create", client);
        await executor.SubmitAsync(new OperationRequest
        {
            OperationId = executor.OperationId,
            Parameters = new Dictionary<string, string?> { ["sourceEnvironment"] = "staging", ["title"] = "2026" }
        }, new OperationPolicyContext());

        using var body = JsonDocument.Parse(capture.Body!);
        body.RootElement.GetProperty("title").ValueKind.Should().Be(JsonValueKind.String);
        body.RootElement.GetProperty("title").GetString().Should().Be("2026");
    }

    [UnitTest]
    public async Task MetadataPrevalidate_MissingRequiredTarget_IsInvalid()
    {
        using var capture = new CaptureHandler();
        using var client = new HttpClient(capture);
        var executor = CreateOperate("admin.metadata.prevalidate", client);
        var validation = await executor.ValidateAsync(new OperationRequest { OperationId = executor.OperationId });
        validation.IsValid.Should().BeFalse("the published schema requires targetEnvironment and a release package");
    }

    private static AdminOperateOperationExecutor CreateOperate(string id, HttpClient client)
    {
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(AdminOperateOperationExecutor.HttpClientName).Returns(client);
        return new AdminOperateOperationExecutor(AdminOperateOperationCatalog.Definitions.Single(d => d.OperationId == id),
            factory, Context(), new InMemoryAdminApiKeyStore(TimeProvider.System), TimeProvider.System,
            new OperationLineageAttestationStore(TimeProvider.System));
    }

    private static IHttpContextAccessor Context()
    {
        var context = new DefaultHttpContext();
        context.Request.Host = new HostString("localhost", 8080);
        context.Request.Scheme = "http";
        context.Connection.LocalPort = 8080;
        return new HttpContextAccessor { HttpContext = context };
    }

    private sealed class CaptureHandler : HttpMessageHandler
    {
        public Uri? Uri { get; private set; }
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Uri = request.RequestUri;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
        }
    }
}
