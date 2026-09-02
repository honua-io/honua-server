// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Honua.Ai.Protocols.Mcp.Models;
using Honua.Ai.Protocols.Mcp.Tools;
using Honua.Ai.StudioAiProxy;
using Honua.Ai.StudioAiProxy.Domain;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;

namespace Honua.Server.Tests.Features.Protocols.Mcp;

/// <summary>
/// Unit coverage for MCP platform-ops tools (#2566).
/// </summary>
public sealed class McpPlatformOpsToolTests
{
    [Theory]
    [InlineData(ProposeDeployOperationTool.ToolName)]
    [InlineData(ProposeMetadataReleaseTool.ToolName)]
    public async Task GovernedModelMutation_ProducesOnlyDurableProposal_BeforeSeparateApproval(string toolName)
    {
        var seed = Enumerable.Range(1, 32).Select(value => (byte)value).ToArray();
        var privateKey = new Ed25519PrivateKeyParameters(seed, 0);
        var signingKey = new StudioAiTranscriptSigner.SigningKey("candidate-key", privateKey, privateKey.GeneratePublicKey().GetEncoded());
        var transcriptRequest = new StudioAiChatRequest
        {
            Provider = "anthropic",
            Model = "claude-sonnet-4-5",
            Certification = new StudioAiTranscriptCertification
            {
                CandidateId = "candidate-a",
                ReleaseId = "2026.1-rc.1",
                EndpointIdentity = "candidate-proxy",
                ActionId = "governed-mutation",
                RunNonce = "nonce-1"
            },
            Messages = [new StudioAiMessage { Role = StudioAiRole.User, Content = "propose the release mutation" }]
        };
        var providerEvents = new[]
        {
            new StudioAiChatEvent { Type = StudioAiChatEventType.MessageStart, Model = "claude-sonnet-4-5" },
            new StudioAiChatEvent { Type = StudioAiChatEventType.ToolCallStart, ToolCallId = "call-1", ToolName = toolName },
            new StudioAiChatEvent { Type = StudioAiChatEventType.ToolCallStop, ToolCallId = "call-1", ToolName = toolName },
            new StudioAiChatEvent { Type = StudioAiChatEventType.MessageStop, StopReason = StudioAiStopReason.ToolCall }
        };
        var signer = new StudioAiTranscriptSigner(
            Microsoft.Extensions.Options.Options.Create(new StudioAiProxyConfiguration()),
            TimeProvider.System);
        var provenance = signer.Sign(signingKey, transcriptRequest, "anthropic", "claude-sonnet-4-5", providerEvents);
        var signedBytes = Convert.FromBase64String(provenance.CanonicalTranscript);
        var verifier = new Ed25519Signer();
        verifier.Init(false, privateKey.GeneratePublicKey());
        verifier.BlockUpdate(signedBytes, 0, signedBytes.Length);
        verifier.VerifySignature(Convert.FromBase64String(provenance.Signature)).Should().BeTrue();
        using var transcript = JsonDocument.Parse(signedBytes);
        transcript.RootElement.GetProperty("candidateId").GetString().Should().Be("candidate-a");
        transcript.RootElement.GetProperty("actionId").GetString().Should().Be("governed-mutation");

        var reader = Substitute.For<IMcpPlatformOpsReader>();
        reader.ProposeDeployOperationAsync(
                Arg.Any<ClaimsPrincipal>(), Arg.Any<McpDeployMutationArgument>(), Arg.Any<CancellationToken>())
            .Returns(new McpProposeOperationOutput
            {
                Outcome = "ProposalCreated",
                RequiresApproval = true,
                ProposalId = "proposal-sealed-1",
                ResourceUri = "honua://proposals/proposal-sealed-1"
            });
        reader.ProposeMetadataReleaseAsync(
                Arg.Any<ClaimsPrincipal>(), Arg.Any<McpMetadataReleaseMutationArgument>(), Arg.Any<CancellationToken>())
            .Returns(new McpProposeOperationOutput
            {
                Outcome = "ProposalCreated",
                RequiresApproval = true,
                ProposalId = "proposal-sealed-1",
                ResourceUri = "honua://proposals/proposal-sealed-1"
            });
        var context = BuildContext(reader);
        IMcpTool tool = toolName == ProposeDeployOperationTool.ToolName
            ? new ProposeDeployOperationTool(NullLogger<ProposeDeployOperationTool>.Instance)
            : new ProposeMetadataReleaseTool(NullLogger<ProposeMetadataReleaseTool>.Instance);
        var arguments = toolName == ProposeDeployOperationTool.ToolName
            ? Json("""{"targetId":"candidate-a","idempotencyKey":"signed-transcript-1","parameters":{}}""")
            : Json("""{"packageId":"package-a","targetEnvironment":"candidate-a","idempotencyKey":"signed-transcript-1"}""");

        var result = await tool.InvokeAsync(context, arguments, CancellationToken.None);

        result.IsError.Should().BeFalse();
        var proposal = result.StructuredContent!.Value;
        proposal.GetProperty("outcome").GetString().Should().Be("ProposalCreated");
        proposal.GetProperty("requiresApproval").GetBoolean().Should().BeTrue();
        proposal.GetProperty("proposalId").GetString().Should().Be("proposal-sealed-1");
        tool.Describe().Name.Should().StartWith("honua_propose_");
        tool.Describe().Name.Should().NotContain("execute");
    }

