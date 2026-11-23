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
        var key = gateway.LastRequest!.IdempotencyKey;
        key.Should().MatchRegex("^caller-key:compose-local:contract:[0-9a-f]{32}$");
        var payload = DeployExecutionPayload.Parse(gateway.LastRequest!.ExecutionPayload)!;
        payload.ParameterOverrides.Should().BeEquivalentTo(new Dictionary<string, string>
        {
            [ComposeContractApproval.NonceParameterKey] = ListedContractNonce,
            [ComposeContractApproval.ScriptsParameterKey] = ListedContractScript
        });
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task ProposePlatformReleaseConvergence_DifferentContractPlan_GetsItsOwnProposalKey()
    {
        var gateway = ConvergenceGateway();
        using var services = CreateServices(gateway);
        var reader = CreateReader(options: ContractApprovalOptions(), services: services);

        async Task<string?> KeyFor(string[] scripts, string nonce)
        {
            await reader.ProposePlatformReleaseConvergenceAsync(CreatePrincipal(), new McpPlatformReleaseConvergenceArgument
            {
                IdempotencyKey = "caller-key",
                ContractApproval = new McpContractApprovalArgument { Scripts = scripts, Nonce = nonce }
            }, CancellationToken.None);
            return gateway.LastRequest!.IdempotencyKey;
        }

        var listed = await KeyFor([ListedContractScript, "B.sql"], ListedContractNonce);
        var reordered = await KeyFor(["B.sql", ListedContractScript], ListedContractNonce.ToLowerInvariant());
        var corrected = await KeyFor([ListedContractScript], ListedContractNonce);
        var renonced = await KeyFor([ListedContractScript, "B.sql"], "fedcba9876543210");

        reordered.Should().Be(listed);
        corrected.Should().NotBe(listed);
        renonced.Should().NotBe(listed);
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
