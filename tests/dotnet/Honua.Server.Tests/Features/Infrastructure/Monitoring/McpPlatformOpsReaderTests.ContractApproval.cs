// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Ai.Protocols.Mcp.Models;
using Honua.ControlPlane;
using Honua.ControlPlane.Executors;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Xunit;

namespace Honua.Server.Tests.Features.Infrastructure.Monitoring;

// A convergence proposal lists the contract migrations it asks approval for, so the one human
// approval of the sealed proposal covers exactly those scripts.
public sealed partial class McpPlatformOpsReaderTests
{
    private const string ListedContractScript = "Honua.Server.Migrations.183_RemoveUnusedFeatureIndexes.sql";
    private const string ListedContractNonce = "0123456789ABCDEF";

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task ProposePlatformReleaseConvergence_ContractApproval_SealsTheListedPlanForComposeOnly()
    {
        var gateway = ConvergenceGateway();
        using var services = CreateServices(gateway);
        var reader = CreateReader(options: ContractApprovalOptions(), services: services);

        var output = await reader.ProposePlatformReleaseConvergenceAsync(
            CreatePrincipal(),
            new McpPlatformReleaseConvergenceArgument
            {
                IdempotencyKey = "caller-key",
                ContractApproval = new McpContractApprovalArgument { Scripts = [" " + ListedContractScript + " "], Nonce = ListedContractNonce }
            },
            CancellationToken.None);

        output.Targets.Should().ContainSingle(target => target.TargetId == "compose-local" && target.ProposalId == "proposal-converge");
        output.Targets.Should().ContainSingle(target => target.TargetId == "ecs-remote" && target.Outcome == "blocked");
        gateway.ProposalCalls.Should().Be(1);
        gateway.LastRequest!.IdempotencyKey.Should().Be("caller-key:compose-local:contract:0123456789abcdef");
        var payload = DeployExecutionPayload.Parse(gateway.LastRequest.ExecutionPayload)!;
        payload.ParameterOverrides.Should().BeEquivalentTo(new Dictionary<string, string>
        {
            [ComposeContractApproval.NonceParameterKey] = ListedContractNonce,
            [ComposeContractApproval.ScriptsParameterKey] = ListedContractScript
        });
    }

    [UnitTheory]
    [InlineData(ListedContractScript, "not-a-nonce!")]
    [InlineData("", ListedContractNonce)]
    [InlineData("bad script;name", ListedContractNonce)]
    [Operation(Operations.TestInfrastructure)]
    public async Task ProposePlatformReleaseConvergence_MalformedContractApproval_IsRejectedBeforeSealing(string script, string nonce)
    {
        var gateway = ConvergenceGateway();
        using var services = CreateServices(gateway);
        var reader = CreateReader(options: ContractApprovalOptions(), services: services);
        var argument = new McpPlatformReleaseConvergenceArgument
        {
            IdempotencyKey = "caller-key",
            ContractApproval = new McpContractApprovalArgument { Scripts = script.Length == 0 ? [] : [script], Nonce = nonce }
        };

        var act = () => reader.ProposePlatformReleaseConvergenceAsync(CreatePrincipal(), argument, CancellationToken.None);

        await act.Should().ThrowAsync<Honua.Geoprocessing.GeoprocessingValidationException>();
        gateway.ProposalCalls.Should().Be(0);
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task ProposePlatformReleaseConvergence_NullContractScript_IsAValidationError()
    {
        var gateway = ConvergenceGateway();
        using var services = CreateServices(gateway);
        var reader = CreateReader(options: ContractApprovalOptions(), services: services);
        var argument = new McpPlatformReleaseConvergenceArgument
        {
            IdempotencyKey = "caller-key",
            ContractApproval = new McpContractApprovalArgument { Scripts = [null!], Nonce = ListedContractNonce }
        };

        var act = () => reader.ProposePlatformReleaseConvergenceAsync(CreatePrincipal(), argument, CancellationToken.None);

        await act.Should().ThrowAsync<Honua.Geoprocessing.GeoprocessingValidationException>();
        gateway.ProposalCalls.Should().Be(0);
    }

    private static ControlPlaneOptions ContractApprovalOptions()
        => new()
        {
            PlatformRelease = new() { Version = "2026.1-rc.2", ServingArtifactReference = "sha256:" + new string('a', 64) },
            DeployTargets =
            [
                new() { TargetId = "compose-local", TargetKind = DeployTargetKind.LocalCompose },
                new() { TargetId = "ecs-remote", TargetKind = DeployTargetKind.AwsEcs }
            ]
        };
}
