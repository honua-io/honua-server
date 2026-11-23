// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.ControlPlane;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.TestKit.Attributes;
using Xunit;

namespace Honua.Server.Tests.Features.Infrastructure.ControlPlane;

// An approved release proposal authorizes the contract migrations it lists, and only those: the
// executor applies them once when they are exactly the candidate's pending contract plan.
public sealed partial class ComposeProposalExecutorTests
{
    private const string ApprovalSetting = "HONUA_APPROVE_CONTRACT_MIGRATIONS=";

    [UnitTest]
    public async Task PrepareCandidate_ApprovedPlanMatches_AppliesItOnceWithTheNonce()
    {
        var docker = RefusingThen(new DockerProcessResult(0, MigrateOnlySummary("success", 0, "contract-annotated"), string.Empty));
        using var controller = new ComposeProjectController(ContractOptions(), docker);

        var refusal = await controller.PrepareCandidateAsync(Digest('a'), Approval(ContractScript, ApprovalNonce.ToUpperInvariant()), CancellationToken.None);

        Assert.Null(refusal);
        Assert.Equal(["run", "run"], docker.Subcommands);
        Assert.DoesNotContain(docker.Argv[0], argument => argument.StartsWith(ApprovalSetting, StringComparison.Ordinal));
        Assert.Contains(ApprovalSetting + ApprovalNonce.ToUpperInvariant(), docker.Argv[1]);
        Assert.Equal("honua", docker.Argv[1][^1]);
    }

    [UnitTheory]
    [InlineData("Honua.Server.Migrations.184_Other.sql", ApprovalNonce)]
    [InlineData(ContractScript, "fedcba9876543210")]
    public async Task PrepareCandidate_ApprovalForAnotherPlan_RefusesWithoutApplying(string script, string nonce)
    {
        var options = ContractOptions();
        var docker = new MigrateOnlyDocker(5, MigrateOnlySummary("contract-approval-required", 5, "contract-annotated"));
        using var controller = new ComposeProjectController(options, docker);

        var refusal = await controller.PrepareCandidateAsync(Digest('a'), Approval(script, nonce), CancellationToken.None);

        Assert.Equal(ComposeExecutorCodes.ContractApprovalRequired, refusal?.Code);
        Assert.Contains("does not list exactly", refusal!.Message, StringComparison.Ordinal);
        Assert.Contains(ContractScript, refusal.Message, StringComparison.Ordinal);
        Assert.Equal(["run", "rm", "up"], docker.Subcommands);
        Assert.DoesNotContain(docker.Argv[0], argument => argument.StartsWith(ApprovalSetting, StringComparison.Ordinal));
        Assert.False(File.Exists(Path.Join(options.ProjectDirectory, ComposeExecutorProtocol.OverrideFileName)));
    }

    [UnitTest]
    public async Task PrepareCandidate_ApprovedRunFindsANewPlan_RefusesAndRestartsThePrior()
    {
        var replanned = MigrateOnlySummary("contract-approval-required", 5, "contract-annotated").Replace(ApprovalNonce, "fedcba9876543210", StringComparison.Ordinal);
        var docker = RefusingThen(new DockerProcessResult(5, replanned, string.Empty));
        using var controller = new ComposeProjectController(ContractOptions(), docker);

        var refusal = await controller.PrepareCandidateAsync(Digest('a'), Approval(ContractScript, ApprovalNonce), CancellationToken.None);

        Assert.Equal(ComposeExecutorCodes.ContractApprovalRequired, refusal?.Code);
        Assert.Contains("fedcba9876543210", refusal!.Message, StringComparison.Ordinal);
        Assert.Equal(["run", "rm", "run", "rm", "up"], docker.Subcommands);
    }

    [UnitTest]
    public async Task PrepareCandidate_ApprovedRunFails_KeepsTheRecoveryPath()
    {
        var docker = RefusingThen(new DockerProcessResult(10, MigrateOnlySummary("migration-failed", 10, "contract-annotated"), string.Empty));
        using var controller = new ComposeProjectController(ContractOptions(), docker);

        var refusal = await controller.PrepareCandidateAsync(Digest('a'), Approval(ContractScript, ApprovalNonce), CancellationToken.None);

        Assert.Equal(ComposeExecutorCodes.ReadbackIncomplete, refusal?.Code);
        Assert.Equal(["run", "rm", "run", "rm"], docker.Subcommands);
    }

    [UnitTest]
    public async Task Execute_ContractApprovalOutsideConverge_RefusesBeforeDocker()
    {
        var docker = new RecordingDocker();
        using var controller = new ComposeProjectController(ContractOptions(), docker);

        var status = await controller.ExecuteAsync(new ComposeExecutorRequest { Op = "status", ContractApproval = Approval(ContractScript, ApprovalNonce) });
        var malformed = await controller.ExecuteAsync(new ComposeExecutorRequest
        {
            Op = "converge",
            Image = Digest('a'),
            ContractApproval = new ComposeContractApproval { Nonce = "not-hex!", Scripts = [ContractScript] }
        });

        Assert.Equal(ComposeExecutorCodes.ConfigUnsupported, status.Code);
        Assert.Equal(ComposeExecutorCodes.ConfigUnsupported, malformed.Code);
        Assert.Empty(docker.Argv);
    }

