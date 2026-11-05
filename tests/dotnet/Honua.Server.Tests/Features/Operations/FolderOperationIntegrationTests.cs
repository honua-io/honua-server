// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Security.Claims;
using Honua.Core.Features.Authorization.Abstractions;
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
public sealed class FolderOperationIntegrationTests(RedisFixture redis) : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new();
    private ReplayServiceAuthorization _authorization = null!;
    private HttpClient _proposer = null!;
    private HttpClient _approver = null!;
    private IOperationProposalStore _proposals = null!;

    public async Task InitializeAsync()
    {
        _fixture.UseKestrel().ConfigureWebHost(builder =>
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
            services.AddSingleton<IOperatorAuthorizationEvaluator>(provider =>
                _authorization = new ReplayServiceAuthorization(ActivatorUtilities.CreateInstance<OperatorAuthorizationEvaluator>(provider)));
        });
        await _fixture.InitializeAsync();
        await FolderPostgresTestHost.InstallSchemaAsync(_fixture);
        _proposer = await CreateClientAsync("proposer", ["admin:*"]);
        _approver = await CreateClientAsync("approver", ["admin:*"]);
        _proposals = _fixture.Services.GetRequiredService<IOperationProposalStore>();
    }

    public async Task DisposeAsync()
    {
        _proposer.Dispose();
        _approver.Dispose();
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
        await ApplyAsync("folder.create", new() { ["name"] = "Survey", ["If-Match"] = initial.Etag });
        var created = await SnapshotAsync();
        var folder = created.Graph.Folders.Should().ContainSingle().Subject;
        await ApplyAsync("service.move", new() { ["folderId"] = folder.Metadata.Id, ["If-Match"] = created.Etag });
        var moved = await SnapshotAsync();
        moved.Graph.Services.Where(service => service.Metadata.Name == "test")
            .Should().HaveCountGreaterThan(1).And.OnlyContain(service => service.FolderId == folder.Metadata.Id);
        await ApplyAsync("folder.update", new() { ["id"] = folder.Metadata.Id, ["name"] = "Survey2", ["If-Match"] = moved.Etag });
        var renamed = await SnapshotAsync();
        renamed.Graph.Folders.Single().PreviousNames.Should().Contain("Survey");
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
            ["name"] = "Owned", ["If-Match"] = initial.Etag, ["tenantId"] = "caller-supplied-tenant"
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
            ["folderId"] = before.Graph.Folders.Single().Metadata.Id, ["If-Match"] = before.Etag
        });
        _authorization.DeniedService = "test";
        await ApproveAsync(proposal);
        (await _proposals.GetAsync(proposal, CancellationToken.None))!.Status.Should().Be(OperationProposalStatus.Failed);
        (await SnapshotAsync()).Etag.Should().Be(before.Etag);
        (await SnapshotAsync()).Graph.Services.Where(service => service.Metadata.Name == "test")
            .Should().OnlyContain(service => service.FolderId == null);
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
        using var replay = await _approver.PostAsync($"/api/v1/admin/proposals/{proposal}/approve", null);
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
        using var response = await _approver.PostAsync($"/api/v1/admin/proposals/{proposal}/approve", null);
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
        var operations = new[] { "folder.create", "folder.update", "folder.delete", "service.move" };
        for (var index = 0; index < operations.Length; index++)
        {
            settings[$"Operations:Policy:Rules:{index}:OperationId"] = operations[index];
            settings[$"Operations:Policy:Rules:{index}:Decision"] = "RequireApproval";
            settings[$"Operations:Policy:Rules:{index}:Reason"] = "separate-folder-review";
        }
        return settings;
    }
}
