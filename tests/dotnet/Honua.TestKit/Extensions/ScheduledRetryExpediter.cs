// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Diagnostics;
using System.Globalization;
using FluentAssertions;
using Honua.ControlPlane;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Npgsql;

namespace Honua.TestKit.Extensions;

// Drives a failing job through every retry its policy allows without sleeping through the backoff.
// The worker requeues a failed attempt with next_retry_at = now() + ComputeDelay(attempt + 1) and
// keeps the row invisible to claims until then (30 s, then 60 s, under the default policy). Each
// requeue's stored schedule is asserted against the policy; then that one row is made due now,
// the state the backoff would reach on its own. Claim, execution, failure accounting and the
// terminal transition stay with the real worker.
public sealed class ScheduledRetryExpediter
{
    private static readonly TimeSpan PollDelay = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan ScheduleTolerance = TimeSpan.FromSeconds(1);

    private readonly NpgsqlDataSource _dataSource;
    private readonly IExecutionJobStore _jobStore;
    private readonly string _jobs;

    public ScheduledRetryExpediter(NpgsqlDataSource dataSource, IExecutionJobStore jobStore, string schema = "honua")
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(jobStore);
        ArgumentException.ThrowIfNullOrWhiteSpace(schema);
        _dataSource = dataSource;
        _jobStore = jobStore;
        _jobs = $"{JobStoreDatabase.QuoteIdentifier(schema)}.{ExecutionJobRow.TableName}";
    }

    public async Task ExpediteAsync(string operationId, JobRetryPolicy retryPolicy, TimeSpan attemptBudget)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);
        ArgumentNullException.ThrowIfNull(retryPolicy);
        for (var attempt = 1; attempt < retryPolicy.MaxAttempts; attempt++)
        {
            var requeued = await WaitForScheduledRetryAsync(operationId, attempt, attemptBudget).ConfigureAwait(false);
            (requeued.NextRetryAt!.Value - requeued.UpdatedAt).Should().BeCloseTo(
                retryPolicy.ComputeDelay(attempt + 1),
                ScheduleTolerance,
                $"attempt {attempt} of job {operationId} must be requeued on the retry policy's backoff");
            await MakeRetryDueAsync(operationId, attempt, attemptBudget).ConfigureAwait(false);
        }
    }

    private async Task<ExecutionJobRecord> WaitForScheduledRetryAsync(string operationId, int attempt, TimeSpan budget)
    {
        var elapsed = Stopwatch.StartNew();
        ExecutionJobRecord? current = null;
        while (elapsed.Elapsed < budget)
        {
            current = await _jobStore.GetAsync(operationId).ConfigureAwait(false);
            if (current is { Status: ExecutionJobStatus.Queued, NextRetryAt: not null } && current.AttemptCount == attempt)
            {
                return current;
            }

            if (current is not null)
            {
                current.Status.IsTerminal().Should().BeFalse(
                    $"job {operationId} must be requeued after attempt {attempt}, not end as {current.Status}: {current.ErrorMessage}");
            }

            await Task.Delay(PollDelay).ConfigureAwait(false);
        }

        throw new TimeoutException(string.Create(
            CultureInfo.InvariantCulture,
            $"Job {operationId} was not requeued after attempt {attempt} within {budget.TotalSeconds:F0} s; " +
            $"last status={current?.Status}, attempts={current?.AttemptCount}, nextRetryAt={current?.NextRetryAt:O}."));
    }

    // The worker requeues in two writes: the job record, then the queue's own visibility update, which
    // checks no version. Making the retry due between them would be undone, so the update repeats until
    // a worker claims the attempt. The version bump makes a writer holding the old row re-read it.
    private async Task MakeRetryDueAsync(string operationId, int attempt, TimeSpan budget)
    {
        var elapsed = Stopwatch.StartNew();
        while (elapsed.Elapsed < budget && await TryMakeRetryDueAsync(operationId, attempt).ConfigureAwait(false))
        {
            await Task.Delay(PollDelay).ConfigureAwait(false);
        }
    }

    // Returns whether the attempt is still waiting to be claimed.
    private async Task<bool> TryMakeRetryDueAsync(string operationId, int attempt)
    {
        await using var command = _dataSource.CreateCommand($"""
            WITH due AS (
                UPDATE {_jobs}
                SET next_retry_at = now(), queue_visible_at = now(), version = version + 1
                WHERE operation_id = @operation_id AND status = '{nameof(ExecutionJobStatus.Queued)}'
                  AND attempt_count = @attempt AND queue_visible_at > now()
                RETURNING operation_id)
            SELECT EXISTS (
                SELECT 1 FROM {_jobs}
                WHERE operation_id = @operation_id AND status = '{nameof(ExecutionJobStatus.Queued)}'
                  AND attempt_count = @attempt)
            """);
        command.Parameters.AddWithValue("operation_id", operationId);
        command.Parameters.AddWithValue("attempt", attempt);
        return (bool)(await command.ExecuteScalarAsync().ConfigureAwait(false))!;
    }
}