    [UnitTest]
    public void ContractApproval_RoundTripsOverTheSocketAndRejectsExtraMembers()
    {
        Assert.True(ComposeExecutorProtocol.TryReadRequest(
            "{\"op\":\"converge\",\"contractApproval\":{\"nonce\":\"" + ApprovalNonce + "\",\"scripts\":[\"" + ContractScript + "\"]}}",
            out var request,
            out _));
        Assert.Equal(ApprovalNonce, request!.ContractApproval?.Nonce);
        Assert.Equal([ContractScript], request.ContractApproval?.Scripts);

        Assert.False(ComposeExecutorProtocol.TryReadRequest(
            "{\"op\":\"converge\",\"contractApproval\":{\"nonce\":\"" + ApprovalNonce + "\",\"command\":\"sh\"}}",
            out _,
            out var failure));
        Assert.Equal(ComposeExecutorCodes.ScopeViolation, failure?.Code);
    }

    [UnitTest]
    public async Task Start_ApprovedConvergence_SendsTheListedPlanToTheExecutor()
    {
        var release = Digest('a');
        var client = new RecordingClient { Response = Ready(Digest('b')) };
        var operation = WithParameters(Operation(release, "approver"), ComposeContractApproval.ToParameters(ApprovalNonce, [ContractScript]));

        await Backend(client, release, "/tmp/honua-compose-unit.sock").StartAsync(operation);

        var converge = Assert.Single(client.Requests, request => request.Op == "converge");
        Assert.Equal(ApprovalNonce, converge.ContractApproval?.Nonce);
        Assert.Equal([ContractScript], converge.ContractApproval?.Scripts);
        Assert.All(client.Requests.Where(request => request.Op != "converge"), request => Assert.Null(request.ContractApproval));
    }

    [UnitTest]
    public async Task Start_ContractApprovalOnRollback_FailsWithoutCallingTheExecutor()
    {
        var client = new RecordingClient { Response = Ready(Digest('b')) };
        var operation = WithParameters(Operation(Digest('c'), "approver"), ComposeContractApproval.ToParameters(ApprovalNonce, [ContractScript]));

        var result = await Backend(client, Digest('a'), "/tmp/honua-compose-unit.sock").StartAsync(operation);

        Assert.Equal(WorkflowOperationStatus.Failed, result.Status);
        Assert.Equal(ComposeExecutorCodes.ConfigUnsupported, result.Message);
        Assert.Empty(client.Requests);
    }

    [UnitTest]
    public async Task Plan_ContractApproval_IsAcceptedOnlyWellFormedOnConvergence()
    {
        var release = Digest('a');
        var backend = Backend(new RecordingClient { Response = Ready(Digest('b')) }, release, "/tmp/honua-compose-unit.sock");
        var listed = ComposeContractApproval.ToParameters(ApprovalNonce, [ContractScript]);

        var convergence = await backend.PlanAsync(Spec(release, listed));
        var rollback = await backend.PlanAsync(Spec(Digest('c'), listed));
        var malformed = await backend.PlanAsync(Spec(release, ComposeContractApproval.ToParameters("not-hex!", [ContractScript])));
        var scriptless = await backend.PlanAsync(Spec(release, new Dictionary<string, string> { [ComposeContractApproval.NonceParameterKey] = ApprovalNonce }));

        Assert.Empty(convergence.BlockingReasons);
        Assert.Contains(ComposeExecutorCodes.ConfigUnsupported, rollback.BlockingReasons);
        Assert.Contains(ComposeExecutorCodes.ConfigUnsupported, malformed.BlockingReasons);
        Assert.Contains(ComposeExecutorCodes.ConfigUnsupported, scriptless.BlockingReasons);
    }

    private static ComposeExecutorOptions ContractOptions()
        => ValidOptions(Path.Join(Path.GetTempPath(), "honua-compose-unit", Guid.NewGuid().ToString("N")), Digest('a'));

    private static MigrateOnlyDocker RefusingThen(DockerProcessResult approvedRun)
    {
        var docker = new MigrateOnlyDocker(0, string.Empty);
        docker.RunResults.Enqueue(new DockerProcessResult(5, MigrateOnlySummary("contract-approval-required", 5, "contract-annotated"), string.Empty));
        docker.RunResults.Enqueue(approvedRun);
        return docker;
    }

    private static ComposeContractApproval Approval(string script, string nonce)
        => new() { Nonce = nonce, Scripts = [script] };

    private static WorkflowOperationRecord WithParameters(WorkflowOperationRecord operation, Dictionary<string, string> parameters)
        => operation with { Deploy = operation.Deploy! with { Parameters = parameters } };
}
