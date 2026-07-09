// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Configuration;
using Honua.Core.Features.Infrastructure.Domain;

namespace Honua.Infrastructure.Monitoring;

internal static class MigrationBackupHookStatusMapper
{
    public static MigrationBackupHookStatus? Build(
        DatabaseMigrationPlan plan,
        MigrationSafetyOptions options,
        DatabaseMigrationBackupHookResult? latest)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(options);

        if (!plan.Successful || !plan.JournalIsNonEmpty || !plan.HasContractScripts)
        {
            return null;
        }

        var pendingContractScripts = plan.ContractScriptNames;
        var configured = !string.IsNullOrWhiteSpace(options.BackupCommand);
        var required = configured && !plan.HasUnannotatedBreakingScripts;
        var matched = required &&
            latest is not null &&
            SamePendingSet(pendingContractScripts, latest.PendingContractScripts);

        return new MigrationBackupHookStatus
        {
            Configured = configured,
            RequiredForPendingSet = required,
            RanForPendingSet = matched,
            Succeeded = matched ? latest!.Succeeded : null,
            Outcome = matched ? latest!.Outcome : null,
            StartedAt = matched ? latest!.StartedAt : null,
            CompletedAt = matched ? latest!.CompletedAt : null,
            DurationMilliseconds = matched ? latest!.DurationMilliseconds : null,
            ExitCode = matched ? latest!.ExitCode : null,
            Stderr = matched ? latest!.Stderr : null,
            PendingContractScripts = pendingContractScripts,
            MigrationRunId = matched ? latest!.MigrationRunId : null,
            CorrelationId = matched ? latest!.CorrelationId : null
        };
    }

    private static bool SamePendingSet(
        IReadOnlyList<string> currentPending,
        IReadOnlyList<string> recordedPending)
    {
        if (currentPending.Count != recordedPending.Count)
        {
            return false;
        }

        for (var i = 0; i < currentPending.Count; i++)
        {
            if (!string.Equals(currentPending[i], recordedPending[i], StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }
}
