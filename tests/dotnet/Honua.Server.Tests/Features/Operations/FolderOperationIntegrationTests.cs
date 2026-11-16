// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Infrastructure.Security;
using Honua.Core.Features.Guardrails.Domain;
using Microsoft.AspNetCore.Http;
using Honua.Server.Features.Admin;
using System.Net;
using System.Security.Claims;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Identity.Abstractions;
using Honua.Core.Features.Licensing.Abstractions;
using Honua.TestKit.Extensions;
using Microsoft.AspNetCore.Authentication;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Security.Domain;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Infrastructure.Authentication;
using Honua.Server.Tests.Features.Admin;
using Honua.Server.Features.Operations;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Features.OperationsToolset;

[Collection(RedisFixture.CollectionName)]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.Configuration)]
public sealed partial class FolderOperationIntegrationTests(RedisFixture redis) : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new WebAppFixture().WithProductionJobSubstrate();
    private ReplayServiceAuthorization _authorization = null!;
    private readonly FolderRequesterMembership _memberships = new();
    private readonly FolderRequesterLicense _licensing = new();
    private HttpClient _proposer = null!;
    private HumanApprovalHttpClient _approver = null!;
    private readonly AdminAuthEndpointsTests.StubHttpClientFactory _identity = AdminAuthEndpointsTests.CreateStubFactory();
    private IOperationProposalStore _proposals = null!;

    [IntegrationTest]
    [Endpoint("POST /api/v1/operations/{id}/submit")]
    [Endpoint("POST /api/v1/admin/proposals/{id}/approve")]
    public async Task ServiceMove_ApprovedReplay_RevalidatesCurrentRequesterServiceRoles()
    {
        var initial = await SnapshotAsync();
        await ApplyAsync("folder.create", new() { ["name"] = "Destination", ["If-Match"] = initial.Etag });
        var before = await RestrictServiceToCurrentRequesterRoleAsync();
        var destination = before.Graph.Folders.Single().Metadata.Id;
        var payload = new AdminApiOperationApprovalPayload
        {
            OperationId = "service.move",
            TenantId = "public",
            SchemaName = _fixture.CurrentSchema,
            ServiceName = "test",
            Parameters = new() { ["folderId"] = destination, ["If-Match"] = before.Etag }
        };
        await WithRequesterContextAsync(payload, async context =>
        {
            var accepted = await CreateRequesterProposalAsync(context, new()
            {
                OperationId = payload.OperationId,
                Kind = OperationClass.AdminConfigChange,
                TenantId = payload.TenantId,
                RequestedBy = CanonicalSecurityActor.Resolve(context.User)!.ActorId,
                Plan = new OperationProposalPlan
                {
                    Summary = "Move service using current requester membership",
                    ExecutionPayload = JsonSerializer.Serialize(payload, AdminApiOperationApprovalJsonContext.Default.AdminApiOperationApprovalPayload)
                }
            });
            accepted.Outcome.Should().Be(OperationGatewayOutcome.ProposalCreated, accepted.Message);
            await ApproveAsync(accepted.ProposalId!);
            var completed = (await _proposals.GetAsync(accepted.ProposalId!))!;
            completed.Status.Should().Be(OperationProposalStatus.Succeeded, completed.ResolutionReason);
        }, "folder-requester");
        (await SnapshotAsync()).Graph.Services.Where(service => service.Metadata.Name == "test")
            .Should().OnlyContain(service => service.FolderId == destination);
    }

    private async Task<MetadataV2GraphSnapshot> RestrictServiceToCurrentRequesterRoleAsync()
    {
        using var scope = _fixture.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IMetadataV2GraphStore>();
        var current = await store.GetCurrentAsync();
        return await store.SaveAsync(current.Graph with
        {
            Services = current.Graph.Services.Select(service => service.Metadata.Name == "test"
                ? service with
                {
                    AccessPolicy = new AccessPolicy
                    {
                        AllowedRoles = ["original-membership"],
                        AllowedWriteRoles = ["original-membership"]
                    }
                } : service).ToArray()
        }, current.Etag);
    }

    private async Task<MetadataV2GraphSnapshot> RestrictLastServiceFacetAsync()
    {
        using var scope = _fixture.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IMetadataV2GraphStore>();
        var before = await store.GetCurrentAsync();
        var facet = before.Graph.Services.Last(service => service.Metadata.Name == "test");
        var restricted = before.Graph with
        {
            Services = before.Graph.Services.Select(service => service.Metadata.Id == facet.Metadata.Id
                ? service with { AccessPolicy = new AccessPolicy { AllowedRoles = ["service-owner"], AllowedWriteRoles = ["service-owner"] } }
                : service).ToArray()
        };
        var saved = await store.SaveAsync(restricted, before.Etag);
        saved.Etag.Should().NotBe(before.Etag);
        var policyContext = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        policyContext.Request.Method = HttpMethods.Put;
        policyContext.Request.Path = "/api/v1/admin/services/test/folder";
        policyContext.Request.Headers["X-API-Key"] = _proposer.DefaultRequestHeaders.GetValues("X-API-Key").Single();
        var authenticated = await policyContext.AuthenticateAsync(AuthenticationExtensions.ApiKeyScheme);
        authenticated.Succeeded.Should().BeTrue();
        policyContext.User = authenticated.Principal!;
        var deniedFacet = saved.Graph.Services.Single(service => service.Metadata.Id == facet.Metadata.Id);
        MetadataV2TenantVisibility.IsVisibleToTenant(deniedFacet, null).Should().BeTrue();
        (await FolderEndpoints.RequireServiceAccessAsync(policyContext, deniedFacet)).Should().NotBeNull(
            "the real authenticated requester must fail the shared service-policy check before testing proposal admission");
        return saved;
    }

    public async Task InitializeAsync()
    {
        HumanApprovalHttpClient.ConfigureKestrel(_fixture, _identity).ConfigureWebHost(builder =>
        {
            builder.UseEnvironment("Test");
            builder.UseSetting("ConnectionStrings:redis", redis.ConnectionString);
            builder.UseSetting("HONUA_DEV_AUTH", "false");
            builder.UseSetting("HONUA_ADMIN_PASSWORD", WebAppFixture.SharedAdminPassword);
            builder.UseSetting("Licensing:DevGrantEdition", "Pro");
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(ApprovalPolicy()));
        }).ConfigureServices(services =>
        {
            FolderPostgresTestHost.Configure(services, () => _fixture.CurrentSchema);
            services.AddHttpClient(AdminApiOperationExecutor.HttpClientName)
                .AddHttpMessageHandler(() => new RejectFolderMutationLoopbackHandler());
            services.AddSingleton<IPrincipalMembershipSource>(_memberships);
            services.AddSingleton<ILicenseEntitlementService>(_licensing);
            services.AddAuthentication().AddScheme<AuthenticationSchemeOptions, RlsClaimsTestAuthHandler>(
                RlsClaimsTestAuthHandler.SchemeName, _ => { });
            services.AddSingleton<IOperatorAuthorizationEvaluator>(provider =>
                _authorization = new ReplayServiceAuthorization(ActivatorUtilities.CreateInstance<OperatorAuthorizationEvaluator>(provider)));
        });
        await _fixture.InitializeAsync();
        await FolderPostgresTestHost.InstallSchemaAsync(_fixture);
        _proposer = await CreateClientAsync("proposer", ["admin:*"]);
        _approver = await HumanApprovalHttpClient.AuthenticateAsync(_fixture, _identity, tenantId: "public");
        _proposals = _fixture.Services.GetRequiredService<IOperationProposalStore>();
    }

    public async Task DisposeAsync()
    {
        await DeletePublishConnectionAsync();
        _proposer.Dispose();
        _approver.Dispose();
        using var identity = _identity;
        await _fixture.DisposeAsync();
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/operations/{id}/submit")]
    [Endpoint("POST /api/v1/admin/proposals/{id}/approve")]
    public async Task FolderOperations_ApprovedLifecycle_PersistsMembershipAliasesAndDeletion()
    {
        var initial = await SnapshotAsync();
        var listed = await SubmitAsync("folder.list", []);
        listed.GetProperty("result").GetProperty("details").GetProperty("etag").GetString().Should().Be(initial.Etag);
        await ApplyAsync("folder.create", new() { ["name"] = "Survey", ["title"] = "Field survey", ["If-Match"] = initial.Etag });
        var created = await SnapshotAsync();
        var folder = created.Graph.Folders.Should().ContainSingle().Subject;
        await ApplyAsync("service.move", new() { ["folderId"] = folder.Metadata.Id, ["If-Match"] = created.Etag });
        var moved = await SnapshotAsync();
        moved.Graph.Services.Where(service => service.Metadata.Name == "test")
            .Should().HaveCountGreaterThan(1).And.OnlyContain(service => service.FolderId == folder.Metadata.Id);
        await ApplyAsync("folder.update", new() { ["id"] = folder.Metadata.Id, ["name"] = "Survey2", ["If-Match"] = moved.Etag });
        var renamed = await SnapshotAsync();
        renamed.Graph.Folders.Single().PreviousNames.Should().Contain("Survey");
        renamed.Graph.Folders.Single().Metadata.Title.Should().Be("Field survey");
        await ApplyAsync("service.move", new() { ["folderId"] = null, ["If-Match"] = renamed.Etag });
        var returned = await SnapshotAsync();
        returned.Graph.Services.Where(service => service.Metadata.Name == "test").Should().OnlyContain(service => service.FolderId == null);
        await ApplyAsync("folder.delete", new() { ["id"] = folder.Metadata.Id, ["If-Match"] = returned.Etag });
        var deleted = await SnapshotAsync();
        deleted.Graph.Folders.Single().Deleted.Should().BeTrue();
        deleted.Etag.Should().NotBe(initial.Etag);
    }

    [IntegrationTheory]
    [Endpoint("POST /api/v1/operations/{id}/submit")]
    [Endpoint("POST /api/v1/admin/proposals/{id}/approve")]
    [InlineData(null, null)]
    [InlineData("workspace", "public")]
    public async Task FolderCreate_ApprovalReplay_PreservesResolvedOwnership(string? requestedNamespace, string? tenant)
    {
        var initial = await SnapshotAsync();
        var parameters = new Dictionary<string, string?>
        {
            ["name"] = "Owned",
            ["If-Match"] = initial.Etag,
            ["tenantId"] = "caller-supplied-tenant"
        };
        if (requestedNamespace is not null) parameters["namespace"] = requestedNamespace;
        await ApplyAsync("folder.create", parameters);
        (await SnapshotAsync()).Graph.Folders.Single().Metadata.Tenant.Should().Be(tenant);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/operations/{id}/submit")]
    [Endpoint("POST /api/v1/admin/proposals/{id}/approve")]
    public async Task FolderCreate_ProposalBecomesStale_ApprovalCannotOverwriteNewRevision()
    {
        var initial = await SnapshotAsync();
        var pending = await ProposeAsync("folder.create", new() { ["name"] = "Stale", ["If-Match"] = initial.Etag });
        await ApplyAsync("folder.create", new() { ["name"] = "Winner", ["If-Match"] = initial.Etag });
        var winner = await SnapshotAsync();
        await ApproveAsync(pending);
        (await _proposals.GetAsync(pending, CancellationToken.None))!.Status.Should().Be(OperationProposalStatus.Failed);
        var current = await SnapshotAsync();
        current.Etag.Should().Be(winner.Etag);
        current.Graph.Folders.Should().ContainSingle(folder => folder.Metadata.Name == "Winner");
    }

    [IntegrationTheory]
    [Endpoint("POST /api/v1/operations/{id}/submit")]
    [Endpoint("POST /api/v1/admin/proposals/{id}/approve")]
    [InlineData(null)]
    [InlineData("\"stale\"")]
    public async Task FolderCreate_InvalidPrecondition_DoesNotMutateSnapshot(string? etag)
    {
        var initial = await SnapshotAsync();
        using var response = await _proposer.PostAsJsonAsync("/api/v1/operations/folder.create/submit",
            new { parameters = new Dictionary<string, string?> { ["name"] = "Rejected", ["If-Match"] = etag } });
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        if (response.IsSuccessStatusCode && body.RootElement.GetProperty("data").TryGetProperty("proposalId", out var id)
            && id.ValueKind == JsonValueKind.String)
        {
            await ApproveAsync(id.GetString()!);
            (await _proposals.GetAsync(id.GetString()!, CancellationToken.None))!.Status.Should().Be(OperationProposalStatus.Failed);
        }
        else
        {
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            body.RootElement.GetProperty("data").GetProperty("status").GetString().Should().Be("Failed");
        }
        (await SnapshotAsync()).Etag.Should().Be(initial.Etag);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/operations/{id}/submit")]
    [Endpoint("POST /api/v1/admin/proposals/{id}/approve")]
    public async Task FolderCreate_ReadOnlyKey_CannotSubmitMutation()
    {
        var initial = await SnapshotAsync();
        using var reader = await CreateClientAsync("reader", ["admin:read"]);
        using var response = await reader.PostAsJsonAsync("/api/v1/operations/folder.create/submit",
            new { parameters = new Dictionary<string, string?> { ["name"] = "Denied", ["If-Match"] = initial.Etag } });
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden, await response.Content.ReadAsStringAsync());
        (await SnapshotAsync()).Etag.Should().Be(initial.Etag);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/operations/{id}/submit")]
    [Endpoint("POST /api/v1/admin/proposals/{id}/approve")]
    public async Task ServiceMove_AccessDeniedAfterSubmission_ApprovalCannotMutateMembership()
    {
        var initial = await SnapshotAsync();
        await ApplyAsync("folder.create", new() { ["name"] = "Destination", ["If-Match"] = initial.Etag });
        var before = await SnapshotAsync();
        var proposal = await ProposeAsync("service.move", new()
        {
            ["folderId"] = before.Graph.Folders.Single().Metadata.Id,
            ["If-Match"] = before.Etag
        });
        _authorization.DeniedService = "test";
        await ApproveAsync(proposal);
        (await _proposals.GetAsync(proposal, CancellationToken.None))!.Status.Should().Be(OperationProposalStatus.Failed);
        (await SnapshotAsync()).Etag.Should().Be(before.Etag);
        (await SnapshotAsync()).Graph.Services.Where(service => service.Metadata.Name == "test")
            .Should().OnlyContain(service => service.FolderId == null);
    }

    private sealed class RejectFolderMutationLoopbackHandler : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            var isFolderMutation = request.Method != HttpMethod.Get &&
                (path.StartsWith("/api/v1/admin/folders", StringComparison.Ordinal) ||
                 path.StartsWith("/api/v1/admin/services/", StringComparison.Ordinal) && path.EndsWith("/folder", StringComparison.Ordinal));
            if (isFolderMutation)
                throw new InvalidOperationException("Folder operations must use the shared mutation service.");
            return base.SendAsync(request, cancellationToken);
        }
    }

    private sealed class ReplayServiceAuthorization(IOperatorAuthorizationEvaluator evaluator) : IOperatorAuthorizationEvaluator
    {
        public string? DeniedService { get; set; }

        public Task<AccessDecision> EvaluateAsync(ClaimsPrincipal principal, OperatorAuthorizationRequest request,
            CancellationToken cancellationToken = default)
            => request.ResourceType == OperatorResourceType.PublishedService && request.ResourceId == DeniedService
                ? Task.FromResult(AccessDecision.Forbidden("Service access denied."))
                : evaluator.EvaluateAsync(principal, request, cancellationToken);
    }

    private async Task ApplyAsync(string operationId, Dictionary<string, string?> parameters)
    {
        var before = await SnapshotAsync();
        var proposal = await ProposeAsync(operationId, parameters);
        (await SnapshotAsync()).Etag.Should().Be(before.Etag, "submission must wait for separate approval");
        await ApproveAsync(proposal);
        var applied = (await _proposals.GetAsync(proposal, CancellationToken.None))!;
        applied.Status.Should().Be(OperationProposalStatus.Succeeded, string.Join("; ", applied.Plan.BlockingReasons));
        var after = await SnapshotAsync();
        after.Etag.Should().NotBe(before.Etag);
        using var replay = await _approver.Client.PostAsync($"/api/v1/admin/proposals/{proposal}/approve", null);
        replay.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await SnapshotAsync()).Etag.Should().Be(after.Etag, "duplicate approval cannot repeat a mutation");
    }

    private async Task<string> ProposeAsync(string operationId, Dictionary<string, string?> parameters)
    {
        var result = await SubmitAsync(operationId, parameters);
        var id = result.GetProperty("proposalId").GetString()!;
        var proposal = await _proposals.GetAsync(id, CancellationToken.None);
        proposal!.OperationId.Should().Be(operationId);
        proposal.Status.Should().Be(OperationProposalStatus.AwaitingApproval);
        return id;
    }

    private async Task<JsonElement> SubmitAsync(string operationId, Dictionary<string, string?> parameters)
    {
        using var response = await _proposer.PostAsJsonAsync($"/api/v1/operations/{operationId}/submit",
            new { serviceName = "test", parameters });
        var payload = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, payload);
        using var body = JsonDocument.Parse(payload);
        return body.RootElement.GetProperty("data").Clone();
    }

    private async Task ApproveAsync(string proposal)
    {
        using var response = await _approver.ApproveAsync(proposal);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
    }

    private async Task<HttpClient> CreateClientAsync(string name, string[] permissions)
    {
        var keys = _fixture.Services.GetRequiredService<IAdminApiKeyStore>();
        var issued = await keys.CreateAsync($"folder-{name}-{Guid.NewGuid():N}", permissions,
            DateTimeOffset.UtcNow.AddHours(1), "test", CancellationToken.None);
        return _fixture.CreateClient(client => client.DefaultRequestHeaders.Add("X-API-Key", issued.Key));
    }

    private async Task<MetadataV2GraphSnapshot> SnapshotAsync()
    {
        using var scope = _fixture.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IMetadataV2GraphStore>().GetCurrentAsync();
    }

    private static Dictionary<string, string?> ApprovalPolicy()
    {
        var settings = new Dictionary<string, string?> { ["Operations:Policy:Enabled"] = "true" };
        var operations = new[] { "folder.create", "folder.update", "folder.delete", "service.move", "service.publish" };
        for (var index = 0; index < operations.Length; index++)
        {
            settings[$"Operations:Policy:Rules:{index}:OperationId"] = operations[index];
            settings[$"Operations:Policy:Rules:{index}:Decision"] = "RequireApproval";
            settings[$"Operations:Policy:Rules:{index}:Reason"] = "separate-folder-review";
        }
        return settings;
    }
}
