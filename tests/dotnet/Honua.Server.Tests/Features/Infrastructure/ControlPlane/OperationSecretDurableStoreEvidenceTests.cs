// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

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

namespace Honua.Server.Tests.Features.Infrastructure.ControlPlane;

// Drives the production dispatcher, approval bridge, gateway and
// executor over the real PostgreSQL instance, proposal and secret stores, then scans every stored row
// the run wrote — plus every audit event the dispatcher recorded — for the plaintext credential.
// The scan reads stored rows as text rather than the typed models, so a leak through any field,
// index or nested payload fails the test.
[Collection("Database")]
[Protocol(TestProtocols.Infrastructure)]
[Operation(Operations.TestInfrastructure)]
public sealed partial class OperationSecretDurableStoreEvidenceTests
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
    public async Task ActuatedSecretOutput_LeavesNoPlaintext_InTheInstanceStoreOrAuditLog()
    {
        var state = await SharedControlPlaneState.GetAsync();
        foreach (var (operationId, secretField, routeId) in SecretOutputOperations)
        {
            var secret = $"plaintext-{Guid.NewGuid():N}";
            var instanceStore = state.CreateInstanceStore();
            var secretStore = BuildSecretStore(state);
            var audit = new RecordingAuditLog();

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

            await AssertNoStoredPlaintextAsync(state, secret);
            audit.Events.Should().NotBeEmpty();
            JsonSerializer.Serialize(audit.Events).Should().NotContain(secret);

            // The one-time channel still delivers the credential exactly once, then fails closed.
            secretStore.Consume(reference, new OperationSecretBinding(handle.OperationInstanceId, operationId, "principal-4187", "tenant-4187"))
                .Should().Be(secret);
            secretStore.Consume(reference, new OperationSecretBinding(handle.OperationInstanceId, operationId, "principal-4187", "tenant-4187"))
                .Should().BeNull();
        }
    }

    [IntegrationTest]
    public async Task ApprovedSecretInput_LeavesNoPlaintext_InTheProposalStoreOrAuditLog()
    {
        var state = await SharedControlPlaneState.GetAsync();
        foreach (var (operationId, routeId) in new[]
                 {
                     ("admin.oidc-provider.create", (string?)null),
                     ("admin.oidc-provider.update", Guid.NewGuid().ToString()),
                 })
        {
            var secret = $"plaintext-{Guid.NewGuid():N}";
            var proposalStore = state.CreateProposalStore();
            var secretStore = BuildSecretStore(state);
            var audit = new RecordingAuditLog();
            var definition = AdminAccessOperationCatalog.Definitions.Single(item => item.OperationId == operationId);
            var descriptor = AdminAccessOperationCatalog.Descriptors.Single(item => item.OperationId == operationId);

            // The gateway resolves its audit sink from this scope, so the recorder captures every
            // audit event the proposal path writes.
            var instanceStore = state.CreateInstanceStore();
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<IAuditLog>(audit);
            services.AddSingleton<IOperationInstanceStore>(instanceStore);
            var gateway = new OperationGateway(
                new ApprovalGuardrailLadder(),
                proposalStore,
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
            // persists it through the real proposal store.
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

            await AssertNoStoredPlaintextAsync(state, secret);
            audit.Events.Should().NotBeEmpty();
            JsonSerializer.Serialize(audit.Events).Should().NotContain(secret);
        }
    }

    private static PostgresOperationSecretStore BuildSecretStore(ControlPlaneStateHarness state)
    {
        var services = new ServiceCollection();
        services.AddDataProtection().SetApplicationName("Honua.Tests.OperationSecretEvidence");
        return state.CreateSecretStore(services.BuildServiceProvider().GetRequiredService<IDataProtectionProvider>());
    }

    private static AdminOperateOperationExecutor BuildAccessExecutor(
        string operationId,
        HttpClient client,
        IOperationSecretStore secretStore,
        Honua.Infrastructure.Authentication.IAdminApiKeyStore? adminApiKeyStore = null)
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
            adminApiKeyStore,
            TimeProvider.System,
            new OperationLineageAttestationStore(TimeProvider.System),
            secretStore);
    }

    private static async Task AssertNoStoredPlaintextAsync(ControlPlaneStateHarness state, string secret)
    {
        var scan = await state.ScanApprovalTablesForAsync(secret);
        scan.RowsScanned.Should().BeGreaterThan(0, "the run must have written durable state to scan");
        scan.RowsContainingText.Should().Be(0, "no stored row may retain secret material");
    }

    private sealed class StubAdminApi(string secret, string secretField) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            // codeql[cs/local-not-disposed]: HttpClient disposes the response after the caller reads it
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = new StringContent(
                    $"{{\"data\":{{\"id\":\"resource-id\",\"{secretField}\":\"{secret}\"}}}}"),
            });
    }

    private sealed class RecordingAuditLog : IAuditLog
    {
        public List<AuditEvent> Events { get; } = [];
        public string? FailAction { get; init; }

        public Task<string?> RecordAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default)
        {
            Events.Add(auditEvent);
            return Task.FromResult<string?>(FailAction is not null && auditEvent.Action == FailAction ? null : $"audit-{Guid.NewGuid():N}");
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
