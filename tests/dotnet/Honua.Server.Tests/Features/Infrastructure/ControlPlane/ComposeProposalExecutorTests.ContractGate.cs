// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.ControlPlane;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.TestKit.Attributes;
using NSubstitute;
using Xunit;

namespace Honua.Server.Tests.Features.Infrastructure.ControlPlane;

// Converge migrates the candidate in migrate-only mode before starting it, so a contract
// migration that needs approval refuses with its own code instead of an unhealthy start (#8097).
public sealed partial class ComposeProposalExecutorTests
{
    private const string ContractScript = "Honua.Server.Migrations.183_RemoveUnusedFeatureIndexes.sql";

    [UnitTest]
    public async Task PrepareCandidate_ContractApprovalRequired_RestoresPriorAndRefusesWithItsCode()
    {
        var root = Path.Join(Path.GetTempPath(), "honua-compose-unit", Guid.NewGuid().ToString("N"));
        var options = ValidOptions(root, Digest('a'));
        var overridePath = Path.Join(options.ProjectDirectory, ComposeExecutorProtocol.OverrideFileName);
        var priorOverride = "services:\n  honua:\n    image: \"" + Digest('c') + "\"\n";
        await File.WriteAllTextAsync(overridePath, priorOverride);
        var docker = new MigrateOnlyDocker(5, MigrateOnlySummary("contract-approval-required", 5));
        using var controller = new ComposeProjectController(options, docker);

        var refusal = await controller.PrepareCandidateAsync(Digest('a'), CancellationToken.None);

        Assert.NotNull(refusal);
        Assert.False(refusal.Ok);
        Assert.Equal(ComposeExecutorCodes.ContractApprovalRequired, refusal.Code);
        Assert.Contains(ContractScript, refusal.Message, StringComparison.Ordinal);
        Assert.Equal(priorOverride, await File.ReadAllTextAsync(overridePath));
        Assert.Equal(["run", "start"], docker.Subcommands);
        Assert.DoesNotContain(docker.Argv, arguments => arguments.Contains("up"));
    }

    [UnitTest]
    public async Task PrepareCandidate_MigratedWithoutApproval_LeavesTheCandidateOverride()
    {
        var root = Path.Join(Path.GetTempPath(), "honua-compose-unit", Guid.NewGuid().ToString("N"));
        var options = ValidOptions(root, Digest('a'));
        var docker = new MigrateOnlyDocker(0, MigrateOnlySummary("success", 0));
        using var controller = new ComposeProjectController(options, docker);

        var refusal = await controller.PrepareCandidateAsync(Digest('a'), CancellationToken.None);

        Assert.Null(refusal);
        var run = Assert.Single(docker.Argv);
        Assert.Contains("HONUA_MIGRATE_ONLY=true", run);
        Assert.Contains("HONUA_APPROVE_CONTRACT_MIGRATIONS=", run);
        Assert.Equal("honua", run[^1]);
        var written = await File.ReadAllTextAsync(Path.Join(options.ProjectDirectory, ComposeExecutorProtocol.OverrideFileName));
        Assert.Contains(Digest('a'), written, StringComparison.Ordinal);
    }

    [UnitTest]
    public async Task PrepareCandidate_MigrationFailure_KeepsTheRecoveryPath()
    {
        var root = Path.Join(Path.GetTempPath(), "honua-compose-unit", Guid.NewGuid().ToString("N"));
        var options = ValidOptions(root, Digest('a'));
        var docker = new MigrateOnlyDocker(1, MigrateOnlySummary("migration-failed", 1));
        using var controller = new ComposeProjectController(options, docker);

        var refusal = await controller.PrepareCandidateAsync(Digest('a'), CancellationToken.None);

        Assert.NotNull(refusal);
        Assert.Equal(ComposeExecutorCodes.ReadbackIncomplete, refusal.Code);
        Assert.Equal(["run"], docker.Subcommands);
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

    private static string MigrateOnlySummary(string code, int exitCode)
        => "startup log\n" + System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["format"] = "honua.migrate-only/v1",
            ["code"] = code,
            ["exitCode"] = exitCode,
            ["schemaVerified"] = exitCode == 0,
            ["pendingScripts"] = new[]
            {
                new Dictionary<string, string> { ["script"] = ContractScript, ["classification"] = "contract-annotated" }
            },
        }) + "\n";

    private sealed class MigrateOnlyDocker(int runExitCode, string runStdout) : IDockerComposeProcess
    {
        public List<string[]> Argv { get; } = [];

        public List<string> Subcommands { get; } = [];

        public Task<DockerProcessResult> RunAsync(IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken)
        {
            var args = arguments.ToArray();
            Argv.Add(args);
            var subcommand = args.FirstOrDefault(argument => argument is "run" or "start" or "up") ?? "other";
            Subcommands.Add(subcommand);
            return Task.FromResult(subcommand == "run"
                ? new DockerProcessResult(runExitCode, runStdout, string.Empty)
                : new DockerProcessResult(0, string.Empty, string.Empty));
        }
    }
}
