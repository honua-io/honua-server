// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Operations.Domain;
using Honua.Infrastructure.Authentication;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using CanonicalExecutor = Honua.Core.Features.Operations.Abstractions.IOperationExecutor;

namespace Honua.Server.Tests.Features.OperationsToolset;

/// <summary>Real authenticated REST proposal/replay with only the final no-cloud actuator substituted.</summary>
[Collection(RedisFixture.CollectionName)]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.TestInfrastructure)]
public sealed class TypedProposalCurrentApiKeyTests(RedisFixture redis) : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new();
    private readonly CountingPublishExecutor _executor = new();
    private readonly List<HttpClient> _clients = [];
    private IAdminApiKeyStore _keys = null!;
    private IOperationProposalStore _proposals = null!;

    public async Task InitializeAsync()
    {
        _fixture.ConfigureWebHost(builder =>
        {
            builder.UseEnvironment("Test");
            builder.UseSetting("ConnectionStrings:redis", redis.ConnectionString);
            builder.UseSetting("HONUA_DEV_AUTH", "false");
            builder.UseSetting("HONUA_ADMIN_PASSWORD", WebAppFixture.SharedAdminPassword);
            builder.UseSetting("Licensing:DevGrantEdition", "Pro");
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Operations:Policy:Enabled"] = "true",
                    ["Operations:Policy:Rules:0:OperationId"] = "service.publish",
                    ["Operations:Policy:Rules:0:Decision"] = "RequireApproval",
                    ["Operations:Policy:Rules:0:Reason"] = "test-separate-review"
                }));
        }).ConfigureServices(services =>
        {
            // The real descriptor, policy, mapper, dispatcher, durable stores, gateway,
            // authentication and approval routes remain composed. Never publish a cloud asset.
            services.RemoveAll<CanonicalExecutor>();
            services.AddSingleton<CanonicalExecutor>(_executor);
        });
        await _fixture.InitializeAsync();
        _keys = _fixture.Services.GetRequiredService<IAdminApiKeyStore>();
        _proposals = _fixture.Services.GetRequiredService<IOperationProposalStore>();
    }

    public async Task DisposeAsync()
    {
        foreach (HttpClient client in _clients) client.Dispose();
        await _fixture.DisposeAsync();
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/operations/{id}/submit")]
    [Endpoint("POST /api/v1/admin/proposals/{id}/approve")]
    public async Task SubmitThenRevoke_SeparateApprovalRefusesBeforeTypedEffect()
    {
        var proposer = await CreateKeyAsync("proposer");
        var approver = await CreateKeyAsync("approver");
        OperationProposal proposal = await SubmitAsync(proposer);
        await _keys.RevokeAsync(proposer.Record.Id, CancellationToken.None);
        using HttpResponseMessage response = await Client(approver.Key).PostAsync(
            $"/api/v1/admin/proposals/{proposal.ProposalId}/approve", null);
        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        _executor.Calls.Should().Be(0);
        var persisted = await _proposals.GetAsync(proposal.ProposalId, CancellationToken.None);
        persisted!.Status.Should().Be(OperationProposalStatus.AwaitingApproval);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/operations/{id}/submit")]
    [Endpoint("POST /api/v1/admin/proposals/{id}/approve")]
    public async Task CurrentKey_SeparateApprovalExecutesOnceWithOriginalPrincipal()
    {
        var proposer = await CreateKeyAsync("current-proposer");
        var approver = await CreateKeyAsync("current-approver");
        OperationProposal proposal = await SubmitAsync(proposer);
        using HttpResponseMessage first = await Client(approver.Key).PostAsync(
            $"/api/v1/admin/proposals/{proposal.ProposalId}/approve", null);
        first.StatusCode.Should().Be(HttpStatusCode.OK, await first.Content.ReadAsStringAsync());
        _executor.Calls.Should().Be(1);
        _executor.LastPrincipal.Should().Be($"ApiKey:api-key:{proposer.Record.Id:D}");
        using HttpResponseMessage replay = await Client(approver.Key).PostAsync(
            $"/api/v1/admin/proposals/{proposal.ProposalId}/approve", null);
        replay.StatusCode.Should().Be(HttpStatusCode.Conflict);
        _executor.Calls.Should().Be(1);
    }

    private Task<AdminApiKeyCreateResult> CreateKeyAsync(string name) =>
        _keys.CreateAsync($"{name}-{Guid.NewGuid():N}", ["admin:*"], DateTimeOffset.UtcNow.AddHours(1), "test", CancellationToken.None);

    private HttpClient Client(string key)
    {
        HttpClient client = _fixture.CreateClient(value => value.DefaultRequestHeaders.Add("X-API-Key", key));
        _clients.Add(client);
        return client;
    }

    private async Task<OperationProposal> SubmitAsync(AdminApiKeyCreateResult proposer)
    {
        using HttpResponseMessage response = await Client(proposer.Key).PostAsJsonAsync(
            "/api/v1/operations/service.publish/submit", new
            {
                connectionId = Guid.NewGuid().ToString("D"), serviceName = "synthetic",
                parameters = new { schema = "public", table = "synthetic", layerName = "Synthetic" }
            });
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        string id = body.RootElement.GetProperty("data").GetProperty("proposalId").GetString()!;
        OperationProposal proposal = (await _proposals.GetAsync(id, CancellationToken.None))!;
        proposal.Evidence.Should().BeNull("this is the actual typed REST mapper, without model evidence");
        proposal.RequestedBy.Should().Be($"ApiKey:api-key:{proposer.Record.Id:D}");
        proposal.Status.Should().Be(OperationProposalStatus.AwaitingApproval);
        proposal.OperationId.Should().Be("service.publish");
        proposal.SealedPlanHash.Should().NotBeNullOrWhiteSpace();
        _executor.Calls.Should().Be(0);
        return proposal;
    }

    private sealed class CountingPublishExecutor : CanonicalExecutor
    {
        private int _calls;
        public string OperationId => "service.publish";
        internal int Calls => Volatile.Read(ref _calls);
        internal string? LastPrincipal { get; private set; }
        public Task<OperationValidation> ValidateAsync(OperationRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(new OperationValidation { IsValid = true, Status = "valid" });
        public Task<OperationHandle> SubmitAsync(OperationRequest request, OperationPolicyContext context,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _calls);
            LastPrincipal = context.PrincipalId;
            return Task.FromResult(new OperationHandle
            {
                OperationId = OperationId, OperationInstanceId = context.OperationInstanceId!, CorrelationId = context.CorrelationId!,
                Status = OperationHandleStatus.Completed, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
            });
        }
        public Task<OperationStatus> GetStatusAsync(OperationHandle handle, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}
