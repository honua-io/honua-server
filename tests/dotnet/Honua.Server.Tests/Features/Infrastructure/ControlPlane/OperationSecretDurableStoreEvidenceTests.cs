// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.AuditLog.Abstractions;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Guardrails.Abstractions;
using Honua.Core.Features.Guardrails.Domain;
using Honua.Core.Features.Licensing.Domain;
using Honua.Core.Features.Operations.Abstractions;
using Honua.Core.Features.Operations.Domain;
using Honua.Core.Features.Operations.Services;
using Honua.ControlPlane;
using Honua.Server.Features.Operations;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using StackExchange.Redis;

namespace Honua.Server.Tests.Features.Infrastructure.ControlPlane;

/// <summary>
/// Issue #4187 acceptance evidence. Drives the production dispatcher, approval bridge, gateway and
/// executor over the real Redis-backed instance, proposal and secret stores, then sweeps every key
/// the run wrote — plus every audit event the dispatcher recorded — for the plaintext credential.
/// The sweep reads raw Redis values rather than the typed models, so a leak through any field,
/// index or nested payload fails the test.
/// </summary>
[Collection(RedisFixture.CollectionName)]
[Protocol(TestProtocols.Infrastructure)]
[Operation(Operations.TestInfrastructure)]
public sealed class OperationSecretDurableStoreEvidenceTests(RedisFixture redis)
{
    /// <summary>Secret-returning access operations and the response field carrying the one-time value.</summary>
    private static readonly string RotateKeyId = Guid.NewGuid().ToString();

    private static readonly (string OperationId, string SecretField, string? RouteId)[] SecretOutputOperations =
    [
        ("admin.api-key.create", "key", null),
        ("admin.api-key.rotate", "key", RotateKeyId),
        ("admin.oauth-client.register", "clientSecret", null),
    ];

    [IntegrationTest]
    public async Task ActuatedSecretOutput_LeavesNoPlaintext_InRedisInstanceStoreOrAuditLog()
    {
        await using var multiplexer = await ConnectionMultiplexer.ConnectAsync(redis.ConnectionString);
        foreach (var (operationId, secretField, routeId) in SecretOutputOperations)
        {
            var secret = $"plaintext-{Guid.NewGuid():N}";
            var instanceStore = new RedisOperationInstanceStore(multiplexer);
            var secretStore = BuildRedisSecretStore(multiplexer);
            var audit = new RecordingAuditLog();
            var keysBefore = SnapshotKeys(multiplexer);

            using var client = new HttpClient(new StubAdminApi(secret, secretField));
            var executor = BuildAccessExecutor(operationId, client, secretStore);
            var dispatcher = new OperationDispatcher(
                new OperationCatalog([new AdminAccessOperationDescriptorProvider()], TimeProvider.System),
                [executor],
                new AllowPolicyDecisionPoint(),
                TimeProvider.System,
                approvalBridge: null,
                instanceStore: instanceStore,
                auditLog: audit);

            var parameters = new Dictionary<string, string?> { ["name"] = "automation" };
            if (routeId is not null)
            {
                parameters["id"] = routeId;
            }

            var handle = await dispatcher.SubmitAsync(
                new OperationRequest { OperationId = operationId, Parameters = parameters },
                new OperationPolicyContext { PrincipalId = "principal-4187", TenantId = "tenant-4187" },
                CancellationToken.None);

            handle.Status.Should().Be(OperationHandleStatus.Completed, "reason: {0}", handle.Reason);

            // The durable handle exposes an opaque reference, never the credential.
            var persisted = await instanceStore.GetAsync(handle.OperationInstanceId);
            persisted.Should().NotBeNull();
            var reference = persisted!.Result!.SecretReferences.Should().ContainSingle().Subject;
            reference.Name.Should().Be(secretField);

            AssertNoPlaintextInNewRedisKeys(multiplexer, keysBefore, secret);
            audit.Events.Should().NotBeEmpty();
            JsonSerializer.Serialize(audit.Events).Should().NotContain(secret);

            // The one-time channel still delivers the credential exactly once, then fails closed.
            secretStore.Consume(reference, handle.OperationInstanceId, operationId, "principal-4187", "tenant-4187")
                .Should().Be(secret);
            secretStore.Consume(reference, handle.OperationInstanceId, operationId, "principal-4187", "tenant-4187")
                .Should().BeNull();
        }
    }

