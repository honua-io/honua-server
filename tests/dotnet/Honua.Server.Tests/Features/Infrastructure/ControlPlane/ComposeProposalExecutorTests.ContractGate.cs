// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.ControlPlane;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.TestKit.Attributes;
using NSubstitute;
using Xunit;

namespace Honua.Server.Tests.Features.Infrastructure.ControlPlane;

// Converge migrates the candidate in migrate-only mode before starting it, so a contract
// migration that needs approval refuses with its own code instead of an unhealthy start.
public sealed partial class ComposeProposalExecutorTests
{
    private const string ContractScript = "Honua.Server.Migrations.183_RemoveUnusedFeatureIndexes.sql";
    private const string ApprovalNonce = "0123456789abcdef";

    [UnitTest]
    public async Task PrepareCandidate_ContractApprovalRequired_RestoresPriorAndRefusesWithTheNonce()
    {
        var options = ValidOptions(Path.Join(Path.GetTempPath(), "honua-compose-unit", Guid.NewGuid().ToString("N")), Digest('a'));
        var overridePath = Path.Join(options.ProjectDirectory, ComposeExecutorProtocol.OverrideFileName);
        var priorOverride = "services:\n  honua:\n    image: \"" + Digest('c') + "\"\n";
        await File.WriteAllTextAsync(overridePath, priorOverride);
        var docker = new MigrateOnlyDocker(5, MigrateOnlySummary("contract-approval-required", 5, "contract-annotated"));
        using var controller = new ComposeProjectController(options, docker);

        var refusal = await controller.PrepareCandidateAsync(Digest('a'), CancellationToken.None);

        Assert.NotNull(refusal);
        Assert.False(refusal.Ok);
        Assert.Equal(ComposeExecutorCodes.ContractApprovalRequired, refusal.Code);
        Assert.Contains(ContractScript, refusal.Message, StringComparison.Ordinal);
        Assert.Contains("HONUA_APPROVE_CONTRACT_MIGRATIONS=" + ApprovalNonce, refusal.Message, StringComparison.Ordinal);
        Assert.Equal(priorOverride, await File.ReadAllTextAsync(overridePath));
        Assert.Equal(["run", "rm", "start"], docker.Subcommands);
    }

    [UnitTest]
    public async Task PrepareCandidate_RefusalWithoutPriorOverride_RemovesTheCandidateOverride()
    {
        var options = ValidOptions(Path.Join(Path.GetTempPath(), "honua-compose-unit", Guid.NewGuid().ToString("N")), Digest('a'));
        var docker = new MigrateOnlyDocker(5, MigrateOnlySummary("contract-approval-required", 5, "contract-annotated"));
        using var controller = new ComposeProjectController(options, docker);

        var refusal = await controller.PrepareCandidateAsync(Digest('a'), CancellationToken.None);

        Assert.Equal(ComposeExecutorCodes.ContractApprovalRequired, refusal?.Code);
        Assert.False(File.Exists(Path.Join(options.ProjectDirectory, ComposeExecutorProtocol.OverrideFileName)));
    }

    [UnitTest]
    public async Task PrepareCandidate_PriorDoesNotRestart_KeepsTheRecoveryPath()
    {
        var options = ValidOptions(Path.Join(Path.GetTempPath(), "honua-compose-unit", Guid.NewGuid().ToString("N")), Digest('a'));
        var docker = new MigrateOnlyDocker(5, MigrateOnlySummary("contract-approval-required", 5, "contract-annotated"), startExitCode: 1);
        using var controller = new ComposeProjectController(options, docker);

        var refusal = await controller.PrepareCandidateAsync(Digest('a'), CancellationToken.None);

        Assert.Equal(ComposeExecutorCodes.ReadbackIncomplete, refusal?.Code);
    }

