// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Observability.Abstractions;
using Honua.Core.Features.Observability.Domain;
using Honua.Db.Postgres.Features.Infrastructure;
using Npgsql;

namespace Honua.Infrastructure.Monitoring;

internal sealed record OpsAutonomySettingsChange(OpsAutonomySettings Settings, string ChangedBy, string? Reason);

internal sealed class OpsAutonomySettingsWriter(
    NpgsqlDataSource dataSource,
    IOpsAutonomyPolicyStore store,
    IOpsWatchdogKillSwitch watchdog)
{
    private static readonly TimeSpan CommittedStopTimeout = TimeSpan.FromSeconds(10);

    internal Task ReconcileAsync(OpsAutonomySettings configured, CancellationToken cancellationToken)
        => WithLockAsync(async () =>
        {
            var settings = await store.GetSettingsAsync(cancellationToken).ConfigureAwait(false);
            if (configured.KillSwitchEnabled || settings.KillSwitchEnabled)
            {
                await watchdog.SuspendAsync(cancellationToken).ConfigureAwait(false);
            }

            return settings;
        }, cancellationToken);

    internal Task<OpsAutonomySettings> SetAsync(OpsAutonomySettingsChange change, CancellationToken cancellationToken)
        => WithLockAsync(async () =>
        {
            if (!change.Settings.KillSwitchEnabled)
            {
                return await RestoreAsync(change, cancellationToken).ConfigureAwait(false);
            }

            OpsAutonomySettings settings;
            try
            {
                settings = await store.SetSettingsAsync(
                    change.Settings, change.ChangedBy, change.Reason, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception storeFailure) when (storeFailure is not OutOfMemoryException)
            {
                // The stop may have committed before the failure; stopping the watchdog is always safe.
                await ReassertWatchdogStopAsync([storeFailure]).ConfigureAwait(false);
                throw;
            }

            if (settings.KillSwitchEnabled)
            {
                await SuspendDetachedAsync().ConfigureAwait(false);
            }

            return settings;
        }, cancellationToken);

    private async Task SuspendDetachedAsync()
    {
        using var timeout = new CancellationTokenSource(CommittedStopTimeout);
        try
        {
            await watchdog.SuspendAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception) when (timeout.IsCancellationRequested)
        {
            throw new TimeoutException("The watchdog stop did not complete in time.", exception);
        }
    }

    private async Task<OpsAutonomySettings> RestoreAsync(
        OpsAutonomySettingsChange change, CancellationToken cancellationToken)
    {
        try
        {
            await watchdog.ResumeAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception resumeFailure) when (resumeFailure is not OutOfMemoryException)
        {
            await ReassertWatchdogStopAsync([resumeFailure]).ConfigureAwait(false);
            throw;
        }

        try
        {
            return await store.SetSettingsAsync(
                change.Settings, change.ChangedBy, change.Reason, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception restoreFailure) when (restoreFailure is not OutOfMemoryException)
        {
            await ReassertGlobalStopAsync(change, restoreFailure).ConfigureAwait(false);
            throw;
        }
    }

    private async Task ReassertGlobalStopAsync(OpsAutonomySettingsChange change, Exception restoreFailure)
    {
        var failures = new List<Exception> { restoreFailure };
        await TryCompensationAsync(async token =>
        {
            await store.SetSettingsAsync(change.Settings with { KillSwitchEnabled = true },
                change.ChangedBy, "Autonomy restore failed; retaining global stop", token).ConfigureAwait(false);
        }, failures).ConfigureAwait(false);
        await ReassertWatchdogStopAsync(failures).ConfigureAwait(false);
    }

    private async Task ReassertWatchdogStopAsync(List<Exception> failures)
    {
        await TryCompensationAsync(watchdog.SuspendAsync, failures).ConfigureAwait(false);
        if (failures.Count > 1)
        {
            throw new IOException("The autonomy change failed and the global stop could not be fully reasserted.",
                new AggregateException(failures));
        }
    }

    private static async Task TryCompensationAsync(
        Func<CancellationToken, Task> compensate, List<Exception> failures)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            await compensate(timeout.Token).ConfigureAwait(false);
        }
        catch (Exception failure) when (failure is not OutOfMemoryException)
        {
            failures.Add(failure);
        }
    }

    private async Task<T> WithLockAsync<T>(Func<Task<T>> action, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT pg_advisory_xact_lock(hashtext('honua:ops-autonomy-settings'))";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        var result = await action().ConfigureAwait(false);
        await transaction.CommitSafelyAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }
}
