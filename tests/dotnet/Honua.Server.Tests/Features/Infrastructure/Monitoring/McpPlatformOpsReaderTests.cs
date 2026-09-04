// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Honua.Ai.Protocols.Mcp.Tools;
using Honua.Ai.Protocols.Mcp.Models;
using Honua.Ai.StudioAiProxy;
using Honua.Ai.StudioAiProxy.Domain;
using Honua.ControlPlane;
using Honua.ControlPlane.Executors;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Authorization;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Guardrails.Domain;
using Honua.Core.Features.Licensing.Domain;
using Honua.Core.Features.Operations.Abstractions;
using Honua.Core.Features.Operations.Domain;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Monitoring;
using Honua.Server.Tests.Features.Infrastructure.ControlPlane;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using StackExchange.Redis;

namespace Honua.Server.Tests.Features.Infrastructure.Monitoring;

/// <summary>
/// Redis-backed integration coverage for signed MCP platform-ops proposals (#3888).
/// </summary>
[Protocol(TestProtocols.TestQuality)]
[Collection("Redis")]
public sealed class McpPlatformOpsReaderIntegrationTests(RedisFixture redis)
{
    [IntegrationTheory]
    [Operation(Operations.TestInfrastructure)]
    [InlineData(ProposeDeployOperationTool.ToolName, OperationClass.Deploy)]
    [InlineData(ProposeMetadataReleaseTool.ToolName, OperationClass.MetadataRelease)]
    public async Task VerifiedModelToolCall_SealsAwaitingApproval_WithoutActuation(
        string toolName,
        OperationClass operationClass)
    {
        var idempotencyKey = $"signed-transcript-{Guid.NewGuid():N}";
        var argumentJson = toolName == ProposeDeployOperationTool.ToolName
            ? $$"""{"targetId":"candidate-a","desiredRevision":"sha256:release-a","idempotencyKey":"{{idempotencyKey}}"}"""
            : $$"""{"packageId":"package-a","targetEnvironment":"candidate-a","resourceSemanticId":"roads","newFieldName":"speed_limit","idempotencyKey":"{{idempotencyKey}}"}""";
        using var argumentDocument = JsonDocument.Parse(argumentJson);
        var signedArguments = argumentDocument.RootElement.Clone();
        var request = new StudioAiChatRequest
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
        var events = new[]
        {
            new StudioAiChatEvent { Type = StudioAiChatEventType.MessageStart, Model = "claude-sonnet-4-5" },
            new StudioAiChatEvent { Type = StudioAiChatEventType.ToolCallStart, ToolCallId = "call-1", ToolName = toolName },
            new StudioAiChatEvent { Type = StudioAiChatEventType.ToolCallStop, ToolCallId = "call-1", ToolArguments = signedArguments },
            new StudioAiChatEvent { Type = StudioAiChatEventType.MessageStop, StopReason = StudioAiStopReason.ToolCall }
        };
        var privateKey = new Ed25519PrivateKeyParameters(Enumerable.Range(1, 32).Select(value => (byte)value).ToArray(), 0);
        var signer = new StudioAiTranscriptSigner(Options.Create(new StudioAiProxyConfiguration()), TimeProvider.System);
        var provenance = signer.Sign(
            new StudioAiTranscriptSigner.SigningKey("candidate-key", privateKey, privateKey.GeneratePublicKey().GetEncoded()),
            request, "anthropic", "claude-sonnet-4-5", events);

        var signedBytes = Convert.FromBase64String(provenance.CanonicalTranscript);
        var verifier = new Ed25519Signer();
        verifier.Init(false, privateKey.GeneratePublicKey());
        verifier.BlockUpdate(signedBytes, 0, signedBytes.Length);
        verifier.VerifySignature(Convert.FromBase64String(provenance.Signature)).Should().BeTrue();
        using var transcript = JsonDocument.Parse(signedBytes);
        transcript.RootElement.GetProperty("candidateId").GetString().Should().Be("candidate-a");
        var verifiedEvents = JsonSerializer.Deserialize(
            Convert.FromBase64String(transcript.RootElement.GetProperty("providerEvents").GetString()!),
            StudioAiProxyJsonContext.Default.ListStudioAiChatEvent)!;
        var verifiedCall = verifiedEvents.Single(item => item.Type == StudioAiChatEventType.ToolCallStop);
        var verifiedToolName = verifiedEvents.Single(item => item.Type == StudioAiChatEventType.ToolCallStart).ToolName;

        await using var multiplexer = await ConnectionMultiplexer.ConnectAsync(redis.ConnectionString);
        var proposalStore = new RedisOperationProposalStore(
            multiplexer, NullLogger<RedisOperationProposalStore>.Instance);
        var ladder = Substitute.For<Honua.Core.Features.Guardrails.Abstractions.IGuardrailLadder>();
        var decision = new GuardrailDecision(GuardrailTier.RequiresApproval, operationClass, HonuaEdition.Enterprise, "model-proposal");
        ladder.Resolve(operationClass).Returns(decision);
        ladder.Resolve(operationClass, Arg.Any<string?>()).Returns(decision);
        var actuator = Substitute.For<Honua.Core.Features.ControlPlane.Abstractions.IOperationExecutor>();
        actuator.OperationClass.Returns(operationClass);
        actuator.PlanAsync(Arg.Any<OperationGatewayRequest>(), Arg.Any<CancellationToken>())
            .Returns(new OperationProposalPlan());
        var gateway = CanonicalOperationGatewayTestComposition.Build(proposalStore, ladder, [actuator]);
        using var readerServices = McpPlatformOpsReaderTests.CreateServices(gateway);
        var reader = McpPlatformOpsReaderTests.CreateReader(services: readerServices);
        using var toolServices = new ServiceCollection().AddSingleton<IMcpPlatformOpsReader>(reader).BuildServiceProvider();
        var context = new DefaultHttpContext
        {
            RequestServices = toolServices,
            User = McpPlatformOpsReaderTests.CreatePrincipal()
        };
        IMcpTool tool = verifiedToolName switch
        {
            ProposeDeployOperationTool.ToolName => new ProposeDeployOperationTool(NullLogger<ProposeDeployOperationTool>.Instance),
            ProposeMetadataReleaseTool.ToolName => new ProposeMetadataReleaseTool(NullLogger<ProposeMetadataReleaseTool>.Instance),
            _ => throw new InvalidOperationException($"Verified tool '{verifiedToolName}' is not a governed proposal tool.")
        };

        var result = await tool.InvokeAsync(context, verifiedCall.ToolArguments, CancellationToken.None);

        result.IsError.Should().BeFalse();
        result.StructuredContent!.Value.GetProperty("requiresApproval").GetBoolean().Should().BeTrue();
        var proposalId = result.StructuredContent.Value.GetProperty("proposalId").GetString();
        var persisted = await proposalStore.GetAsync(proposalId!);
        persisted.Should().NotBeNull("the real Redis store must round-trip the sealed proposal");
        persisted!.Status.Should().Be(OperationProposalStatus.AwaitingApproval);
        persisted.Kind.Should().Be(operationClass);
        persisted.Plan.ExecutionPayload.Should().Contain("candidate-a");
        await actuator.DidNotReceive().ExecuteAsync(
            Arg.Any<OperationGatewayRequest>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }
}

/// <summary>
/// Unit coverage for the server-side MCP platform-ops adapter (#2566).
/// </summary>
[Protocol(TestProtocols.TestQuality)]
public sealed class McpPlatformOpsReaderTests
{
    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public void ValidateFindingCandidateBinding_DeployTargetMismatch_IsRejected()
    {
        var payload = new DeployExecutionPayload
        {
            TargetId = "candidate-b",
            DesiredRevision = "sha256:release-b"
        }.Serialize();

        var act = () => McpPlatformOpsReader.ValidateFindingCandidateBinding(
            OperationClass.Deploy,
            payload,
            "candidate-a");

        act.Should().Throw<Honua.Geoprocessing.GeoprocessingValidationException>()
            .WithMessage("*does not match the certified candidate*");
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task GetPlatformReleaseStatus_Authorized_UsesOpsReadPolicyAndReturnsProjection()
    {
        var principal = CreatePrincipal();
        var authorization = CreateAuthorization(AuthorizationResult.Success());

        using var services = CreateServices();
        var reader = CreateReader(authorization: authorization, services: services);

        var result = await reader.GetPlatformReleaseStatusAsync(principal, CancellationToken.None);

        result.GetProperty("releaseDeclared").GetBoolean().Should().BeTrue();
        result.GetProperty("releaseVersion").GetString().Should().Be("2026.07.01");
        result.GetProperty("skewedIds")
            .EnumerateArray()
            .Select(element => element.GetString())
            .Should()
            .Contain("serving-pinned");
        result.GetProperty("serving")
            .EnumerateArray()
            .Select(element => element.GetProperty("id").GetString())
            .Should()
            .Contain("serving-us-west");
        result.GetProperty("execution")
            .EnumerateArray()
            .Select(element => element.GetProperty("id").GetString())
            .Should()
            .Contain("gp-gdal");

        await authorization.Received(1).AuthorizeAsync(
            principal,
            Arg.Is<object>(resource => IsOpsReadResource(resource, principal)),
            AuthenticationExtensions.OpsReadPolicy);
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task GetSupportedOperationKinds_Authorized_UsesOpsReadPolicyAndReturnsSortedKinds()
    {
        var principal = CreatePrincipal();
        var authorization = CreateAuthorization(AuthorizationResult.Success());
        var catalog = new MutableExecutorCatalog(
            [OperationClass.MetadataRelease, OperationClass.Deploy, OperationClass.AdminConfigChange]);

        using var services = CreateServices(catalog: catalog);
        var reader = CreateReader(authorization: authorization, services: services);

        var result = await reader.GetSupportedOperationKindsAsync(principal, CancellationToken.None);

        result.SupportedKinds.Should().Equal("AdminConfigChange", "Deploy", "MetadataRelease");
        await authorization.Received(1).AuthorizeAsync(
            principal,
            Arg.Is<object>(resource => IsOpsReadResource(resource, principal)),
            AuthenticationExtensions.OpsReadPolicy);
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task GetSupportedOperationKinds_EmptyCatalog_ReturnsEmptyKinds()
    {
        using var services = CreateServices(catalog: new MutableExecutorCatalog([]));
        var reader = CreateReader(services: services);

        var result = await reader.GetSupportedOperationKindsAsync(CreatePrincipal(), CancellationToken.None);

        result.SupportedKinds.Should().BeEmpty();
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task GetSupportedOperationKinds_CatalogChanges_ReflectsLatestKinds()
    {
        var catalog = new MutableExecutorCatalog([OperationClass.Deploy]);
        using var services = CreateServices(catalog: catalog);
        var reader = CreateReader(services: services);

        var initial = await reader.GetSupportedOperationKindsAsync(CreatePrincipal(), CancellationToken.None);
        catalog.SupportedKinds = [OperationClass.AdminConfigChange, OperationClass.MetadataRelease];
        var changed = await reader.GetSupportedOperationKindsAsync(CreatePrincipal(), CancellationToken.None);

        initial.SupportedKinds.Should().Equal("Deploy");
        changed.SupportedKinds.Should().Equal("AdminConfigChange", "MetadataRelease");
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task GetSupportedOperationKinds_WithoutOpsReadPermission_IsRejected()
    {
        using var services = CreateServices(catalog: new MutableExecutorCatalog([OperationClass.Deploy]));
        var reader = CreateReader(
            authorization: CreateAuthorization(AuthorizationResult.Failed()),
            services: services);

        var act = () => reader.GetSupportedOperationKindsAsync(CreatePrincipal(), CancellationToken.None);

        await act.Should().ThrowAsync<Honua.Geoprocessing.GeoprocessingAuthorizationException>();
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task GetDeployOperations_List_TranslatesFiltersAndClampsPageSize()
    {
        var store = new RecordingWorkflowOperationStore(
            BuildDeployOperation(
                "op-1",
                targetId: "serving-us-west",
                desiredRevision: "rev-2",
                currentRevision: "rev-1",
                status: WorkflowOperationStatus.Submitted));
        using var services = CreateServices();
        var reader = CreateReader(store: store, services: services);

        var result = await reader.GetDeployOperationsAsync(
            CreatePrincipal(),
            new McpDeployOperationsArgument
            {
                Status = "Submitted",
                Kind = "Deploy",
                Page = 0,
                PageSize = 999,
            },
            CancellationToken.None);

        store.LastQuery.Should().NotBeNull();
        store.LastQuery!.Status.Should().Be(WorkflowOperationStatus.Submitted);
        store.LastQuery.Kind.Should().Be(WorkflowOperationKind.Deploy);
        store.LastQuery.Page.Should().Be(1);
        store.LastQuery.PageSize.Should().Be(200);
        result.GetProperty("page").GetInt32().Should().Be(1);
        result.GetProperty("pageSize").GetInt32().Should().Be(200);
        result.GetProperty("items")
            .EnumerateArray()
            .Should()
            .ContainSingle()
            .Which
            .GetProperty("operationId")
            .GetString()
            .Should()
            .Be("op-1");
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task GetDeployOperations_ById_ReturnsSingleItemEnvelope()
    {
        var store = new RecordingWorkflowOperationStore(
            BuildDeployOperation(
                "op-7",
                targetId: "serving-us-west",
                desiredRevision: "rev-7",
                currentRevision: "rev-6",
                status: WorkflowOperationStatus.Succeeded));
        using var services = CreateServices();
        var reader = CreateReader(store: store, services: services);

        var result = await reader.GetDeployOperationsAsync(
            CreatePrincipal(),
            new McpDeployOperationsArgument { OperationId = " op-7 " },
            CancellationToken.None);

        result.GetProperty("page").GetInt32().Should().Be(1);
        result.GetProperty("pageSize").GetInt32().Should().Be(1);
        result.GetProperty("totalCount").GetInt32().Should().Be(1);
        result.GetProperty("items")
            .EnumerateArray()
            .Should()
            .ContainSingle()
            .Which
            .GetProperty("target")
            .GetProperty("desiredRevision")
            .GetString()
            .Should()
            .Be("rev-7");
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task ProposeRollback_ExplicitRevision_RoutesForwardDeployPayload()
    {
        var store = new RecordingWorkflowOperationStore(
            BuildDeployOperation(
                "op-latest",
                targetId: "serving-us-west",
                desiredRevision: "rev-10",
                currentRevision: "rev-9",
                status: WorkflowOperationStatus.Succeeded));
        var gateway = new RecordingGateway(new OperationGatewayResult
        {
            Outcome = OperationGatewayOutcome.ProposalCreated,
            Decision = new GuardrailDecision(
                GuardrailTier.RequiresApproval,
                OperationClass.Deploy,
                HonuaEdition.Pro,
                "test"),
            ProposalId = "proposal-1",
            Message = "queued for approval",
        });

        using var services = CreateServices(
            gateway,
            new StaticExecutorCatalog([OperationClass.MetadataRelease, OperationClass.Deploy]));
        var reader = CreateReader(store: store, services: services);

        var output = await reader.ProposeRollbackAsync(
            CreatePrincipal(),
            new McpProposeRollbackArgument
            {
                TargetId = " serving-us-west ",
                ToRevision = " rev-9 ",
                Reason = "rollback bad release",
                IdempotencyKey = "rollback-key",
                ParameterOverrides = new Dictionary<string, string> { ["activePort"] = "5102" },
            },
            CancellationToken.None);

        output.Outcome.Should().Be(nameof(OperationGatewayOutcome.ProposalCreated));
        output.RequiresApproval.Should().BeTrue();
        output.ProposalId.Should().Be("proposal-1");
        output.ResourceUri.Should().NotBeNullOrWhiteSpace();
        output.SupportedKinds.Should().Equal("Deploy", "MetadataRelease");
        gateway.LastRequest.Should().NotBeNull();
        gateway.RouteCalls.Should().Be(0, "rollback proposals must never use the direct-execution route");
        gateway.ProposalCalls.Should().Be(1);
        gateway.LastRequest!.Kind.Should().Be(OperationClass.Deploy);
        gateway.LastRequest.RequestedBy.Should().Be("test:subject:-:ops-agent");
        gateway.LastRequest.RequestedByAgent.Should().Be("agent:test:subject:-:ops-agent");
        gateway.LastRequest.Reason.Should().Be("rollback bad release");
        gateway.LastRequest.IdempotencyKey.Should().Be("rollback-key");
        await services.GetRequiredService<IOperationEnvelopeFactory>().Received(1).CreateAcceptedAsync(
            "control-plane.deploy",
            Arg.Is<OperationPolicyContext>(context =>
                context.AuthorizationOutcome == "admin-policy-authorized" &&
                !context.ScopeGoverned &&
                context.PrincipalId == "test:subject:-:ops-agent"),
            Arg.Any<CancellationToken>());

        var payload = DeployExecutionPayload.Parse(gateway.LastRequest.ExecutionPayload);
        payload.Should().NotBeNull();
        payload!.TargetId.Should().Be("serving-us-west");
        payload.ParameterOverrides.Should().Contain("activePort", "5102");
        payload.DesiredRevision.Should().Be("rev-9");
        payload.CurrentRevision.Should().Be("rev-10");
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task ProposeRollback_WithoutRevision_UsesSecondNewestSucceededDeploy()
    {
        var store = new RecordingWorkflowOperationStore(
            BuildDeployOperation(
                "op-new",
                targetId: "serving-us-west",
                desiredRevision: "rev-10",
                currentRevision: "rev-9",
                status: WorkflowOperationStatus.Succeeded,
                createdAt: DateTimeOffset.UtcNow),
            BuildDeployOperation(
                "op-prior",
                targetId: "serving-us-west",
                desiredRevision: "rev-9",
                currentRevision: "rev-8",
                status: WorkflowOperationStatus.Succeeded,
                createdAt: DateTimeOffset.UtcNow.AddMinutes(-10)));
        var gateway = new RecordingGateway(new OperationGatewayResult
        {
            Outcome = OperationGatewayOutcome.ProposalCreated,
            Decision = new GuardrailDecision(
                GuardrailTier.RequiresApproval,
                OperationClass.Deploy,
                HonuaEdition.Pro,
                "model-facing-proposal-requires-approval"),
            ProposalId = "proposal-rollback",
            Message = "queued for approval",
        });

        using var services = CreateServices(gateway);
        var reader = CreateReader(store: store, services: services);

        var output = await reader.ProposeRollbackAsync(
            CreatePrincipal(),
            new McpProposeRollbackArgument { TargetId = "serving-us-west" },
            CancellationToken.None);

        output.Outcome.Should().Be(nameof(OperationGatewayOutcome.ProposalCreated));
        output.RequiresApproval.Should().BeTrue();
        gateway.RouteCalls.Should().Be(0, "a direct-execute edition default cannot bypass approval");
        gateway.ProposalCalls.Should().Be(1);
        gateway.LastRequest.Should().NotBeNull();
        gateway.LastRequest!.IdempotencyKey.Should().Be("rollback:serving-us-west:rev-9");

        var payload = DeployExecutionPayload.Parse(gateway.LastRequest.ExecutionPayload);
        payload.Should().NotBeNull();
        payload!.DesiredRevision.Should().Be("rev-9");
        payload.CurrentRevision.Should().Be("rev-10");
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task ProposeRollback_ReadOnlyOAuthScope_IsDeniedBeforeProposalPersistence()
    {
        var gateway = new RecordingGateway(new OperationGatewayResult
        {
            Outcome = OperationGatewayOutcome.ProposalCreated,
            Decision = new GuardrailDecision(GuardrailTier.RequiresApproval, OperationClass.Deploy, HonuaEdition.Pro, "test"),
        });
        using var services = CreateServices(gateway);
        var reader = CreateReader(scopeAuthorizer: new OperatorScopeAuthorizer(), services: services);
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, "ops-agent"),
            new Claim(OperatorScopeCatalog.ScopeGovernedClaimType, OperatorScopeCatalog.ScopeGovernedClaimValue),
            new Claim(OperatorScopeCatalog.ScopeClaimType, OperatorScopeCatalog.Read),
        ], "test"));

        var act = () => reader.ProposeRollbackAsync(principal,
            new McpProposeRollbackArgument { TargetId = "serving-us-west", ToRevision = "rev-9" },
            CancellationToken.None);

        await act.Should().ThrowAsync<Honua.Geoprocessing.GeoprocessingAuthorizationException>();
        gateway.ProposalCalls.Should().Be(0);
        gateway.RouteCalls.Should().Be(0);
    }

    internal static McpPlatformOpsReader CreateReader(
        ControlPlaneOptions? options = null,
        IWorkflowOperationStore? store = null,
        IAuthorizationService? authorization = null,
        IOperatorScopeAuthorizer? scopeAuthorizer = null,
        IServiceProvider? services = null)
        => new(
            new StaticOptionsMonitor<ControlPlaneOptions>(options ?? CreateOptions()),
            CreateDeployService(store ?? new RecordingWorkflowOperationStore()),
            authorization ?? CreateAuthorization(AuthorizationResult.Success()),
            scopeAuthorizer ?? NullOperatorScopeAuthorizer.Instance,
            services ?? CreateServices());

    private static DeployWorkflowService CreateDeployService(IWorkflowOperationStore store)
        => new(
            Substitute.For<IDeployTargetRegistry>(),
            [store],
            Array.Empty<IDeployBackend>(),
            Substitute.For<IOperatorApprovalEvaluator>(),
            NullLogger<DeployWorkflowService>.Instance);

    private static IAuthorizationService CreateAuthorization(AuthorizationResult result)
    {
        var authorization = Substitute.For<IAuthorizationService>();
        authorization.AuthorizeAsync(
                Arg.Any<ClaimsPrincipal>(),
                Arg.Any<object>(),
                AuthenticationExtensions.OpsReadPolicy)
            .Returns(result);
        authorization.AuthorizeAsync(
                Arg.Any<ClaimsPrincipal>(),
                Arg.Any<object>(),
                AuthenticationExtensions.AdminPolicy)
            .Returns(result);
        return authorization;
    }

    internal static ServiceProvider CreateServices(
        IOperationGateway? gateway = null,
        IOperationExecutorCatalog? catalog = null)
    {
        var services = new ServiceCollection();
        var envelopeFactory = Substitute.For<IOperationEnvelopeFactory>();
        var now = DateTimeOffset.UtcNow;
        envelopeFactory.CreateAcceptedAsync(
                Arg.Any<string>(),
                Arg.Any<OperationPolicyContext>(),
                Arg.Any<CancellationToken>())
            .Returns(new OperationHandle
            {
                OperationInstanceId = "opinst-rollback",
                OperationId = "control-plane.deploy",
                Status = OperationHandleStatus.Accepted,
                CorrelationId = "corr-rollback",
                AuditId = "audit-rollback",
                CreatedAt = now,
                UpdatedAt = now,
            });
        services.AddSingleton(envelopeFactory);
        if (gateway is not null)
        {
            services.AddSingleton(gateway);
        }

        if (catalog is not null)
        {
            services.AddSingleton(catalog);
        }

        return services.BuildServiceProvider();
    }

    internal static ClaimsPrincipal CreatePrincipal()
        => new(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.Name, "ops-agent"),
                new Claim(ClaimTypes.NameIdentifier, "ops-agent"),
            ],
            "test"));

    private static bool IsOpsReadResource(object resource, ClaimsPrincipal principal)
        => resource is DefaultHttpContext context &&
            ReferenceEquals(context.User, principal) &&
            string.Equals(context.Request.Method, HttpMethods.Get, StringComparison.Ordinal);

    private static ControlPlaneOptions CreateOptions()
        => new()
        {
            PlatformRelease = new PlatformReleaseOptions
            {
                Version = "2026.07.01",
                ServingArtifactReference = "ghcr.io/honua/server:2026.07.01",
                Workers =
                [
                    new PlatformReleaseWorkerImageOptions
                    {
                        RuntimeProfile = "gdal",
                        ArtifactReference = "ghcr.io/honua/worker-gdal:2026.07.01",
                    },
                ],
            },
            DeployTargets =
            [
                new DeployTargetOptions
                {
                    TargetId = "serving-us-west",
                    TargetKind = DeployTargetKind.SelfHostedRolling,
                    Backend = "self-hosted",
                    Environment = "prod",
                    TargetName = "Serving west",
                },
                new DeployTargetOptions
                {
                    TargetId = "serving-pinned",
                    TargetKind = DeployTargetKind.SelfHostedRolling,
                    Backend = "self-hosted",
                    Environment = "prod",
                    TargetName = "Pinned",
                    ArtifactReference = "ghcr.io/honua/server:old",
                },
            ],
            ExecutionWorkloads =
            [
                new ExecutionWorkloadOptions
                {
                    WorkloadId = "gp-gdal",
                    TargetKind = BatchComputeTargetKind.LocalProcess,
                    Backend = "local-process",
                    WorkloadName = "GDAL workers",
                    RuntimeProfile = "gdal",
                },
            ],
        };

    private static WorkflowOperationRecord BuildDeployOperation(
        string operationId,
        string targetId,
        string desiredRevision,
        string? currentRevision,
        WorkflowOperationStatus status,
        DateTimeOffset? createdAt = null)
    {
        var created = createdAt ?? DateTimeOffset.UtcNow;
        return new WorkflowOperationRecord
        {
            OperationId = operationId,
            Kind = WorkflowOperationKind.Deploy,
            Status = status,
            CreatedAt = created,
            UpdatedAt = created,
            Deploy = new DeployOperationSpec
            {
                TargetId = targetId,
                TargetKind = DeployTargetKind.SelfHostedRolling,
                Backend = "self-hosted",
                Environment = "prod",
                TargetName = "Honua serving",
                CurrentRevision = currentRevision,
                DesiredRevision = desiredRevision,
            },
        };
    }

    private sealed class RecordingWorkflowOperationStore(params WorkflowOperationRecord[] operations) : IWorkflowOperationStore
    {
        private readonly Dictionary<string, WorkflowOperationRecord> _operations =
            operations.ToDictionary(operation => operation.OperationId, StringComparer.Ordinal);

        public WorkflowOperationQuery? LastQuery { get; private set; }

        public Task<bool> TryAcquireLeaseAsync(
            string operationId,
            string ownerId,
            TimeSpan leaseDuration,
            CancellationToken cancellationToken = default)
            => Task.FromResult(true);

        public Task<bool> RenewLeaseAsync(
            string operationId,
            string ownerId,
            TimeSpan leaseDuration,
            CancellationToken cancellationToken = default)
            => Task.FromResult(true);

        public Task ReleaseLeaseAsync(
            string operationId,
            string ownerId,
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<bool> TryCreateAsync(
            WorkflowOperationRecord operation,
            TimeSpan? ttl = null,
            CancellationToken cancellationToken = default)
        {
            var created = !_operations.ContainsKey(operation.OperationId);
            _operations[operation.OperationId] = operation;
            return Task.FromResult(created);
        }

        public Task<WorkflowOperationRecord?> GetAsync(
            string operationId,
            CancellationToken cancellationToken = default)
            => Task.FromResult(_operations.GetValueOrDefault(operationId));

        public Task<WorkflowOperationRecord?> GetByMetadataPackageIdAsync(
            string packageId,
            CancellationToken cancellationToken = default)
            => Task.FromResult(_operations.Values.FirstOrDefault(
                operation => string.Equals(operation.MetadataRelease?.PackageId, packageId, StringComparison.Ordinal)));

        public Task SetAsync(
            WorkflowOperationRecord operation,
            TimeSpan? ttl = null,
            CancellationToken cancellationToken = default)
        {
            _operations[operation.OperationId] = operation;
            return Task.CompletedTask;
        }

        public Task<bool> TrySetAsync(
            WorkflowOperationRecord operation,
            TimeSpan? ttl = null,
            CancellationToken cancellationToken = default)
        {
            _operations[operation.OperationId] = operation;
            return Task.FromResult(true);
        }

        public Task<IReadOnlyList<WorkflowOperationRecord>> ListActiveAsync(
            WorkflowOperationKind? kind = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<WorkflowOperationRecord>>(
                _operations.Values
                    .Where(operation => !kind.HasValue || operation.Kind == kind.Value)
                    .ToArray());

        public Task<WorkflowOperationPage> QueryAsync(
            WorkflowOperationQuery query,
            CancellationToken cancellationToken = default)
        {
            LastQuery = query;
            var filtered = _operations.Values
                .Where(operation => !query.Kind.HasValue || operation.Kind == query.Kind.Value)
                .Where(operation => !query.Status.HasValue || operation.Status == query.Status.Value)
                .OrderByDescending(operation => operation.CreatedAt)
                .ToArray();
            var items = filtered
                .Skip((query.Page - 1) * query.PageSize)
                .Take(query.PageSize)
                .ToArray();

            return Task.FromResult(new WorkflowOperationPage
            {
                Items = items,
                Page = query.Page,
                PageSize = query.PageSize,
                TotalCount = filtered.Length,
                HasMore = query.Page * query.PageSize < filtered.Length,
            });
        }

        public Task<WorkflowOperationRecord?> GetMostRecentSucceededDeployByTargetAsync(
            string targetId,
            CancellationToken cancellationToken = default)
            => Task.FromResult(_operations.Values
                .Where(operation =>
                    operation.Kind == WorkflowOperationKind.Deploy &&
                    operation.Status == WorkflowOperationStatus.Succeeded &&
                    string.Equals(operation.Deploy?.TargetId, targetId, StringComparison.Ordinal))
                .OrderByDescending(operation => operation.CreatedAt)
                .FirstOrDefault());
    }

    private sealed class RecordingGateway(OperationGatewayResult result) : IOperationGateway
    {
        public OperationGatewayRequest? LastRequest { get; private set; }

        public int RouteCalls { get; private set; }

        public int ProposalCalls { get; private set; }

        public Task<OperationGatewayResult> RouteAsync(
            OperationGatewayRequest request,
            CancellationToken cancellationToken = default)
        {
            RouteCalls++;
            LastRequest = request;
            return Task.FromResult(result);
        }

        public Task<OperationGatewayResult> CreateApprovalProposalAsync(
            string operationInstanceId,
            OperationGatewayRequest request,
            CancellationToken cancellationToken = default)
        {
            ProposalCalls++;
            LastRequest = request;
            return Task.FromResult(result);
        }

        public Task<OperationProposal?> ApplyApprovedProposalAsync(
            string proposalId,
            string approvedBy,
            CancellationToken cancellationToken = default)
            => Task.FromResult<OperationProposal?>(null);

        public Task<OperationProposal?> RejectProposalAsync(
            string proposalId,
            string rejectedBy,
            string reason,
            CancellationToken cancellationToken = default)
            => Task.FromResult<OperationProposal?>(null);
    }

    private sealed class StaticExecutorCatalog(IReadOnlyCollection<OperationClass> supportedKinds) : IOperationExecutorCatalog
    {
        public IReadOnlyCollection<OperationClass> SupportedKinds { get; } = supportedKinds;
    }

    private sealed class MutableExecutorCatalog(IReadOnlyCollection<OperationClass> supportedKinds) : IOperationExecutorCatalog
    {
        public IReadOnlyCollection<OperationClass> SupportedKinds { get; set; } = supportedKinds;
    }

    private sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
        where T : class
    {
        public T CurrentValue { get; } = value;

        public T Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
