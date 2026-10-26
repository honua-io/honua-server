// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Collections.Concurrent;
using Honua.ControlPlane;
using Honua.Core.Features.AuditLog;
using Honua.Core.Features.AuditLog.Abstractions;
using Microsoft.Extensions.Logging;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using FluentAssertions.Execution;
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
    private LookupSwitchStore _lookup = null!;
    private readonly ConcurrentQueue<string> _audit = new();
    private readonly RecordingLogs _logs = new();
    private string _denialAuditMode = "ok";
    private const string Canary = "synthetic-private-store-error-canary";
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
            var keyDescriptor = services.Last(descriptor => descriptor.ServiceType == typeof(IAdminApiKeyStore));
            services.RemoveAll<IAdminApiKeyStore>();
            services.AddSingleton<IAdminApiKeyStore>(provider => _lookup = new LookupSwitchStore(
                ResolveDescriptor<IAdminApiKeyStore>(provider, keyDescriptor)));
            var auditDescriptor = services.Last(descriptor => descriptor.ServiceType == typeof(IAuditLog));
            services.RemoveAll<IAuditLog>();
            services.AddScoped<IAuditLog>(provider => new CapturingAudit(
                ResolveDescriptor<IAuditLog>(provider, auditDescriptor), _audit, () => _denialAuditMode));
            services.AddLogging(logging => logging.AddProvider(_logs));
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
        var persisted = await _proposals.GetAsync(proposal.ProposalId, CancellationToken.None);
        using var assertions = new AssertionScope();
        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        _executor.Calls.Should().Be(0);
        persisted!.Status.Should().Be(OperationProposalStatus.AwaitingApproval);
        await AssertDenialAuditAsync(proposal, approver.Record.Id.ToString("D"), "current_authority");
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
        _executor.LastPrincipal.Should().Be($"apikey:api-key:{proposer.Record.Id:D}");
        using HttpResponseMessage replay = await Client(approver.Key).PostAsync(
            $"/api/v1/admin/proposals/{proposal.ProposalId}/approve", null);
        replay.StatusCode.Should().Be(HttpStatusCode.Conflict);
        _executor.Calls.Should().Be(1);
        (await ReadAuditsAsync(proposal.ProposalId)).Should()
            .NotContain(entry => entry.Action == "operation.approval_denied");
    }

    [IntegrationTheory]
    [Endpoint("POST /api/v1/admin/proposals/{id}/approve")]
    [InlineData("missing")]
    [InlineData("expired")]
    [InlineData("read-only")]
    [InlineData("outage")]
    public async Task CurrentKeyLookupDenies_ApprovalCannotUseSealedOldAuthority(string state)
    {
        var proposer = await CreateKeyAsync("changed-proposer");
        var approver = await CreateKeyAsync("changed-approver");
        OperationProposal proposal = await SubmitAsync(proposer);
        _lookup.Target = proposer.Record.Id;
        _lookup.Transform = record => state switch
        {
            "missing" => null,
            "expired" => record! with { ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1) },
            "read-only" => record! with { Permissions = ["admin:read"] },
            _ => record
        };
        if (state == "outage") _lookup.OnRead = (_, _) => throw new InvalidOperationException(Canary);
        using HttpResponseMessage response = await Client(approver.Key).PostAsync(
            $"/api/v1/admin/proposals/{proposal.ProposalId}/approve", null);
        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await response.Content.ReadAsStringAsync()).Should().NotContain(Canary);
        _executor.Calls.Should().Be(0);
        (await _proposals.GetAsync(proposal.ProposalId, CancellationToken.None))!.Status
            .Should().Be(OperationProposalStatus.AwaitingApproval);
        await AssertDenialAuditAsync(proposal, approver.Record.Id.ToString("D"), "current_authority");
        _audit.Should().OnlyContain(message => !message.Contains(Canary));
        _logs.Messages.Should().OnlyContain(message => !message.Contains(Canary));
    }

    [IntegrationTheory]
    [Endpoint("POST /api/v1/admin/proposals/{id}/approve")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AfterClaimAuthorityLoss_FailsDurablyBeforeEffectWithoutRawErrors(bool outage)
    {
        var proposer = await CreateKeyAsync("claim-proposer");
        var approver = await CreateKeyAsync("claim-approver");
        OperationProposal proposal = await SubmitAsync(proposer);
        _lookup.Target = proposer.Record.Id;
        _lookup.OnRead = async (count, token) =>
        {
            if (count != 2) return;
            (await _proposals.GetAsync(proposal.ProposalId, token))!.Status.Should().Be(OperationProposalStatus.Executing);
            if (outage) throw new InvalidOperationException(Canary);
            await _keys.RevokeAsync(proposer.Record.Id, token);
        };
        using HttpResponseMessage response = await Client(approver.Key).PostAsync(
            $"/api/v1/admin/proposals/{proposal.ProposalId}/approve", null);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        _lookup.Reads.Should().Be(2);
        _executor.Calls.Should().Be(0);
        var persisted = (await _proposals.GetAsync(proposal.ProposalId, CancellationToken.None))!;
        persisted.Status.Should().Be(OperationProposalStatus.Failed);
        (await response.Content.ReadAsStringAsync()).Should().NotContain(Canary);
        JsonSerializer.Serialize(persisted).Should().NotContain(Canary);
        _logs.Messages.Should().Contain(message => message.Contains(OperationProposerApiKeyAuthority.Refusal));
        _logs.Messages.Should().OnlyContain(message => !message.Contains(Canary));
        _audit.Should().NotBeEmpty();
        _audit.Should().OnlyContain(message => !message.Contains(Canary));
        var audits = await ReadAuditsAsync(proposal.ProposalId);
        audits.Should().ContainSingle(entry => entry.Action == "operation.apply_failed");
        audits.Should().NotContain(entry => entry.Action == "operation.approval_denied");
    }

    [IntegrationTest]
    public async Task AfterClaimCallerCancellation_CompensatesWithoutEffect()
    {
        var proposer = await CreateKeyAsync("cancel-proposer");
        OperationProposal proposal = await SubmitAsync(proposer);
        using var cancellation = new CancellationTokenSource();
        _lookup.Target = proposer.Record.Id;
        _lookup.OnRead = async (count, token) =>
        {
            if (count != 2) return;
            (await _proposals.GetAsync(proposal.ProposalId, token))!.Status.Should().Be(OperationProposalStatus.Executing);
            cancellation.Cancel();
            token.ThrowIfCancellationRequested();
        };
        // The real route created the proposal above; direct gateway cancellation lets the
        // test retain an observer after the caller's cancellation, rather than assert on a closed socket.
        var gateway = _fixture.Services.GetRequiredService<IOperationGateway>();
        var act = () => gateway.ApplyApprovedProposalAsync(proposal.ProposalId, "separate-cancellation-test-approver", cancellation.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
        _executor.Calls.Should().Be(0);
        (await _proposals.GetAsync(proposal.ProposalId, CancellationToken.None))!.Status
            .Should().Be(OperationProposalStatus.Cancelled);
        (await ReadAuditsAsync(proposal.ProposalId)).Should()
            .NotContain(entry => entry.Action == "operation.approval_denied");
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/proposals/{id}/approve")]
    public async Task SameProposer_CannotApproveTypedProposal()
    {
        var proposer = await CreateKeyAsync("self-proposer");
        OperationProposal proposal = await SubmitAsync(proposer);
        using HttpResponseMessage response = await Client(proposer.Key).PostAsync(
            $"/api/v1/admin/proposals/{proposal.ProposalId}/approve", null);
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        _executor.Calls.Should().Be(0);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/proposals/{id}/approve")]
    public async Task ExistingRotationSemantics_ActiveSameIdCanStillBeApproved()
    {
        var proposer = await CreateKeyAsync("rotate-proposer");
        var approver = await CreateKeyAsync("rotate-approver");
        OperationProposal proposal = await SubmitAsync(proposer);
        var rotated = await _keys.RotateAsync(proposer.Record.Id, CancellationToken.None);
        rotated.Should().NotBeNull();
        rotated!.Record.Id.Should().Be(proposer.Record.Id);
        using HttpResponseMessage response = await Client(approver.Key).PostAsync(
            $"/api/v1/admin/proposals/{proposal.ProposalId}/approve", null);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
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

    [IntegrationTheory]
    [InlineData("null")]
    [InlineData("throw")]
    [Endpoint("POST /api/v1/admin/proposals/{id}/approve")]
    public async Task CurrentAuthorityRefused_AuditUnavailable_ReturnsSafe503BeforeClaim(string mode)
    {
        var proposer = await CreateKeyAsync("audit-unavailable-proposer");
        var approver = await CreateKeyAsync("audit-unavailable-approver");
        var proposal = await SubmitAsync(proposer);
        await _keys.RevokeAsync(proposer.Record.Id, CancellationToken.None);
        _denialAuditMode = mode;
        using var response = await Client(approver.Key).PostAsync(
            $"/api/v1/admin/proposals/{proposal.ProposalId}/approve", null);
        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable, await response.Content.ReadAsStringAsync());
        (await response.Content.ReadAsStringAsync()).Should().NotContain(Canary);
        _executor.Calls.Should().Be(0);
        (await _proposals.GetAsync(proposal.ProposalId, CancellationToken.None))!.Status
            .Should().Be(OperationProposalStatus.AwaitingApproval);
        (await ReadAuditsAsync(proposal.ProposalId)).Should()
            .NotContain(entry => entry.Action == "operation.approval_denied");
        _logs.Messages.Should().OnlyContain(message => !message.Contains(Canary));
    }

    private async Task<IReadOnlyList<AuditEventRecord>> ReadAuditsAsync(string proposalId)
    {
        await using var scope = _fixture.Services.CreateAsyncScope();
        var page = await scope.ServiceProvider.GetRequiredService<IAuditLogReader>().ListAsync(
            new AuditLogFilter { ResourceId = proposalId, PageSize = 20 });
        return page.Items;
    }

    private async Task AssertDenialAuditAsync(OperationProposal proposal, string actor, string reason)
    {
        var denial = (await ReadAuditsAsync(proposal.ProposalId)).Should()
            .ContainSingle(entry => entry.Action == "operation.approval_denied").Subject;
        denial.Actor.Should().Be(actor);
        denial.CorrelationId.Should().Be(proposal.Audit.CorrelationId ?? proposal.ProposalId);
        denial.Outcome.Should().Be(AuditOutcome.Denied);
        using var details = JsonDocument.Parse(denial.Details);
        details.RootElement.GetProperty("reason").GetString().Should().Be(reason);
        JsonSerializer.Serialize(denial).Should().NotContain(Canary);
    }

    private async Task<OperationProposal> SubmitAsync(AdminApiKeyCreateResult proposer)
    {
        using HttpResponseMessage response = await Client(proposer.Key).PostAsJsonAsync(
            "/api/v1/operations/service.publish/submit", new
            {
                connectionId = Guid.NewGuid().ToString("D"),
                serviceName = "synthetic",
                parameters = new { schema = "public", table = "synthetic", layerName = "Synthetic" }
            });
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        string id = body.RootElement.GetProperty("data").GetProperty("proposalId").GetString()!;
        OperationProposal proposal = (await _proposals.GetAsync(id, CancellationToken.None))!;
        proposal.Evidence.Should().BeNull("this is the actual typed REST mapper, without model evidence");
        proposal.RequestedBy.Should().Be($"apikey:api-key:{proposer.Record.Id:D}");
        proposal.Status.Should().Be(OperationProposalStatus.AwaitingApproval);
        proposal.OperationId.Should().Be("service.publish");
        proposal.SealedPlanHash.Should().NotBeNullOrWhiteSpace();
        _executor.Calls.Should().Be(0);
        return proposal;
    }

    private static T ResolveDescriptor<T>(IServiceProvider provider, ServiceDescriptor descriptor) where T : class
        => descriptor.ImplementationInstance as T ?? descriptor.ImplementationFactory?.Invoke(provider) as T
            ?? (T)ActivatorUtilities.CreateInstance(provider, descriptor.ImplementationType!);

    private sealed class LookupSwitchStore(IAdminApiKeyStore inner) : IAdminApiKeyStore
    {
        internal Guid Target { get; set; }
        internal int Reads { get; private set; }
        internal Func<int, CancellationToken, Task>? OnRead { get; set; }
        internal Func<AdminApiKeyRecord?, AdminApiKeyRecord?>? Transform { get; set; }
        public async Task<AdminApiKeyRecord?> GetAsync(Guid id, CancellationToken token)
        {
            if (id != Target) return await inner.GetAsync(id, token);
            Reads++;
            if (OnRead is { } callback) await callback(Reads, token);
            var record = await inner.GetAsync(id, token);
            return Transform is { } transform ? transform(record) : record;
        }
        public Task<IReadOnlyList<AdminApiKeyRecord>> ListAsync(CancellationToken token) => inner.ListAsync(token);
        public Task<AdminApiKeyCreateResult> CreateAsync(string name, IReadOnlyList<string> permissions, DateTimeOffset? expires,
            string? createdBy, CancellationToken token) => inner.CreateAsync(name, permissions, expires, createdBy, token);
        public Task<AdminApiKeyCreateResult> CreateAsync(string name, IReadOnlyList<string> permissions, DateTimeOffset? expires,
            string? createdBy, AdminApiKeyRestrictions? restrictions, CancellationToken token) =>
            inner.CreateAsync(name, permissions, expires, createdBy, restrictions, token);
        public Task<AdminApiKeyCreateResult?> RotateAsync(Guid id, CancellationToken token) => inner.RotateAsync(id, token);
        public Task<AdminApiKeyRecord?> RevokeAsync(Guid id, CancellationToken token) => inner.RevokeAsync(id, token);
        public Task<AdminApiKeyValidationResult?> ValidateAsync(string key, CancellationToken token) => inner.ValidateAsync(key, token);
    }

    private sealed class CapturingAudit(IAuditLog inner, ConcurrentQueue<string> events, Func<string> mode) : IAuditLog
    {
        public bool IsPersisted => inner.IsPersisted;
        public Task<string?> RecordAsync(AuditEvent entry, CancellationToken token = default)
        {
            events.Enqueue(JsonSerializer.Serialize(entry));
            if (entry.Action == "operation.approval_denied")
            {
                if (mode() == "throw") throw new InvalidOperationException(Canary);
                if (mode() == "null") return Task.FromResult<string?>(null);
            }
            return inner.RecordAsync(entry, token);
        }
    }

    private sealed class RecordingLogs : ILoggerProvider
    {
        internal ConcurrentQueue<string> Messages { get; } = new();
        public ILogger CreateLogger(string categoryName) => new Recorder(Messages);
        public void Dispose() { }
        private sealed class Recorder(ConcurrentQueue<string> messages) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
                => messages.Enqueue(formatter(state, exception) + " " + exception);
        }
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
                OperationId = OperationId,
                OperationInstanceId = context.OperationInstanceId!,
                CorrelationId = context.CorrelationId!,
                Status = OperationHandleStatus.Completed,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            });
        }
        public Task<OperationStatus> GetStatusAsync(OperationHandle handle, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}