    [UnitTest]
    public async Task PrepareCandidate_MigratedWithoutApproval_KeepsTheCandidateAndAnyOperatorApproval()
    {
        var options = ValidOptions(Path.Join(Path.GetTempPath(), "honua-compose-unit", Guid.NewGuid().ToString("N")), Digest('a'));
        var docker = new MigrateOnlyDocker(0, MigrateOnlySummary("success", 0, "expand"));
        using var controller = new ComposeProjectController(options, docker);

        var refusal = await controller.PrepareCandidateAsync(Digest('a'), CancellationToken.None);

        Assert.Null(refusal);
        var run = Assert.Single(docker.Argv);
        Assert.Contains("HONUA_MIGRATE_ONLY=true", run);
        Assert.DoesNotContain(run, argument => argument.StartsWith("HONUA_APPROVE_CONTRACT_MIGRATIONS", StringComparison.Ordinal));
        Assert.Equal("honua", run[^1]);
        var written = await File.ReadAllTextAsync(Path.Join(options.ProjectDirectory, ComposeExecutorProtocol.OverrideFileName));
        Assert.Contains(Digest('a'), written, StringComparison.Ordinal);
    }

    [UnitTheory]
    [InlineData("migration-failed", 10, "contract-annotated")]
    [InlineData("contract-approval-required", 5, "expand")]
    public async Task PrepareCandidate_OtherMigrationFailures_RemoveTheRunAndKeepRecovery(string code, int exitCode, string classification)
    {
        var options = ValidOptions(Path.Join(Path.GetTempPath(), "honua-compose-unit", Guid.NewGuid().ToString("N")), Digest('a'));
        var docker = new MigrateOnlyDocker(exitCode, MigrateOnlySummary(code, exitCode, classification));
        using var controller = new ComposeProjectController(options, docker);

        var refusal = await controller.PrepareCandidateAsync(Digest('a'), CancellationToken.None);

        Assert.Equal(ComposeExecutorCodes.ReadbackIncomplete, refusal?.Code);
        Assert.Equal(["run", "rm"], docker.Subcommands);
        var name = docker.Argv[0][Array.IndexOf(docker.Argv[0], "--name") + 1];
        Assert.Equal(name, docker.Argv[1][^1]);
    }

    [UnitTest]
    public async Task ContractGateRefusalWithBackup_FailsWithoutRecovery()
    {
        var release = Digest('a');
        var prior = Digest('b');
        var client = Substitute.For<IComposeExecutorClient>();
        client.ExecuteAsync(Arg.Is<ComposeExecutorRequest>(request => request.Op == "status"), Arg.Any<CancellationToken>())
            .Returns(Ready(prior));
        client.ExecuteAsync(Arg.Is<ComposeExecutorRequest>(request => request.Op == "converge"), Arg.Any<CancellationToken>())
            .Returns(new ComposeExecutorResponse
            {
                Ok = false, BackupId = "backup-" + new string('c', 32), Code = ComposeExecutorCodes.ContractApprovalRequired
            });

        var result = await Backend(client, release, "/tmp/honua-compose-unit.sock").StartAsync(Operation(release, "approver"));

        Assert.Equal(WorkflowOperationStatus.Failed, result.Status);
        Assert.Equal(ComposeExecutorCodes.ContractApprovalRequired, result.Message);
        Assert.Equal(prior, result.ObservedRevision);
    }

    private static string MigrateOnlySummary(string code, int exitCode, string classification)
        => "startup log\n" + System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["format"] = "honua.migrate-only/v1",
            ["code"] = code,
            ["exitCode"] = exitCode,
            ["schemaVerified"] = exitCode == 0,
            ["pendingScripts"] = new[]
            {
                new Dictionary<string, string> { ["script"] = ContractScript, ["classification"] = classification }
            },
            ["contractApproval"] = new Dictionary<string, string> { ["nonce"] = ApprovalNonce },
        }) + "\n";

    private sealed class MigrateOnlyDocker(int runExitCode, string runStdout, int startExitCode = 0) : IDockerComposeProcess
    {
        public List<string[]> Argv { get; } = [];

        public List<string> Subcommands { get; } = [];

        public Task<DockerProcessResult> RunAsync(IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken)
        {
            var args = arguments.ToArray();
            Argv.Add(args);
            var subcommand = args.FirstOrDefault(argument => argument is "run" or "start" or "up" or "rm") ?? "other";
            Subcommands.Add(subcommand);
            return Task.FromResult(subcommand switch
            {
                "run" => new DockerProcessResult(runExitCode, runStdout, string.Empty),
                "start" => new DockerProcessResult(startExitCode, string.Empty, string.Empty),
                _ => new DockerProcessResult(0, string.Empty, string.Empty),
            });
        }
    }
}