    [IntegrationTest]
    public async Task ApprovedSecretInput_LeavesNoPlaintext_InRedisProposalStoreOrAuditLog()
    {
        await using var multiplexer = await ConnectionMultiplexer.ConnectAsync(redis.ConnectionString);
        foreach (var (operationId, routeId) in new[]
                 {
                     ("admin.oidc-provider.create", (string?)null),
                     ("admin.oidc-provider.update", Guid.NewGuid().ToString()),
                 })
        {
            var secret = $"plaintext-{Guid.NewGuid():N}";
            var proposalStore = new RedisOperationProposalStore(
                multiplexer,
                NullLogger<RedisOperationProposalStore>.Instance);
            var secretStore = BuildRedisSecretStore(multiplexer);
            var audit = new RecordingAuditLog();
            var definition = AdminAccessOperationCatalog.Definitions.Single(item => item.OperationId == operationId);
            var descriptor = AdminAccessOperationCatalog.Descriptors.Single(item => item.OperationId == operationId);
            var keysBefore = SnapshotKeys(multiplexer);

            // The gateway resolves its audit sink from this scope, so the recorder captures every
            // audit event the proposal path writes.
            var instanceStore = new RedisOperationInstanceStore(multiplexer);
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<IAuditLog>(audit);
            services.AddSingleton<IOperationInstanceStore>(instanceStore);
            var gateway = new OperationGateway(
                new ApprovalGuardrailLadder(),
                proposalStore,
                [],
                services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
                new NullProposalNotifier(),
                NullLogger<OperationGateway>.Instance);

            // The gateway joins a sealed proposal to an already-accepted durable instance.
            var operationInstanceId = $"opinst-{Guid.NewGuid():N}";
            (await instanceStore.TryCreateAsync(new OperationHandle
            {
                OperationInstanceId = operationInstanceId,
                OperationId = operationId,
                CorrelationId = $"corr-{Guid.NewGuid():N}",
                Status = OperationHandleStatus.Accepted,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            })).Should().BeTrue();
            var context = new OperationPolicyContext
            {
                OperationInstanceId = operationInstanceId,
                CorrelationId = $"corr-{Guid.NewGuid():N}",
                PrincipalId = "principal-4187",
                TenantId = "tenant-4187",
            };
            var parameters = new Dictionary<string, string?>
            {
                ["name"] = "issuer",
                ["providerType"] = "Generic",
                ["authority"] = "https://issuer.example.test",
                ["clientId"] = "console",
                ["clientSecret"] = secret,
            };
            if (routeId is not null)
            {
                parameters["id"] = routeId;
            }

            // Production mapper decides what a proposal carries; the production gateway seals and
            // persists it through the real 30-day Redis proposal store.
            var gatewayRequest = new AdminOperateOperationApprovalRequestMapper(definition, secretStore).Map(
                descriptor,
                new OperationRequest { OperationId = operationId, Parameters = parameters },
                context,
                new PolicyDecision { Kind = PolicyDecisionKind.RequireApproval });
            var result = await gateway.CreateApprovalProposalAsync(
                operationInstanceId,
                gatewayRequest with
                {
                    OperationId = operationId,
                    TenantId = context.TenantId,
                    OperationInstanceId = operationInstanceId,
                    CorrelationId = context.CorrelationId,
                });

            result.Outcome.Should().Be(OperationGatewayOutcome.ProposalCreated, "message: {0}", result.Message);

            // The proposal that a 30-day TTL retains, and that replay reads, holds only a reference.
            var proposal = await proposalStore.GetAsync(result.ProposalId!);
            proposal.Should().NotBeNull();
            JsonSerializer.Serialize(proposal).Should().NotContain(secret);

            AssertNoPlaintextInNewRedisKeys(multiplexer, keysBefore, secret);
            audit.Events.Should().NotBeEmpty();
            JsonSerializer.Serialize(audit.Events).Should().NotContain(secret);
        }
    }

    private static RedisOperationSecretStore BuildRedisSecretStore(ConnectionMultiplexer multiplexer)
    {
        var services = new ServiceCollection();
        services.AddDataProtection().SetApplicationName("Honua.Tests.OperationSecretEvidence");
        return new RedisOperationSecretStore(
            multiplexer,
            services.BuildServiceProvider().GetRequiredService<IDataProtectionProvider>());
    }

