// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Configuration;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Infrastructure.Migrations;
using Honua.Infrastructure.Monitoring;

namespace Honua.Server.Tests.Features.Infrastructure.Monitoring;

public sealed class MigrationBackupHookStatusMapperTests
{
    [Fact]
    public void Build_UnannotatedContractWithEnforcementDisabled_ReportsMatchingBackupHook()
    {
        const string ContractScript = "099_drop_legacy.sql";
        var plan = DatabaseMigrationPlan.Succeeded(
            pendingScripts: [ContractScript],
            pendingScriptClassifications:
            [
                new MigrationScriptClassification
                {
                    ScriptName = ContractScript,
                    Classification = MigrationSafetyClassification.ContractUnannotated,
                    BreakingRules = ["drop-column"]
                }
            ],
            journalIsNonEmpty: true);
        var options = new MigrationSafetyOptions
        {
            Enforce = false,
            BackupCommand = "pg_dump --format=custom"
        };
        var latest = new DatabaseMigrationBackupHookResult
        {
            Outcome = "succeeded",
            Succeeded = true,
            StartedAt = DateTimeOffset.UnixEpoch,
            CompletedAt = DateTimeOffset.UnixEpoch.AddSeconds(1),
            DurationMilliseconds = 1_000,
            ExitCode = 0,
            PendingContractScripts = [ContractScript],
            MigrationRunId = "migration-run",
            CorrelationId = "migration-run"
        };

        var status = MigrationBackupHookStatusMapper.Build(plan, options, latest);

        status.Should().NotBeNull();
        status!.RequiredForPendingSet.Should().BeTrue();
        status.RanForPendingSet.Should().BeTrue();
        status.Succeeded.Should().BeTrue();
        status.MigrationRunId.Should().Be("migration-run");
    }
}