    [UnitTest]
    public async Task PlatformReleaseStatusTool_Invoke_ReturnsReaderPayload()
    {
        var reader = new FakePlatformOpsReader();
        var context = BuildContext(reader);
        var tool = new PlatformReleaseStatusTool(NullLogger<PlatformReleaseStatusTool>.Instance);

        var result = await tool.InvokeAsync(context, Json("{}"), CancellationToken.None);

        result.IsError.Should().BeFalse();
        result.StructuredContent!.Value.GetProperty("releaseDeclared").GetBoolean().Should().BeTrue();
        reader.PlatformReleaseStatusCalls.Should().Be(1);
        reader.LastPrincipal.Should().BeSameAs(context.User);
    }

    [UnitTest]
    public async Task DeployOperationsTool_Invoke_PassesFiltersToReader()
    {
        var reader = new FakePlatformOpsReader();
        var context = BuildContext(reader);
        var tool = new DeployOperationsTool(NullLogger<DeployOperationsTool>.Instance);
        var arguments = Json(
            """
            {
              "operationId": "op-1",
              "status": "Submitted",
              "kind": "Deploy",
              "page": 2,
              "pageSize": 25
            }
            """);

        var result = await tool.InvokeAsync(context, arguments, CancellationToken.None);

        result.IsError.Should().BeFalse();
        reader.DeployOperationsArgument.Should().NotBeNull();
        reader.DeployOperationsArgument!.OperationId.Should().Be("op-1");
        reader.DeployOperationsArgument.Status.Should().Be("Submitted");
        reader.DeployOperationsArgument.Kind.Should().Be("Deploy");
        reader.DeployOperationsArgument.Page.Should().Be(2);
        reader.DeployOperationsArgument.PageSize.Should().Be(25);
    }

    [UnitTest]
    public async Task SupportedOperationKindsTool_Invoke_ReturnsLiveReaderPayload()
    {
        var reader = new FakePlatformOpsReader();
        var context = BuildContext(reader);
        var tool = new SupportedOperationKindsTool(NullLogger<SupportedOperationKindsTool>.Instance);

        var result = await tool.InvokeAsync(context, Json("{}"), CancellationToken.None);

        result.IsError.Should().BeFalse();
        result.StructuredContent!.Value.GetProperty("supportedKinds")
            .EnumerateArray()
            .Select(element => element.GetString())
            .Should()
            .Equal("AdminConfigChange", "Deploy");
        reader.SupportedOperationKindsCalls.Should().Be(1);
        reader.LastPrincipal.Should().BeSameAs(context.User);
    }

    [UnitTest]
    public async Task ProposeRollbackTool_Invoke_PassesArgumentsAndReturnsProposal()
    {
        var reader = new FakePlatformOpsReader();
        var context = BuildContext(reader);
        var tool = new ProposeRollbackTool(NullLogger<ProposeRollbackTool>.Instance);
        var arguments = Json(
            """
            {
              "targetId": "serving-us-west",
              "toRevision": "rev-9",
              "reason": "SLO regression",
              "idempotencyKey": "rb-1",
              "parameterOverrides": { "activePort": "5102" }
            }
            """);

        var result = await tool.InvokeAsync(context, arguments, CancellationToken.None);

        result.IsError.Should().BeFalse();
        result.StructuredContent!.Value.GetProperty("proposalId").GetString().Should().Be("proposal-1");
        reader.RollbackArgument.Should().NotBeNull();
        reader.RollbackArgument!.TargetId.Should().Be("serving-us-west");
        reader.RollbackArgument.ToRevision.Should().Be("rev-9");
        reader.RollbackArgument.Reason.Should().Be("SLO regression");
        reader.RollbackArgument.IdempotencyKey.Should().Be("rb-1");
        reader.RollbackArgument.ParameterOverrides.Should().Contain("activePort", "5102");
    }