    private static AdminOperateOperationExecutor BuildAccessExecutor(
        string operationId,
        HttpClient client,
        IOperationSecretStore secretStore)
    {
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(AdminOperateOperationExecutor.HttpClientName).Returns(client);
        var context = new DefaultHttpContext();
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("public.example.test");
        context.Connection.LocalPort = 8080;
        context.Request.Headers["X-API-Key"] = "test-key";
        var accessor = Substitute.For<IHttpContextAccessor>();
        accessor.HttpContext.Returns(context);
        return new AdminOperateOperationExecutor(
            AdminAccessOperationCatalog.Definitions.Single(item => item.OperationId == operationId),
            AdminAccessOperationCatalog.Descriptors.Single(item => item.OperationId == operationId),
            factory,
            accessor,
            null,
            TimeProvider.System,
            new OperationLineageAttestationStore(TimeProvider.System),
            secretStore);
    }

    private static HashSet<string> SnapshotKeys(ConnectionMultiplexer multiplexer)
    {
        var server = multiplexer.GetServer(multiplexer.GetEndPoints().Single());
        return server.Keys(pattern: "*").Select(key => key.ToString()).ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>
    /// Reads every key this run created straight out of Redis and fails on any raw occurrence of the
    /// credential, regardless of which model or index wrote it.
    /// </summary>
    private static void AssertNoPlaintextInNewRedisKeys(
        ConnectionMultiplexer multiplexer,
        HashSet<string> keysBefore,
        string secret)
    {
        var server = multiplexer.GetServer(multiplexer.GetEndPoints().Single());
        var database = multiplexer.GetDatabase();
        var inspected = 0;
        foreach (var key in server.Keys(pattern: "*").Where(key => !keysBefore.Contains(key.ToString())))
        {
            inspected++;
            var values = database.KeyType(key) switch
            {
                RedisType.Hash => database.HashGetAll(key).Select(entry => entry.Value.ToString()),
                RedisType.List => database.ListRange(key).Select(value => value.ToString()),
                RedisType.Set => database.SetMembers(key).Select(value => value.ToString()),
                RedisType.SortedSet => database.SortedSetRangeByRank(key).Select(value => value.ToString()),
                _ => [database.StringGet(key).ToString()],
            };
            foreach (var value in values)
            {
                value.Should().NotContain(secret, $"durable key '{key}' must not retain secret material");
            }
        }

        inspected.Should().BeGreaterThan(0, "the run must have written durable state to sweep");
    }

    private sealed class StubAdminApi(string secret, string secretField) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = new StringContent(
                    $"{{\"data\":{{\"id\":\"resource-id\",\"{secretField}\":\"{secret}\"}}}}"),
            });
    }

    private sealed class RecordingAuditLog : IAuditLog
    {
        public List<AuditEvent> Events { get; } = [];

        public Task<string?> RecordAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default)
        {
            Events.Add(auditEvent);
            return Task.FromResult<string?>($"audit-{Guid.NewGuid():N}");
        }
    }

    private sealed class AllowPolicyDecisionPoint : IOperationPolicyDecisionPoint
    {
        public Task<PolicyDecision> EvaluateAsync(
            IOperationDescriptor descriptor,
            OperationRequest request,
            OperationPolicyContext context,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new PolicyDecision { Kind = PolicyDecisionKind.Allow });
    }

    private sealed class ApprovalGuardrailLadder : IGuardrailLadder
    {
        public GuardrailDecision Resolve(OperationClass operationClass)
            => Resolve(operationClass, HonuaEdition.Enterprise);

        public GuardrailDecision Resolve(OperationClass operationClass, HonuaEdition edition)
            => new(GuardrailTier.RequiresApproval, operationClass, edition, "secret-evidence-test");

        public GuardrailDecision Resolve(OperationClass operationClass, string? actionDiscriminator)
            => Resolve(operationClass, actionDiscriminator, HonuaEdition.Enterprise);

        public GuardrailDecision Resolve(
            OperationClass operationClass,
            string? actionDiscriminator,
            HonuaEdition edition)
            => new(GuardrailTier.RequiresApproval, operationClass, edition, "secret-evidence-test");
    }

    private sealed class NullProposalNotifier : IProposalNotifier
    {
        public Task NotifyPendingAsync(OperationProposal proposal, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task NotifyResolvedAsync(OperationProposal proposal, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }
}
