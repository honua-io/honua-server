// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Honua.Ai.Protocols.Mcp;
using Honua.Ai.Protocols.Mcp.Tools;
using Honua.Ai.StudioAiProxy;
using Honua.Ai.StudioAiProxy.Abstractions;
using Honua.Ai.StudioAiProxy.Domain;
using Honua.ControlPlane;
using Honua.Core.Features.AuditLog.Abstractions;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Guardrails.Abstractions;
using Honua.Core.Features.Guardrails.Domain;
using Honua.Core.Features.Licensing.Domain;
using Honua.Core.Features.Operations.Abstractions;
using Honua.Core.Features.Operations.Domain;
using Honua.Core.Features.Security.Abstractions;
using Honua.Infrastructure.Authentication;
using Honua.Server.Features.Operations;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using StackExchange.Redis;
using Xunit.Abstractions;
using LegacyExecutor = Honua.Core.Features.ControlPlane.Abstractions.IOperationExecutor;

namespace Honua.Server.Tests.Features.Infrastructure.Monitoring;

/// <summary>
/// Full production-route security journey for model-selected release proposals (#3888).
/// The only substituted boundaries are the deterministic model provider, mutation spies,
/// clock, signing secret, and explicit durable-store failure switches.
/// </summary>
[Collection(RedisFixture.CollectionName)]
[Protocol(TestProtocols.Mcp)]
[Operation(Operations.TestInfrastructure)]
public sealed class ProposalEvidenceSecurityJourneyTests(
    RedisFixture redis,
    ITestOutputHelper output) : IAsyncLifetime
{
    private const string TenantA = "tenant-a";
    private const string TenantB = "tenant-b";
    private const string Candidate = "candidate-a";
    private const string Provider = "proposal-evidence-deterministic";
    private const string SigningKeyId = "proposal-evidence-key-1";
    private const string SigningReference = "test-secret://proposal-evidence-signing-seed";
    private static readonly byte[] SigningSeed = Enumerable.Range(1, 32).Select(value => (byte)value).ToArray();
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    private readonly MutableTimeProvider _clock = new(new DateTimeOffset(2026, 9, 2, 19, 45, 0, TimeSpan.Zero));
    private readonly DeterministicProposalAdapter _adapter = new();
    private readonly RecordingActuator _deploy = new(OperationClass.Deploy);
    private readonly RecordingActuator _metadata = new(OperationClass.MetadataRelease);
    private readonly AuditFailureSwitch _auditFailure = new();
    private readonly List<HttpClient> _clients = [];
    private readonly Dictionary<string, ToolDescriptor> _descriptors = new(StringComparer.Ordinal);
    private ConnectionMultiplexer _redis = null!;
    private FaultSwitchProposalStore _proposals = null!;
    private WebAppFixture _fixture = null!;
    private IAdminApiKeyStore _apiKeys = null!;

    public async Task InitializeAsync()
    {
        _redis = await ConnectionMultiplexer.ConnectAsync(redis.ConnectionString);
        _proposals = new FaultSwitchProposalStore(new RedisOperationProposalStore(
            _redis, NullLogger<RedisOperationProposalStore>.Instance));

        _fixture = new WebAppFixture()
            .ConfigureWebHost(ConfigureHost)
            .ConfigureServices(ConfigureServices);
        await _fixture.InitializeAsync();
        _apiKeys = _fixture.Services.GetRequiredService<IAdminApiKeyStore>();
    }

    public async Task DisposeAsync()
    {
        foreach (var client in _clients)
        {
            client.Dispose();
        }

        await _fixture.DisposeAsync();
        await _redis.DisposeAsync();
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/studio/ai/chat")]
    [Endpoint("POST /mcp")]
    [Endpoint("GET /api/v1/admin/proposals/{id}")]
    [Endpoint("POST /api/v1/admin/proposals/{id}/approve")]
    [InterfaceOperation(TestProtocols.Mcp, "initialize")]
    [InterfaceOperation(TestProtocols.Mcp, "tools/list")]
    [InterfaceOperation(TestProtocols.Mcp, "tools/call")]
    public async Task FullProposalEvidenceJourney_EmitsExactDenominator_WithZeroSkippedScenarios()
    {
        var receipts = new List<JourneyReceipt>();

        await RunAllowedAsync(ProposeDeployOperationTool.ToolName, OperationClass.Deploy, receipts);
        await RunAllowedAsync(ProposeMetadataReleaseTool.ToolName, OperationClass.MetadataRelease, receipts);

        await RejectDirectExecutionAsync(receipts);
        await RejectOpaqueFieldAsync(receipts);
        await RejectUnsupportedDescriptorAsync(receipts);
        await RejectParameterTamperAsync(receipts);
        await RejectCandidateMismatchAsync(receipts);
        await RejectTenantMismatchAsync(receipts);
        await RejectEvidenceTamperAsync("target-mismatch", evidence => evidence with { TargetId = "candidate-b" }, receipts);
        await RejectEvidenceTamperAsync("descriptor-revision-mismatch", evidence => evidence with { DescriptorRevision = "changed" }, receipts);
        await RejectEvidenceTamperAsync("policy-revision-mismatch", evidence => evidence with { PolicyRevision = "changed" }, receipts);
        await RejectPayloadTamperAsync(receipts);
        await RejectSelfApprovalAsync(receipts);
        await RejectExpiredEvidenceAsync(receipts);
        await RejectRevokedBeforeCallAsync(receipts);
        await RejectRevokedBeforeApprovalAsync(receipts);
        await RejectUnauthorizedApproverAsync(receipts);
        await HideCrossTenantReadAsync(receipts);
        await RejectSequentialReplayAsync(receipts);
        await BoundConcurrentApprovalAsync(receipts);
        await RejectProposalStoreFailureAsync(receipts);
        await RejectAuditStoreFailureAsync(receipts);

        var expected = new[]
        {
            "allow-deploy", "allow-metadata-release", "direct-execution", "opaque-extra-field",
            "unsupported-descriptor", "parameter-tamper", "candidate-mismatch", "tenant-mismatch",
            "target-mismatch", "descriptor-revision-mismatch", "policy-revision-mismatch",
            "payload-digest-mismatch", "proposer-self-approval", "expired", "revoked-before-call",
            "revoked-before-approval", "unauthorized-approver", "cross-tenant-read", "sequential-replay",
            "concurrent-approval", "proposal-store-failure", "audit-store-failure",
        };
        receipts.Should().HaveCount(expected.Length);
        receipts.Select(receipt => receipt.Scenario).Should().BeEquivalentTo(expected);
        receipts.Where(receipt => receipt.Outcome != "allowed" && receipt.Outcome != "at-most-once")
            .Should().OnlyContain(receipt => receipt.MutationCount == 0 && receipt.NegativeReason != null);
        receipts.Where(receipt => receipt.Outcome == "allowed")
            .Should().OnlyContain(receipt => receipt.MutationCount == 1
                && receipt.FinalResourceOrJobId != null
                && receipt.SignedTranscript != null
                && receipt.TranscriptSignature != null
                && receipt.McpSessionId != null
                && receipt.McpCallId != null
                && receipt.AuthorizationDecision != null
                && receipt.CanonicalRequestDigest != null
                && receipt.VerifierVerdict == "verified");

        foreach (var receipt in receipts)
        {
            output.WriteLine(JsonSerializer.Serialize(receipt, WebJson));
        }
    }

    private async Task RunAllowedAsync(
        string toolName,
        OperationClass operationClass,
        List<JourneyReceipt> receipts)
    {
        var before = MutationCount;
        var proposal = await CreateProposalAsync(toolName);
        MutationCount.Should().Be(before, "model output and proposal creation must not actuate");

        var approver = await CreateKeyAsync($"approver-{Guid.NewGuid():N}", ["admin:*"]);
        var approverClient = CreateClient(approver.Key, TenantA);
        using var approval = await approverClient.PostAsync(
            $"/api/v1/admin/proposals/{proposal.Proposal.ProposalId}/approve", null);
        approval.StatusCode.Should().Be(HttpStatusCode.OK);

        var persisted = await _proposals.GetAsync(proposal.Proposal.ProposalId);
        persisted.Should().NotBeNull();
        persisted!.Status.Should().Be(
            OperationProposalStatus.Submitted,
            "release actuators return a durable downstream job identity");
        MutationCount.Should().Be(before + 1);
        (operationClass == OperationClass.Deploy ? _deploy : _metadata)
            .Executions.Should().ContainSingle();

        var evidence = persisted.Evidence!;
        evidence.VerifierDecision.Should().Be("verified");
        evidence.CandidateId.Should().Be(Candidate);
        evidence.TenantId.Should().Be(TenantA);
        evidence.TargetId.Should().Be(Candidate);
        evidence.OperationId.Should().Be(LegacyOperationIds.For(operationClass));
        evidence.PayloadDigest.Should().NotBeNullOrWhiteSpace();
        evidence.RequestDigest.Should().NotBeNullOrWhiteSpace();
        evidence.AuthorizationDecision.Should().Be("admin-policy-authorized");
        evidence.CanonicalTranscript.Should().NotBeNullOrWhiteSpace();
        evidence.TranscriptSignature.Should().NotBeNullOrWhiteSpace();
        evidence.McpSessionId.Should().NotBeNullOrWhiteSpace();
        evidence.McpCallId.Should().NotBeNullOrWhiteSpace();
        persisted.RequestedBy.Should().Contain($":api-key:{proposal.Proposer.Record.Id:D}");
        persisted.ResolvedBy.Should().Contain($":api-key:{approver.Record.Id:D}");
        persisted.Audit.OperationInstanceId.Should().NotBeNullOrWhiteSpace();
        persisted.Audit.AuditId.Should().NotBeNullOrWhiteSpace();
        persisted.Audit.CorrelationId.Should().NotBeNullOrWhiteSpace();
        persisted.Audit.IdempotencyKey.Should().NotBeNullOrWhiteSpace();
        persisted.ExecutionOperationId.Should().NotBeNullOrWhiteSpace();

        using var scope = _fixture.Services.CreateScope();
        var auditPage = await scope.ServiceProvider.GetRequiredService<IAuditLogReader>().ListAsync(
            new AuditLogFilter { ResourceId = persisted.ProposalId, PageSize = 20 });
        auditPage.Items.Should().Contain(item => item.Action == "operation.proposed");
        auditPage.Items.Should().Contain(item => item.Action == "operation.applied");
        auditPage.Items.Select(item => item.AuditId.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .Should().Contain(persisted.Audit.AuditId!);

        receipts.Add(ToReceipt(
            operationClass == OperationClass.Deploy ? "allow-deploy" : "allow-metadata-release",
            "allowed", persisted, MutationCount - before));
    }

    private async Task RejectDirectExecutionAsync(List<JourneyReceipt> receipts)
    {
        var context = await CreateSelectionContextAsync(
            "honua_execute_plan", "{}", Candidate, descriptorOverride: null);
        var before = MutationCount;
        var response = await CallToolAsync(context.Client, context.SessionId, context.Selection);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Document.Should().NotBeNull();
        HasApprovalHandle(response.Document!.RootElement).Should().BeFalse();
        MutationCount.Should().Be(before);
        receipts.Add(new JourneyReceipt("direct-execution", "rejected", null, null, null, null, null,
            null, null, null, null, null, null, null, null, null, context.Selection.Provenance?.TranscriptDigest, 0,
            SignedTranscript: context.Selection.Provenance?.CanonicalTranscript,
            TranscriptSignature: context.Selection.Provenance?.Signature,
            TranscriptKeyId: context.Selection.Provenance?.KeyId,
            McpSessionId: context.SessionId,
            ToolName: context.Selection.ToolName,
            VerifierVerdict: "rejected-at-mcp",
            NegativeReason: "signed output selected a direct-execution tool"));
    }

    private async Task RejectOpaqueFieldAsync(List<JourneyReceipt> receipts)
    {
        var context = await CreateSelectionContextAsync(
            ProposeDeployOperationTool.ToolName,
            ValidDeployArguments(extra: "\"opaque\":true"), Candidate, descriptorOverride: null);
        await AssertCallRejectedAsync("opaque-extra-field", context, receipts);
    }

    private async Task RejectUnsupportedDescriptorAsync(List<JourneyReceipt> receipts)
    {
        var descriptor = await GetDescriptorAsync(ProposeDeployOperationTool.ToolName);
        var node = JsonNode.Parse(descriptor.InputSchema.GetRawText())!.AsObject();
        node["title"] = "unsupported-descriptor-revision";
        var changed = descriptor with { InputSchema = ParseElement(node.ToJsonString()) };
        var context = await CreateSelectionContextAsync(
            ProposeDeployOperationTool.ToolName, ValidDeployArguments(), Candidate, changed);
        await AssertCallRejectedAsync("unsupported-descriptor", context, receipts);
    }

    private async Task RejectParameterTamperAsync(List<JourneyReceipt> receipts)
    {
        var context = await CreateSelectionContextAsync(
            ProposeDeployOperationTool.ToolName, ValidDeployArguments(), Candidate, descriptorOverride: null);
        context = context with
        {
            Selection = context.Selection with
            {
                Arguments = ParseElement(ValidDeployArguments(desiredRevision: "sha256:tampered")),
            },
        };
        await AssertCallRejectedAsync("parameter-tamper", context, receipts);
    }

    private async Task RejectCandidateMismatchAsync(List<JourneyReceipt> receipts)
    {
        var before = MutationCount;
        var key = await CreateKeyAsync($"candidate-mismatch-{Guid.NewGuid():N}", ["admin:*"]);
        var client = CreateClient(key.Key, TenantA);
        var session = await InitializeMcpAsync(client);
        var descriptor = await GetDescriptorAsync(ProposeDeployOperationTool.ToolName, client, session);
        var selection = await SelectThroughProxyAsync(
            client, descriptor, ValidDeployArguments(target: "candidate-b"), Candidate);
        selection.Provenance.Should().BeNull();
        selection.ErrorCode.Should().Be("studio_ai/provenance_validation_failed");
        MutationCount.Should().Be(before);
        receipts.Add(new JourneyReceipt("candidate-mismatch", "rejected-at-proxy", null, null, null, null,
            TenantA, "candidate-b", Candidate, null, null, null, null, null, null, null, null, 0,
            McpSessionId: session,
            ToolName: selection.ToolName,
            VerifierVerdict: "rejected-at-proxy",
            NegativeReason: selection.ErrorCode));
    }

    private async Task RejectTenantMismatchAsync(List<JourneyReceipt> receipts)
    {
        var tenantA = await CreateSelectionContextAsync(
            ProposeDeployOperationTool.ToolName, ValidDeployArguments(), Candidate, descriptorOverride: null);
        var tenantBKey = await CreateKeyAsync($"tenant-b-{Guid.NewGuid():N}", ["admin:*"]);
        var tenantBClient = CreateClient(tenantBKey.Key, TenantB);
        var tenantBSession = await InitializeMcpAsync(tenantBClient);
        var context = tenantA with { Client = tenantBClient, SessionId = tenantBSession };
        await AssertCallRejectedAsync("tenant-mismatch", context, receipts);
    }

    private async Task RejectEvidenceTamperAsync(
        string scenario,
        Func<OperationProposalEvidence, OperationProposalEvidence> tamper,
        List<JourneyReceipt> receipts)
    {
        var before = MutationCount;
        var context = await CreateProposalAsync(ProposeDeployOperationTool.ToolName);
        var changed = context.Proposal with { Evidence = tamper(context.Proposal.Evidence!) };
        (await _proposals.TrySetAsync(changed)).Should().BeTrue();
        var approver = await CreateKeyAsync($"{scenario}-approver-{Guid.NewGuid():N}", ["admin:*"]);
        using var response = await CreateClient(approver.Key, TenantA).PostAsync(
            $"/api/v1/admin/proposals/{changed.ProposalId}/approve", null);
        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        MutationCount.Should().Be(before);
        receipts.Add(ToReceipt(scenario, "rejected-at-approval", changed, 0));
    }

    private async Task RejectPayloadTamperAsync(List<JourneyReceipt> receipts)
    {
        var before = MutationCount;
        var context = await CreateProposalAsync(ProposeDeployOperationTool.ToolName);
        var plan = context.Proposal.Plan with
        {
            ExecutionPayload = "{\"currentRevision\":null,\"desiredRevision\":\"sha256:tampered\",\"targetId\":\"candidate-a\"}",
        };
        var changed = context.Proposal with
        {
            Plan = plan,
            SealedPlanHash = OperationApprovalPlanSeal.Compute(plan),
        };
        (await _proposals.TrySetAsync(changed)).Should().BeTrue();
        var approver = await CreateKeyAsync($"payload-approver-{Guid.NewGuid():N}", ["admin:*"]);
        using var response = await CreateClient(approver.Key, TenantA).PostAsync(
            $"/api/v1/admin/proposals/{changed.ProposalId}/approve", null);
        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        MutationCount.Should().Be(before);
        receipts.Add(ToReceipt("payload-digest-mismatch", "rejected-at-approval", changed, 0));
    }

    private async Task RejectSelfApprovalAsync(List<JourneyReceipt> receipts)
    {
        var before = MutationCount;
        var context = await CreateProposalAsync(ProposeDeployOperationTool.ToolName);
        using var response = await context.Client.PostAsync(
            $"/api/v1/admin/proposals/{context.Proposal.ProposalId}/approve", null);
        response.StatusCode.Should().BeOneOf(HttpStatusCode.Forbidden, HttpStatusCode.Conflict);
        MutationCount.Should().Be(before);
        receipts.Add(ToReceipt("proposer-self-approval", "rejected-at-approval", context.Proposal, 0));
    }

    private async Task RejectExpiredEvidenceAsync(List<JourneyReceipt> receipts)
    {
        var before = MutationCount;
        var context = await CreateProposalAsync(ProposeDeployOperationTool.ToolName);
        var approver = await CreateKeyAsync($"expired-approver-{Guid.NewGuid():N}", ["admin:*"]);
        _clock.Advance(TimeSpan.FromSeconds(31));
        try
        {
            using var response = await CreateClient(approver.Key, TenantA).PostAsync(
                $"/api/v1/admin/proposals/{context.Proposal.ProposalId}/approve", null);
            response.StatusCode.Should().Be(HttpStatusCode.Conflict);
            MutationCount.Should().Be(before);
            receipts.Add(ToReceipt("expired", "rejected-at-approval", context.Proposal, 0));
        }
        finally
        {
            _clock.Advance(TimeSpan.FromSeconds(-31));
        }
    }

    private async Task RejectRevokedBeforeCallAsync(List<JourneyReceipt> receipts)
    {
        var context = await CreateSelectionContextAsync(
            ProposeDeployOperationTool.ToolName, ValidDeployArguments(), Candidate, descriptorOverride: null);
        await _apiKeys.RevokeAsync(context.Proposer.Record.Id, CancellationToken.None);
        var before = MutationCount;
        var response = await CallToolAsync(context.Client, context.SessionId, context.Selection);
        response.StatusCode.Should().Be(HttpStatusCode.OK, "MCP reports tool-call failures in its JSON-RPC envelope");
        response.Document.Should().NotBeNull();
        IsMcpError(response.Document!.RootElement).Should().BeTrue(
            $"a revoked proposer must receive an MCP error: {response.Document.RootElement.GetRawText()}");
        HasApprovalHandle(response.Document.RootElement).Should().BeFalse();
        MutationCount.Should().Be(before);
        receipts.Add(new JourneyReceipt("revoked-before-call", "rejected-by-authentication", null, null,
            null, null, TenantA, Candidate, Candidate, null, null, null, null, null, null, null,
            context.Selection.Provenance?.TranscriptDigest, 0,
            SignedTranscript: context.Selection.Provenance?.CanonicalTranscript,
            TranscriptSignature: context.Selection.Provenance?.Signature,
            TranscriptKeyId: context.Selection.Provenance?.KeyId,
            McpSessionId: context.SessionId,
            ToolName: context.Selection.ToolName,
            VerifierVerdict: "rejected-by-authentication",
            NegativeReason: "proposer credential revoked before tools/call"));
    }

    private async Task RejectRevokedBeforeApprovalAsync(List<JourneyReceipt> receipts)
    {
        var before = MutationCount;
        var context = await CreateProposalAsync(ProposeDeployOperationTool.ToolName);
        await _apiKeys.RevokeAsync(context.Proposer.Record.Id, CancellationToken.None);
        var approver = await CreateKeyAsync($"revocation-approver-{Guid.NewGuid():N}", ["admin:*"]);
        using var response = await CreateClient(approver.Key, TenantA).PostAsync(
            $"/api/v1/admin/proposals/{context.Proposal.ProposalId}/approve", null);
        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        MutationCount.Should().Be(before);
        receipts.Add(ToReceipt("revoked-before-approval", "rejected-at-approval", context.Proposal, 0));
    }

    private async Task RejectUnauthorizedApproverAsync(List<JourneyReceipt> receipts)
    {
        var before = MutationCount;
        var context = await CreateProposalAsync(ProposeDeployOperationTool.ToolName);
        var reader = await CreateKeyAsync($"reader-{Guid.NewGuid():N}", ["admin:read"]);
        using var response = await CreateClient(reader.Key, TenantA).PostAsync(
            $"/api/v1/admin/proposals/{context.Proposal.ProposalId}/approve", null);
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        MutationCount.Should().Be(before);
        receipts.Add(ToReceipt("unauthorized-approver", "rejected-by-authorization", context.Proposal, 0));
    }

    private async Task HideCrossTenantReadAsync(List<JourneyReceipt> receipts)
    {
        var before = MutationCount;
        var context = await CreateProposalAsync(ProposeDeployOperationTool.ToolName);
        var other = await CreateKeyAsync($"other-tenant-{Guid.NewGuid():N}", ["admin:*"]);
        using var response = await CreateClient(other.Key, TenantB).GetAsync(
            $"/api/v1/admin/proposals/{context.Proposal.ProposalId}");
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        MutationCount.Should().Be(before);
        receipts.Add(ToReceipt("cross-tenant-read", "hidden", context.Proposal, 0));
    }

    private async Task RejectSequentialReplayAsync(List<JourneyReceipt> receipts)
    {
        var before = MutationCount;
        var context = await CreateProposalAsync(ProposeDeployOperationTool.ToolName);
        var approver = await CreateKeyAsync($"replay-approver-{Guid.NewGuid():N}", ["admin:*"]);
        var client = CreateClient(approver.Key, TenantA);
        using var first = await client.PostAsync($"/api/v1/admin/proposals/{context.Proposal.ProposalId}/approve", null);
        using var second = await client.PostAsync($"/api/v1/admin/proposals/{context.Proposal.ProposalId}/approve", null);
        first.StatusCode.Should().Be(HttpStatusCode.OK);
        second.StatusCode.Should().Be(HttpStatusCode.Conflict);
        MutationCount.Should().Be(before + 1);
        receipts.Add(ToReceipt("sequential-replay", "at-most-once", context.Proposal, 1));
    }

    private async Task BoundConcurrentApprovalAsync(List<JourneyReceipt> receipts)
    {
        var before = MutationCount;
        var context = await CreateProposalAsync(ProposeMetadataReleaseTool.ToolName);
        var approver = await CreateKeyAsync($"concurrent-approver-{Guid.NewGuid():N}", ["admin:*"]);
        var client = CreateClient(approver.Key, TenantA);
        var path = $"/api/v1/admin/proposals/{context.Proposal.ProposalId}/approve";
        var responses = await Task.WhenAll(client.PostAsync(path, null), client.PostAsync(path, null));
        try
        {
            responses.Count(response => response.StatusCode == HttpStatusCode.OK).Should().Be(1);
            responses.Count(response => response.StatusCode == HttpStatusCode.Conflict).Should().Be(1);
        }
        finally
        {
            foreach (var response in responses) response.Dispose();
        }
        MutationCount.Should().Be(before + 1);
        receipts.Add(ToReceipt("concurrent-approval", "at-most-once", context.Proposal, 1));
    }

    private async Task RejectProposalStoreFailureAsync(List<JourneyReceipt> receipts)
    {
        var context = await CreateSelectionContextAsync(
            ProposeDeployOperationTool.ToolName, ValidDeployArguments(), Candidate, descriptorOverride: null);
        var before = MutationCount;
        _proposals.FailCreates = true;
        try
        {
            var response = await CallToolAsync(context.Client, context.SessionId, context.Selection);
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            response.Document.Should().NotBeNull();
            HasApprovalHandle(response.Document!.RootElement).Should().BeFalse();
        }
        finally
        {
            _proposals.FailCreates = false;
        }
        MutationCount.Should().Be(before);
        receipts.Add(new JourneyReceipt("proposal-store-failure", "failed-closed", null, null, null, null,
            TenantA, Candidate, Candidate, null, null, null, null, null, null, null,
            context.Selection.Provenance?.TranscriptDigest, 0,
            SignedTranscript: context.Selection.Provenance?.CanonicalTranscript,
            TranscriptSignature: context.Selection.Provenance?.Signature,
            TranscriptKeyId: context.Selection.Provenance?.KeyId,
            McpSessionId: context.SessionId,
            ToolName: context.Selection.ToolName,
            VerifierVerdict: "verified-before-store-failure",
            NegativeReason: "proposal store rejected durable creation"));
    }

    private async Task RejectAuditStoreFailureAsync(List<JourneyReceipt> receipts)
    {
        var context = await CreateSelectionContextAsync(
            ProposeDeployOperationTool.ToolName, ValidDeployArguments(), Candidate, descriptorOverride: null);
        var before = MutationCount;
        _auditFailure.FailWrites = true;
        try
        {
            var response = await CallToolAsync(context.Client, context.SessionId, context.Selection);
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            response.Document.Should().NotBeNull();
            HasApprovalHandle(response.Document!.RootElement).Should().BeFalse();
        }
        finally
        {
            _auditFailure.FailWrites = false;
        }
        MutationCount.Should().Be(before);
        receipts.Add(new JourneyReceipt("audit-store-failure", "failed-closed", null, null, null, null,
            TenantA, Candidate, Candidate, null, null, null, null, null, null, null,
            context.Selection.Provenance?.TranscriptDigest, 0,
            SignedTranscript: context.Selection.Provenance?.CanonicalTranscript,
            TranscriptSignature: context.Selection.Provenance?.Signature,
            TranscriptKeyId: context.Selection.Provenance?.KeyId,
            McpSessionId: context.SessionId,
            ToolName: context.Selection.ToolName,
            VerifierVerdict: "verified-before-audit-failure",
            NegativeReason: "audit store did not return a durable identity"));
    }

    private async Task AssertCallRejectedAsync(
        string scenario,
        SelectionContext context,
        List<JourneyReceipt> receipts)
    {
        var before = MutationCount;
        var response = await CallToolAsync(context.Client, context.SessionId, context.Selection);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Document.Should().NotBeNull();
        HasApprovalHandle(response.Document!.RootElement).Should().BeFalse();
        MutationCount.Should().Be(before);
        receipts.Add(new JourneyReceipt(scenario, "rejected-at-mcp", null, null, null, null,
            TenantA, Candidate, Candidate, null, null, null, null, null, null, null,
            context.Selection.Provenance?.TranscriptDigest, 0,
            SignedTranscript: context.Selection.Provenance?.CanonicalTranscript,
            TranscriptSignature: context.Selection.Provenance?.Signature,
            TranscriptKeyId: context.Selection.Provenance?.KeyId,
            McpSessionId: context.SessionId,
            ToolName: context.Selection.ToolName,
            VerifierVerdict: "rejected-at-mcp",
            NegativeReason: scenario));
    }

    private async Task<ProposalContext> CreateProposalAsync(string toolName)
    {
        var arguments = toolName == ProposeDeployOperationTool.ToolName
            ? ValidDeployArguments()
            : ValidMetadataArguments();
        var selection = await CreateSelectionContextAsync(
            toolName, arguments, Candidate, descriptorOverride: null);
        var before = MutationCount;
        var call = await CallToolAsync(selection.Client, selection.SessionId, selection.Selection);
        call.StatusCode.Should().Be(HttpStatusCode.OK);
        call.Document.Should().NotBeNull();
        var result = call.Document!.RootElement.GetProperty("result");
        result.TryGetProperty("structuredContent", out var structured)
            .Should().BeTrue($"the governed proposal call must return structured content; MCP result: {result.GetRawText()}");
        structured.TryGetProperty("requiresApproval", out var requiresApproval)
            .Should().BeTrue($"the governed proposal call must declare approval; MCP result: {result.GetRawText()}");
        requiresApproval.GetBoolean().Should().BeTrue();
        structured.TryGetProperty("proposalId", out var proposalIdElement)
            .Should().BeTrue($"the governed proposal call must return a proposal id; MCP result: {result.GetRawText()}");
        var proposalId = proposalIdElement.GetString();
        proposalId.Should().NotBeNullOrWhiteSpace();
        MutationCount.Should().Be(before);

        var proposal = await _proposals.GetAsync(proposalId!);
        proposal.Should().NotBeNull();
        proposal!.Status.Should().Be(OperationProposalStatus.AwaitingApproval);
        proposal.Evidence.Should().NotBeNull();
        return new ProposalContext(
            proposal, selection.Proposer, selection.Client, selection.SessionId, selection.Selection);
    }

    private async Task<SelectionContext> CreateSelectionContextAsync(
        string toolName,
        string arguments,
        string candidate,
        ToolDescriptor? descriptorOverride)
    {
        var proposer = await CreateKeyAsync($"proposer-{Guid.NewGuid():N}", ["admin:*"]);
        var client = CreateClient(proposer.Key, TenantA);
        var session = await InitializeMcpAsync(client);
        var descriptor = descriptorOverride ?? await GetDescriptorAsync(toolName, client, session);
        var selection = await SelectThroughProxyAsync(client, descriptor, arguments, candidate);
        selection.Provenance.Should().NotBeNull();
        selection.ToolName.Should().Be(toolName);
        return new SelectionContext(proposer, client, session, selection);
    }

    private async Task<SignedSelection> SelectThroughProxyAsync(
        HttpClient client,
        ToolDescriptor descriptor,
        string arguments,
        string candidate)
    {
        var request = new StudioAiChatHttpRequest
        {
            Certification = new StudioAiTranscriptCertification
            {
                CandidateId = candidate,
                ReleaseId = "2026.09.02-rc.1",
                EndpointIdentity = "/api/v1/studio/ai/chat",
                ActionId = $"proposal-evidence-{Guid.NewGuid():N}",
                RunNonce = $"run-{Guid.NewGuid():N}",
            },
            Messages = [new StudioAiChatHttpMessage { Role = "user", Content = arguments }],
            Tools =
            [
                new StudioAiChatHttpTool
                {
                    Name = descriptor.Name,
                    Description = descriptor.Description,
                    InputSchema = descriptor.InputSchema,
                    Annotations = descriptor.Annotations,
                    OutputSchema = descriptor.OutputSchema,
                },
            ],
            ToolChoice = new StudioAiChatHttpToolChoice { Mode = "specific", ToolName = descriptor.Name },
        };
        using var response = await client.PostAsJsonAsync("/api/v1/studio/ai/chat", request, WebJson);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("text/event-stream");
        return VerifyCanonicalSelection(
            ConsumeCanonicalSse(await response.Content.ReadAsStringAsync()));
    }

    private static SignedSelection VerifyCanonicalSelection(SignedSelection selection)
    {
        if (selection.Provenance is null)
        {
            return selection;
        }

        var transcript = Convert.FromBase64String(selection.Provenance.CanonicalTranscript);
        StudioAiTranscriptSigner.Canonicalize(transcript).Should().Equal(
            transcript, "the canonical consumer must reject non-canonical transcript bytes");
        Convert.ToHexStringLower(SHA256.HashData(transcript)).Should().Be(
            selection.Provenance.TranscriptDigest,
            "the canonical consumer must verify the transcript digest before dispatch");

        var privateKey = new Ed25519PrivateKeyParameters(SigningSeed, 0);
        var verifier = new Ed25519Signer();
        verifier.Init(false, privateKey.GeneratePublicKey());
        verifier.BlockUpdate(transcript, 0, transcript.Length);
        verifier.VerifySignature(Convert.FromBase64String(selection.Provenance.Signature)).Should().BeTrue(
            "the canonical consumer must verify the proxy signature before dispatch");
        return selection;
    }

    private static SignedSelection ConsumeCanonicalSse(string body)
    {
        string? toolName = null;
        JsonElement arguments = default;
        StudioAiSignedTranscript? provenance = null;
        string? errorCode = null;
        foreach (var frame in body.Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
        {
            var lines = frame.Split('\n');
            var eventName = lines.Single(line => line.StartsWith("event: ", StringComparison.Ordinal))[7..];
            var data = lines.Single(line => line.StartsWith("data: ", StringComparison.Ordinal))[6..];
            var evt = JsonSerializer.Deserialize(data, StudioAiProxyJsonContext.Default.StudioAiChatEvent)!;
            switch (eventName)
            {
                case "tool_call_start":
                    toolName = evt.ToolName;
                    break;
                case "tool_call_stop":
                    arguments = evt.ToolArguments!.Value.Clone();
                    break;
                case "transcript_provenance":
                    provenance = evt.Provenance;
                    break;
                case "error":
                    errorCode = evt.ErrorCode;
                    break;
            }
        }

        return new SignedSelection(toolName, arguments, provenance, errorCode);
    }

    private async Task<McpResponse> CallToolAsync(
        HttpClient client,
        string sessionId,
        SignedSelection selection)
    {
        var body = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = Guid.NewGuid().ToString("N"),
            ["method"] = "tools/call",
            ["params"] = new JsonObject
            {
                ["name"] = selection.ToolName,
                ["arguments"] = JsonNode.Parse(selection.Arguments.GetRawText()),
                ["_meta"] = new JsonObject
                {
                    [ProposalEvidenceVerifier.MetaProperty] = selection.Provenance is null
                        ? null
                        : JsonSerializer.SerializeToNode(
                            selection.Provenance,
                            StudioAiProxyJsonContext.Default.StudioAiSignedTranscript),
                },
            },
        };
        using var request = BuildMcpRequest(body, sessionId);
        var response = await client.SendAsync(request);
        JsonDocument? document = null;
        if (response.Content.Headers.ContentLength != 0)
        {
            var content = await response.Content.ReadAsStringAsync();
            if (!string.IsNullOrWhiteSpace(content)) document = JsonDocument.Parse(content);
        }
        var statusCode = response.StatusCode;
        response.Dispose();
        return new McpResponse(statusCode, document);
    }

    private async Task<string> InitializeMcpAsync(HttpClient client)
    {
        var body = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = "initialize",
            ["method"] = "initialize",
            ["params"] = new JsonObject
            {
                ["protocolVersion"] = "2025-06-18",
                ["capabilities"] = new JsonObject(),
                ["clientInfo"] = new JsonObject { ["name"] = "proposal-evidence-consumer", ["version"] = "1.0.0" },
                ["_meta"] = new JsonObject
                {
                    ["honua.io/workflow-view"] = "full",
                },
            },
        };
        using var request = BuildMcpRequest(body, sessionId: null);
        using var response = await client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.TryGetValues("Mcp-Session-Id", out var values).Should().BeTrue();
        return values!.Single();
    }

    private async Task<ToolDescriptor> GetDescriptorAsync(
        string toolName,
        HttpClient? client = null,
        string? sessionId = null)
    {
        if (_descriptors.TryGetValue(toolName, out var cached)) return cached;
        client.Should().NotBeNull();
        sessionId.Should().NotBeNull();
        string? cursor = null;
        do
        {
            var body = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = "tools-list",
                ["method"] = "tools/list",
                ["params"] = cursor is null ? new JsonObject() : new JsonObject { ["cursor"] = cursor },
            };
            using var request = BuildMcpRequest(body, sessionId);
            using var response = await client!.SendAsync(request);
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var result = document.RootElement.GetProperty("result");
            foreach (var tool in result.GetProperty("tools").EnumerateArray())
            {
                if (tool.GetProperty("name").GetString() != toolName) continue;
                var descriptor = new ToolDescriptor(
                    toolName,
                    tool.TryGetProperty("description", out var description) ? description.GetString() : null,
                    tool.GetProperty("inputSchema").Clone(),
                    tool.TryGetProperty("annotations", out var annotations) ? annotations.Clone() : null,
                    tool.TryGetProperty("outputSchema", out var outputSchema) ? outputSchema.Clone() : null);
                _descriptors.Add(toolName, descriptor);
                return descriptor;
            }

            cursor = result.TryGetProperty("nextCursor", out var nextCursor)
                && nextCursor.ValueKind == JsonValueKind.String
                ? nextCursor.GetString()
                : null;
        }
        while (cursor is not null);

        throw new InvalidOperationException($"The live MCP catalog did not advertise tool '{toolName}'.");
    }

    private static HttpRequestMessage BuildMcpRequest(JsonNode body, string? sessionId)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (!string.IsNullOrWhiteSpace(sessionId)) request.Headers.Add("Mcp-Session-Id", sessionId);
        return request;
    }

    private async Task<AdminApiKeyCreateResult> CreateKeyAsync(string name, IReadOnlyList<string> permissions)
        => await _apiKeys.CreateAsync(name, permissions, _clock.GetUtcNow().AddDays(1), "journey", CancellationToken.None);

    private HttpClient CreateClient(string key, string tenant)
    {
        var client = _fixture.CreateClient(candidate =>
        {
            candidate.DefaultRequestHeaders.Add("X-API-Key", key);
            candidate.DefaultRequestHeaders.Add("X-Honua-Tenant", tenant);
        });
        _clients.Add(client);
        return client;
    }

    private int MutationCount => _deploy.Executions.Count + _metadata.Executions.Count;

    private static bool HasApprovalHandle(JsonElement root)
        => root.TryGetProperty("result", out var result)
            && result.TryGetProperty("structuredContent", out var structured)
            && ((structured.TryGetProperty("proposalId", out var proposalId)
                    && proposalId.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(proposalId.GetString()))
                || (structured.TryGetProperty("requiresApproval", out var approval)
                    && approval.ValueKind == JsonValueKind.True));

    private static bool IsMcpError(JsonElement root)
        => root.TryGetProperty("error", out _)
            || (root.TryGetProperty("result", out var result)
                && result.TryGetProperty("isError", out var isError)
                && isError.ValueKind == JsonValueKind.True);

    private static JourneyReceipt ToReceipt(
        string scenario,
        string outcome,
        OperationProposal proposal,
        int mutationCount)
    {
        var evidence = proposal.Evidence;
        return new JourneyReceipt(
            scenario,
            outcome,
            proposal.ProposalId,
            proposal.Audit.OperationInstanceId,
            proposal.Audit.AuditId,
            proposal.Audit.CorrelationId,
            evidence?.TenantId,
            evidence?.TargetId,
            evidence?.CandidateId,
            evidence?.DescriptorRevision,
            evidence?.PolicyRevision,
            evidence?.PayloadDigest,
            proposal.Audit.IdempotencyKey,
            evidence?.ExpiresAt,
            proposal.RequestedBy,
            proposal.ResolvedBy,
            evidence?.TranscriptDigest,
            mutationCount,
            SignedTranscript: evidence?.CanonicalTranscript,
            TranscriptSignature: evidence?.TranscriptSignature,
            TranscriptKeyId: evidence?.TranscriptKeyId,
            McpSessionId: evidence?.McpSessionId,
            McpCallId: evidence?.McpCallId,
            ToolName: evidence?.ToolName,
            AuthorizationDecision: evidence?.AuthorizationDecision,
            CanonicalRequestDigest: evidence?.RequestDigest,
            FinalResourceOrJobId: proposal.ExecutionOperationId,
            CreatedAt: proposal.CreatedAt,
            UpdatedAt: proposal.UpdatedAt,
            ResolvedAt: proposal.ResolvedAt,
            IssuedAt: evidence?.IssuedAt,
            VerifierVerdict: evidence?.VerifierDecision,
            ExpiryDecision: scenario == "expired" ? "expired" : "valid-at-decision",
            NegativeReason: outcome is "allowed" or "at-most-once" ? null : scenario);
    }

    private static string ValidDeployArguments(
        string target = Candidate,
        string desiredRevision = "sha256:release-a",
        string? extra = null)
        => $$"""{"targetId":"{{target}}","desiredRevision":"{{desiredRevision}}","idempotencyKey":"deploy-{{Guid.NewGuid():N}}"{{(extra is null ? string.Empty : "," + extra)}}}""";

    private static string ValidMetadataArguments()
        => $$"""{"packageId":"package-a","targetEnvironment":"{{Candidate}}","resourceSemanticId":"roads","newFieldName":"speed_limit","idempotencyKey":"metadata-{{Guid.NewGuid():N}}"}""";

    private static JsonElement ParseElement(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private void ConfigureHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Test");
        builder.UseSetting("HONUA_DEV_AUTH", "false");
        builder.UseSetting("HONUA_ADMIN_PASSWORD", WebAppFixture.SharedAdminPassword);
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["MultiTenancy:MultiTenantAdminRoles:0"] = "admin",
                ["MultiTenancy:MultiTenantAdminRoles:1"] = "scoped-api-key",
                ["StudioAiProxy:Enabled"] = "true",
                ["StudioAiProxy:DefaultProvider"] = Provider,
                [$"StudioAiProxy:Providers:{Provider}:Kind"] = StudioAiProxyConfiguration.OpenAiKind,
                [$"StudioAiProxy:Providers:{Provider}:Endpoint"] = "https://deterministic.invalid/v1",
                [$"StudioAiProxy:Providers:{Provider}:Model"] = "deterministic-proposal-model",
                [$"StudioAiProxy:Providers:{Provider}:ApiKey"] = "test-key",
                [$"StudioAiProxy:Providers:{Provider}:SupportsTools"] = "true",
                ["StudioAiProxy:TranscriptSigning:KeyId"] = SigningKeyId,
                ["StudioAiProxy:TranscriptSigning:PrivateKeyReference"] = SigningReference,
                ["StudioAiProxy:TranscriptSigning:LifetimeSeconds"] = "30",
            }));
    }

    private void ConfigureServices(IServiceCollection services)
    {
        var auditDescriptor = services.Last(descriptor => descriptor.ServiceType == typeof(IAuditLog));
        services.RemoveAll<IAuditLog>();
        services.AddScoped<IAuditLog>(provider => new FaultSwitchAuditLog(
            ResolveDescriptor<IAuditLog>(provider, auditDescriptor), _auditFailure));

        services.RemoveAll<TimeProvider>();
        services.AddSingleton<TimeProvider>(_clock);
        services.RemoveAll<ISecretProvider>();
        services.AddSingleton<ISecretProvider>(new SigningSecretProvider(SigningReference, SigningSeed));
        services.RemoveAll<IStudioAiProxyAdapter>();
        services.AddSingleton<IStudioAiProxyAdapter>(_adapter);

        services.RemoveAll<IConnectionMultiplexer>();
        services.AddSingleton<IConnectionMultiplexer>(_redis);
        services.RemoveAll<IOperationProposalStore>();
        services.AddSingleton<IOperationProposalStore>(_proposals);
        services.RemoveAll<IOperationInstanceStore>();
        services.AddSingleton<IOperationInstanceStore>(new RedisOperationInstanceStore(_redis));
        services.RemoveAll<IOperationEnvelopeFactory>();
        services.AddSingleton<IOperationEnvelopeFactory>(provider => new ScopedOperationEnvelopeFactory(
            provider.GetRequiredService<IServiceScopeFactory>(), useVolatileAudit: false));

        services.RemoveAll<LegacyExecutor>();
        services.AddSingleton<LegacyExecutor>(_deploy);
        services.AddSingleton<LegacyExecutor>(_metadata);
        services.RemoveAll<IOperationExecutorCatalog>();
        services.AddSingleton<IOperationExecutorCatalog>(new JourneyExecutorCatalog());
        services.RemoveAll<IGuardrailLadder>();
        services.AddSingleton<IGuardrailLadder>(new JourneyGuardrailLadder());
        services.RemoveAll<IOperationProposalEvidenceValidator>();
        services.AddSingleton<IOperationProposalEvidenceValidator, OperationProposalEvidenceValidator>();
        services.RemoveAll<IOperationGateway>();
        services.AddSingleton<IOperationGateway, OperationGateway>();
    }

    private static T ResolveDescriptor<T>(IServiceProvider provider, ServiceDescriptor descriptor)
        where T : class
        => descriptor.ImplementationInstance as T
            ?? descriptor.ImplementationFactory?.Invoke(provider) as T
            ?? (T)ActivatorUtilities.CreateInstance(provider, descriptor.ImplementationType!);

    private sealed class DeterministicProposalAdapter : IStudioAiProxyAdapter
    {
        public string Kind => StudioAiProxyConfiguration.OpenAiKind;

        public bool IsConfigured(string providerName, StudioAiProxyProviderOptions options) => true;

        public async IAsyncEnumerable<StudioAiChatEvent> StreamAsync(
            StudioAiProxyProviderOptions options,
            StudioAiChatRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            var toolName = request.ToolChoice!.ToolName!;
            var arguments = ParseElement(request.Messages.Single().Content);
            yield return new StudioAiChatEvent
            {
                Type = StudioAiChatEventType.MessageStart,
                Model = options.Model,
            };
            yield return new StudioAiChatEvent
            {
                Type = StudioAiChatEventType.ToolCallStart,
                ToolCallId = "deterministic-call-1",
                ToolName = toolName,
            };
            yield return new StudioAiChatEvent
            {
                Type = StudioAiChatEventType.ToolCallStop,
                ToolCallId = "deterministic-call-1",
                ToolArguments = arguments,
            };
            yield return new StudioAiChatEvent
            {
                Type = StudioAiChatEventType.MessageStop,
                StopReason = StudioAiStopReason.ToolCall,
            };
            await Task.CompletedTask;
        }
    }

    private sealed class RecordingActuator(OperationClass operationClass) : LegacyExecutor
    {
        public OperationClass OperationClass { get; } = operationClass;
        public List<ExecutionReceipt> Executions { get; } = [];

        public Task<OperationProposalPlan?> PlanAsync(
            OperationGatewayRequest request,
            CancellationToken cancellationToken = default)
            => Task.FromResult<OperationProposalPlan?>(new OperationProposalPlan
            {
                Summary = $"Apply {OperationClass} release mutation",
                RiskLevel = ProposalRiskLevel.High,
                ExecutionPayload = request.ExecutionPayload,
            });

        public Task<string?> ExecuteAsync(
            OperationGatewayRequest request,
            string? executionPayload,
            CancellationToken cancellationToken = default)
        {
            Executions.Add(new ExecutionReceipt(
                request.Evidence?.McpCallId,
                request.Evidence?.ToolName,
                request.Evidence?.TargetId,
                request.Evidence is null ? null : request.Evidence.McpCallId,
                request.Evidence is null ? null : request.Evidence.TranscriptDigest,
                request.Evidence is null ? null : request.Evidence.TenantId,
                request.Evidence is null ? null : request.Evidence.CandidateId,
                request.Evidence is null ? null : request.Evidence.OperationId,
                request.Evidence is null ? null : request.Evidence.ReleaseId,
                request.Evidence is null ? null : request.Evidence.ActionId,
                request.Evidence is null ? null : request.Evidence.RunNonce,
                request.Evidence is null ? null : request.Evidence.PayloadDigest,
                request.Evidence is null ? null : request.Evidence.DescriptorRevision,
                request.Evidence is null ? null : request.Evidence.PolicyRevision,
                request.Evidence is null ? null : request.Evidence.McpSessionId,
                request.Evidence is null ? null : request.Evidence.VerifierDecision,
                request.OperationInstanceId,
                request.CorrelationId,
                request.IdempotencyKey,
                executionPayload,
                request.Evidence is null ? null : request.Evidence.McpCallId));
            return Task.FromResult<string?>($"actuation-{OperationClass}-{Executions.Count}");
        }
    }

    private sealed class JourneyExecutorCatalog : IOperationExecutorCatalog
    {
        public IReadOnlyCollection<OperationClass> SupportedKinds { get; } =
            [OperationClass.Deploy, OperationClass.MetadataRelease];
    }

    private sealed class JourneyGuardrailLadder : IGuardrailLadder
    {
        public GuardrailDecision Resolve(OperationClass operationClass, HonuaEdition edition)
            => new(GuardrailTier.RequiresApproval, operationClass, edition, "proposal-evidence-journey");

        public GuardrailDecision Resolve(OperationClass operationClass)
            => new(GuardrailTier.RequiresApproval, operationClass, HonuaEdition.Enterprise, "proposal-evidence-journey");

        public GuardrailDecision Resolve(OperationClass operationClass, string? actionDiscriminator)
            => Resolve(operationClass);

        public GuardrailDecision Resolve(
            OperationClass operationClass,
            string? actionDiscriminator,
            HonuaEdition edition)
            => Resolve(operationClass, edition);
    }

    private sealed class SigningSecretProvider(string reference, byte[] seed) : ISecretProvider
    {
        public string ProviderName => "proposal-evidence-test-secret";
        public Task<string?> GetSecretAsync(string secretKey, CancellationToken cancellationToken = default)
            => Task.FromResult<string?>(secretKey == reference ? Convert.ToBase64String(seed) : null);
        public Task<string?> GetSecretOrDefaultAsync(
            string secretKey,
            string? defaultValue = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(secretKey == reference ? Convert.ToBase64String(seed) : defaultValue);
        public bool CanProvideSecret(string secretKey) => secretKey == reference;
        public Task<bool> CanResolveSecretAsync(string secretKey, CancellationToken cancellationToken = default)
            => Task.FromResult(secretKey == reference);
        public string[] GetSupportedProviders() => ["test-secret"];
        public bool IsSecretReference(string? value) => value == reference;
    }

    private sealed class MutableTimeProvider(DateTimeOffset initial) : TimeProvider
    {
        private DateTimeOffset _now = initial;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan delta) => _now = _now.Add(delta);
    }

    private sealed class AuditFailureSwitch
    {
        public bool FailWrites { get; set; }
    }

    private sealed class FaultSwitchAuditLog(IAuditLog inner, AuditFailureSwitch failure) : IAuditLog
    {
        public bool IsPersisted => inner.IsPersisted;
        public Task<string?> RecordAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default)
            => failure.FailWrites ? Task.FromResult<string?>(null) : inner.RecordAsync(auditEvent, cancellationToken);
    }

    private sealed class FaultSwitchProposalStore(IOperationProposalStore inner) : IOperationProposalStore
    {
        public bool FailCreates { get; set; }

        public Task<bool> TryCreateAsync(
            OperationProposal proposal,
            TimeSpan? ttl = null,
            CancellationToken cancellationToken = default)
            => FailCreates ? Task.FromResult(false) : inner.TryCreateAsync(proposal, ttl, cancellationToken);

        public Task<OperationProposal?> GetAsync(string proposalId, CancellationToken cancellationToken = default)
            => inner.GetAsync(proposalId, cancellationToken);
        public Task<bool> TrySetAsync(OperationProposal proposal, TimeSpan? ttl = null, CancellationToken cancellationToken = default)
            => inner.TrySetAsync(proposal, ttl, cancellationToken);
        public Task<IReadOnlyList<OperationProposal>> ListActiveAsync(OperationClass? kind = null, CancellationToken cancellationToken = default)
            => inner.ListActiveAsync(kind, cancellationToken);
        public Task<bool> TryAcquireLeaseAsync(string operationId, string ownerId, TimeSpan leaseDuration, CancellationToken cancellationToken = default)
            => inner.TryAcquireLeaseAsync(operationId, ownerId, leaseDuration, cancellationToken);
        public Task<bool> RenewLeaseAsync(string operationId, string ownerId, TimeSpan leaseDuration, CancellationToken cancellationToken = default)
            => inner.RenewLeaseAsync(operationId, ownerId, leaseDuration, cancellationToken);
        public Task ReleaseLeaseAsync(string operationId, string ownerId, CancellationToken cancellationToken = default)
            => inner.ReleaseLeaseAsync(operationId, ownerId, cancellationToken);
    }

    private sealed record ToolDescriptor(
        string Name,
        string? Description,
        JsonElement InputSchema,
        JsonElement? Annotations,
        JsonElement? OutputSchema);
    private sealed record SignedSelection(
        string? ToolName,
        JsonElement Arguments,
        StudioAiSignedTranscript? Provenance,
        string? ErrorCode);
    private sealed record SelectionContext(
        AdminApiKeyCreateResult Proposer,
        HttpClient Client,
        string SessionId,
        SignedSelection Selection);
    private sealed record ProposalContext(
        OperationProposal Proposal,
        AdminApiKeyCreateResult Proposer,
        HttpClient Client,
        string SessionId,
        SignedSelection Selection);
    private sealed record McpResponse(HttpStatusCode StatusCode, JsonDocument? Document);
    private sealed record JourneyReceipt(
        string Scenario,
        string Outcome,
        string? ProposalId,
        string? OperationInstanceId,
        string? AuditId,
        string? CorrelationId,
        string? TenantId,
        string? TargetId,
        string? CandidateId,
        string? DescriptorRevision,
        string? PolicyRevision,
        string? PayloadDigest,
        string? IdempotencyKey,
        DateTimeOffset? ExpiresAt,
        string? ProposerActor,
        string? ApproverActor,
        string? TranscriptDigest,
        int MutationCount,
        string? SignedTranscript = null,
        string? TranscriptSignature = null,
        string? TranscriptKeyId = null,
        string? McpSessionId = null,
        string? McpCallId = null,
        string? ToolName = null,
        string? AuthorizationDecision = null,
        string? CanonicalRequestDigest = null,
        string? FinalResourceOrJobId = null,
        DateTimeOffset? CreatedAt = null,
        DateTimeOffset? UpdatedAt = null,
        DateTimeOffset? ResolvedAt = null,
        DateTimeOffset? IssuedAt = null,
        string? VerifierVerdict = null,
        string? ExpiryDecision = null,
        string? NegativeReason = null);
    private sealed record ExecutionReceipt(
        string? ProposalId,
        string? ToolName,
        string? TargetId,
        string? McpCallId,
        string? TranscriptDigest,
        string? TenantId,
        string? CandidateId,
        string? OperationId,
        string? ReleaseId,
        string? ActionId,
        string? RunNonce,
        string? PayloadDigest,
        string? DescriptorRevision,
        string? PolicyRevision,
        string? McpSessionId,
        string? VerifierDecision,
        string? OperationInstanceId,
        string? CorrelationId,
        string? IdempotencyKey,
        string? ExecutionPayload,
        string? CallIdentity);
}