    private static DefaultHttpContext BuildContext(IMcpPlatformOpsReader reader)
    {
        var services = new ServiceCollection();
        services.AddSingleton(reader);
        var provider = services.BuildServiceProvider();

        return new DefaultHttpContext
        {
            RequestServices = provider,
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.Name, "mcp-test")],
                "Test"))
        };
    }

    private static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private sealed class FakePlatformOpsReader : IMcpPlatformOpsReader
    {
        public int PlatformReleaseStatusCalls { get; private set; }

        public int SupportedOperationKindsCalls { get; private set; }

        public ClaimsPrincipal? LastPrincipal { get; private set; }

        public McpDeployOperationsArgument? DeployOperationsArgument { get; private set; }

        public McpProposeRollbackArgument? RollbackArgument { get; private set; }

        public Task<JsonElement> GetPlatformReleaseStatusAsync(
            ClaimsPrincipal principal,
            CancellationToken cancellationToken)
        {
            LastPrincipal = principal;
            PlatformReleaseStatusCalls++;
            return Task.FromResult(Json("""{"releaseDeclared":true,"isCoVersioned":true,"serving":[],"execution":[],"skewedIds":[]}"""));
        }

        public Task<JsonElement> GetDeployOperationsAsync(
            ClaimsPrincipal principal,
            McpDeployOperationsArgument argument,
            CancellationToken cancellationToken)
        {
            LastPrincipal = principal;
            DeployOperationsArgument = argument;
            return Task.FromResult(Json("""{"items":[],"page":1,"pageSize":50,"totalCount":0,"hasMore":false}"""));
        }

        public Task<McpSupportedOperationKindsOutput> GetSupportedOperationKindsAsync(
            ClaimsPrincipal principal,
            CancellationToken cancellationToken)
        {
            LastPrincipal = principal;
            SupportedOperationKindsCalls++;
            return Task.FromResult(new McpSupportedOperationKindsOutput
            {
                SupportedKinds = ["AdminConfigChange", "Deploy"]
            });
        }

        public Task<McpProposeOperationOutput> ProposeRollbackAsync(
            ClaimsPrincipal principal,
            McpProposeRollbackArgument argument,
            CancellationToken cancellationToken)
        {
            LastPrincipal = principal;
            RollbackArgument = argument;
            return Task.FromResult(new McpProposeOperationOutput
            {
                Outcome = "ProposalCreated",
                RequiresApproval = true,
                ProposalId = "proposal-1",
                ResourceUri = "honua://proposals/proposal-1",
            });
        }

        public Task<McpProposeOperationOutput> ProposeFindingAsync(ClaimsPrincipal principal, McpProposeFindingArgument argument, CancellationToken cancellationToken)
            => Proposal(principal);

        public Task<McpProposeOperationOutput> ProposeDeployPlanAsync(ClaimsPrincipal principal, McpDeployMutationArgument argument, CancellationToken cancellationToken)
            => Proposal(principal);

        public Task<McpProposeOperationOutput> ProposeDeployOperationAsync(ClaimsPrincipal principal, McpDeployMutationArgument argument, CancellationToken cancellationToken)
            => Proposal(principal);

        public Task<McpProposeOperationOutput> ProposePlatformReleaseConvergenceAsync(ClaimsPrincipal principal, McpPlatformReleaseConvergenceArgument argument, CancellationToken cancellationToken)
            => Proposal(principal);

        private Task<McpProposeOperationOutput> Proposal(ClaimsPrincipal principal)
        {
            LastPrincipal = principal;
            return Task.FromResult(new McpProposeOperationOutput { Outcome = "ProposalCreated", RequiresApproval = true, ProposalId = "proposal-1" });
        }

        public Task<McpProposeOperationOutput> ProposeMetadataReleaseAsync(ClaimsPrincipal principal, McpMetadataReleaseMutationArgument argument, CancellationToken cancellationToken)
            => Task.FromResult(new McpProposeOperationOutput());
    }
}
